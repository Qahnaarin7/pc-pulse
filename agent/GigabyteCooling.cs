using System.Runtime.InteropServices;
using Microsoft.Win32;

sealed class GigabyteCooling : IDisposable
{
    private const int ReadTelemetryCommand = 12;
    private const int ReadCapabilitiesCommand = 13;
    private const int SetFanModeCommand = 121;
    private const int SetKeyboardColorCommand = 103;
    private const int SettingReadSucceeded = 4096;
    private const int FanModePage = 4;
    private const int FanModeOffset = 5;
    private const int PowerModePage = 1;
    private const int PowerModeOffset = 1;
    private readonly nint library;
    private readonly ReadBuffer readBuffer;
    private readonly ReadSetting readSetting;
    private readonly WriteCommand writeCommand;
    public bool SupportsKeyboard { get; private set; }

    private GigabyteCooling(nint library)
    {
        this.library = library;
        readBuffer = Resolve<ReadBuffer>("GetDCHU_Data_Buffer");
        readSetting = Resolve<ReadSetting>("ReadAppSettings");
        writeCommand = Resolve<WriteCommand>("SetDCHU_Data");
    }

    public static GigabyteCooling? Open()
    {
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        if (!string.Equals(bios?.GetValue("SystemManufacturer") as string, "GIGABYTE", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(bios?.GetValue("SystemProductName") as string, "G6X9KG", StringComparison.OrdinalIgnoreCase))
            return null;

        using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\CCDCHUService");
        if (service?.GetValue("ImagePath") is not string servicePath)
            return null;

        var driverStore = Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository");
        var driverFolder = Path.GetDirectoryName(Path.GetFullPath(servicePath.Trim('"')));
        if (driverFolder is null ||
            !string.Equals(Path.GetDirectoryName(driverFolder), driverStore, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(driverFolder).StartsWith("acpibridge1.inf_amd64_", StringComparison.OrdinalIgnoreCase))
            return null;

        var vendorLibrary = Path.Combine(driverFolder, "InsydeDCHU.dll");
        const uint SearchDllDirectory = 0x100;
        const uint SearchSystem32 = 0x800;
        var handle = LoadLibraryEx(vendorLibrary, 0, SearchDllDirectory | SearchSystem32);
        if (handle == 0)
            return null;

        var accepted = false;
        try
        {
            var cooling = new GigabyteCooling(handle);
            var capabilities = new byte[256];
            accepted = cooling.readBuffer(ReadCapabilitiesCommand, capabilities) == ReadCapabilitiesCommand &&
                capabilities[12] == 2;
            cooling.SupportsKeyboard = accepted && capabilities[15] == 6;
            return accepted ? cooling : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (!accepted)
                NativeLibrary.Free(handle);
        }
    }

    public GigabyteTelemetry? Read()
    {
        var data = new byte[256];
        if (readBuffer(ReadTelemetryCommand, data) != ReadTelemetryCommand || data[18] is < 10 or > 120)
            return null;

        var savedMode = ReadSettingByte(FanModePage, FanModeOffset);
        var power = ReadSettingByte(PowerModePage, PowerModeOffset);
        var mode = data[26] switch
        {
            16 when savedMode is 0 or 1 => "maximum",
            2 when savedMode is 0 or 1 => "automatic",
            _ when savedMode == 6 => "custom",
            _ => "unknown"
        };
        return new GigabyteTelemetry(data[18], new CoolingResponse(
            mode, (mode is "automatic" or "maximum") && (power is 0 or 2 or 3 or 4),
            FanRpm(data, 2), FanRpm(data, 4)));
    }

    public CoolingResponse? SetMode(bool maximum)
    {
        if (Read()?.Cooling is not { CanControl: true } current)
            return null;

        var mode = maximum ? "maximum" : "automatic";
        if (current.Mode == mode)
            return current;

        // The OEM fan-mode command permits firmware Auto and Full Speed.
        var data = new byte[] { maximum ? (byte)1 : (byte)0, 0, 0, 1 };
        if (writeCommand(SetFanModeCommand, data, data.Length) != SetFanModeCommand)
            return null;

        var applied = Read()?.Cooling;
        if (applied?.Mode != mode)
        {
            Thread.Sleep(150);
            applied = Read()?.Cooling;
        }
        return applied?.Mode == mode ? applied : null;
    }

    public bool SetKeyboardColor(KeyboardColor color)
    {
        if (!SupportsKeyboard)
            return false;
        // The one-zone OEM protocol uses half-scale components in G, R, B order.
        var payload = new[] { (byte)(color.Green / 2), (byte)(color.Red / 2), (byte)(color.Blue / 2), (byte)0xF0 };
        return writeCommand(SetKeyboardColorCommand, payload, payload.Length) == SetKeyboardColorCommand;
    }

    public bool RestoreKeyboardColor()
    {
        var saved = new byte[3];
        return SupportsKeyboard && readSetting(2, 81, saved.Length, saved) == SettingReadSucceeded &&
            SetKeyboardColor(new KeyboardColor(saved[0], saved[1], saved[2]));
    }

    private byte? ReadSettingByte(int page, int offset)
    {
        var value = new byte[] { 255 };
        return readSetting(page, offset, value.Length, value) == SettingReadSucceeded && value[0] != 255
            ? value[0] : null;
    }

    private static int? FanRpm(byte[] data, int offset)
    {
        var period = (data[offset] << 8) | data[offset + 1];
        var rpm = period == 0 ? 0 : (int)Math.Round(2_156_250d / period);
        return rpm <= 15_000 ? rpm : null;
    }

    private T Resolve<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    public void Dispose() => NativeLibrary.Free(library);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ReadBuffer(int command, [Out] byte[] data);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ReadSetting(int page, int offset, int length, [Out] byte[] data);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int WriteCommand(int command, [In] byte[] data, int length);

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern nint LoadLibraryEx(string path, nint file, uint flags);
}

sealed record GigabyteTelemetry(float CpuTemperatureC, CoolingResponse Cooling);
sealed record CoolingResponse(string Mode, bool CanControl, int? CpuFanRpm, int? GpuFanRpm);
readonly record struct KeyboardColor(byte Red, byte Green, byte Blue);
