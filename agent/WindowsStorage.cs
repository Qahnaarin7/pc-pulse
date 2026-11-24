using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

static class WindowsStorage
{
    private const uint QueryStorageProperty = 0x2D1400;
    private const uint StorageDeviceProperty = 0;
    private const uint StorageAdapterTemperatureProperty = 51;
    private const uint StorageDeviceTemperatureProperty = 52;
    private const string PhysicalDrive = "PhysicalDrive";

    public static IReadOnlyList<StorageReading> Temperatures()
    {
        var names = new char[32768];
        var length = QueryDosDevice(null, names, names.Length);
        if (length == 0 || length > names.Length)
            return [];
        var result = new List<StorageReading>();
        foreach (var name in new string(names, 0, (int)length).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!name.StartsWith(PhysicalDrive, StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(name.AsSpan(PhysicalDrive.Length), out var index))
                continue;

            using var handle = CreateFile(@"\\.\" + name, 0, 3, 0, 3, 0, 0);
            if (handle.IsInvalid)
                continue;
            var measurement = ReadTemperature(handle, StorageDeviceTemperatureProperty) ??
                ReadTemperature(handle, StorageAdapterTemperatureProperty);
            if (measurement.HasValue)
                result.Add(new StorageReading(index, ReadModel(handle), measurement.Value));
        }
        return result.OrderBy(item => item.Index).ToArray();
    }

    private static short? ReadTemperature(SafeFileHandle device, uint property)
    {
        var data = Query(device, property);
        const int descriptorHeaderSize = 24;
        const int temperatureInfoSize = 16;
        if (data.Length < descriptorHeaderSize + temperatureInfoSize)
            return null;

        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12));
        var available = (data.Length - descriptorHeaderSize) / temperatureInfoSize;
        for (var index = 0; index < Math.Min(count, available); index++)
        {
            var offset = descriptorHeaderSize + index * temperatureInfoSize + 2;
            var temperature = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset));
            if (temperature is >= -20 and <= 125)
                return temperature;
        }
        return null;
    }

    private static string ReadModel(SafeFileHandle device)
    {
        var data = Query(device, StorageDeviceProperty);
        if (data.Length < 20)
            return "";
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        if (offset == 0 || offset >= data.Length)
            return "";
        var model = data.AsSpan((int)offset);
        var end = model.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? model : model[..end]).Trim();
    }

    private static byte[] Query(SafeFileHandle device, uint property)
    {
        var input = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(input, property);
        var response = new byte[4096];
        if (!DeviceIoControl(device, QueryStorageProperty, input, input.Length,
                response, response.Length, out var bytes, 0) || bytes > response.Length)
            return [];
        return response[..(int)bytes];
    }

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint QueryDosDevice(string? deviceName, [Out] char[] target, int capacity);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security,
        uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize,
        [Out] byte[] output, int outputSize, out uint returned, nint overlapped);
}

sealed record StorageReading(int Index, string Model, float TemperatureC);
