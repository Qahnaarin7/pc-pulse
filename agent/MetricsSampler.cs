using System.Diagnostics;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using PawnIoDriver = LibreHardwareMonitor.PawnIo.PawnIo;

sealed class MetricsSampler(bool sensorsEnabled) : IDisposable
{
    private readonly object overviewLock = new();
    private readonly SemaphoreSlim processLock = new(1);
    private readonly object sensorLock = new();
    private readonly object auxiliaryLock = new();
    private OverviewSample? overview;
    private long overviewAt;
    private CpuTimes? overviewCpu;
    private ProcessSnapshot? processSnapshot;
    private ProcessCounterSample? processCounters;
    private SensorSnapshot? sensorSnapshot;
    private long sensorsAt;
    private Computer? computer;
    private Computer? auxiliaryComputer;
    private long auxiliaryAt;
    private ComponentTemperatures? auxiliary;
    private Task? auxiliaryScan;
    private bool closing;
    private readonly bool elevated = IsElevated();
    private readonly Lazy<GigabyteCooling?> gigabyte = new(GigabyteCooling.Open);

    public OverviewResponse ReadOverview(bool includeSensors)
    {
        OverviewSample sample;
        lock (overviewLock)
        {
            if (overview is null || Stopwatch.GetElapsedTime(overviewAt).TotalSeconds >= 2)
            {
                var current = WindowsCounters.ReadCpu();
                double? cpuPercent = null;
                if (current is { } fresh && overviewCpu is { } previous &&
                    Stopwatch.GetElapsedTime(overviewAt).TotalSeconds < 45 &&
                    fresh.Total > previous.Total && fresh.Idle >= previous.Idle)
                {
                    var idle = fresh.Idle - previous.Idle;
                    var total = fresh.Total - previous.Total;
                    cpuPercent = Percent(100 * (1 - (double)idle / total));
                }

                overviewCpu = current;
                overview = new OverviewSample(DateTimeOffset.UtcNow, cpuPercent, WindowsCounters.ReadMemory());
                overviewAt = Stopwatch.GetTimestamp();
            }
            sample = overview;
        }

        var sensors = includeSensors && sensorsEnabled ? ReadSensors() : null;
        return new OverviewResponse(
            Environment.MachineName, sample.SampledAt, sample.CpuPercent, sample.Memory,
            sensors?.CpuTemperatureC, sensors?.Gpus ?? [], sensors?.SampledAt, sensors is not null,
            CpuTemperatureStatus(sensors), sensors?.CpuTemperatureSource, sensors?.Cooling,
            includeSensors && sensorsEnabled ? ReadAuxiliary() : null);
    }

    public TemperatureResponse ReadTemperatures()
    {
        var sensors = sensorsEnabled ? ReadSensors() : null;
        return new TemperatureResponse(sensors?.SampledAt, sensors?.CpuTemperatureC, sensors?.Gpus ?? [],
            CpuTemperatureStatus(sensors));
    }

    public CoolingResponse? SetCoolingMode(bool maximum)
    {
        if (!sensorsEnabled)
            return null;

        lock (sensorLock)
        {
            var result = gigabyte.Value?.SetMode(maximum);
            sensorSnapshot = null;
            return result;
        }
    }

    public async Task<ProcessSnapshot> ReadProcessesAsync(CancellationToken cancellationToken)
    {
        await processLock.WaitAsync(cancellationToken);
        try
        {
            var previous = processCounters;
            if (processSnapshot is not null && previous is not null &&
                Stopwatch.GetElapsedTime(previous.At).TotalSeconds < 4)
                return processSnapshot;

            if (previous is null || Stopwatch.GetElapsedTime(previous.At).TotalSeconds >= 60)
            {
                processSnapshot = null;
                processCounters = previous = ReadProcessCounters();
            }
            var warmup = TimeSpan.FromMilliseconds(850) - Stopwatch.GetElapsedTime(previous.At);
            if (warmup > TimeSpan.Zero)
                await Task.Delay(warmup, cancellationToken);

            var current = ReadProcessCounters();
            var delta = current.Total is { } fresh && previous.Total is { } old && fresh > old ? fresh - old : 0;
            var entries = new List<ProcessEntry>(current.Processes.Count);
            foreach (var (pid, info) in current.Processes)
            {
                double? cpu = null;
                if (delta != 0 && info.Time is { } now &&
                    previous.Processes.TryGetValue(pid, out var item) && item.Time is { } then &&
                    now.Created == then.Created && now.Total >= then.Total)
                    cpu = Percent(100d * (now.Total - then.Total) / delta);
                entries.Add(new ProcessEntry(pid, info.Name, info.WorkingSet, cpu));
            }
            processCounters = current;
            processSnapshot = new ProcessSnapshot(DateTimeOffset.UtcNow, entries,
                delta > 0 ? Stopwatch.GetElapsedTime(previous.At, current.At).TotalSeconds : null);
            return processSnapshot;
        }
        finally
        {
            processLock.Release();
        }
    }

    private static ProcessCounterSample ReadProcessCounters()
    {
        var at = Stopwatch.GetTimestamp();
        var total = WindowsCounters.ReadCpu()?.Total;
        var entries = new Dictionary<int, ProcessCounter>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == 0)
                    continue;
                try
                {
                    entries.Add(process.Id, new ProcessCounter(process.ProcessName, process.WorkingSet64,
                        WindowsCounters.ReadProcess(process.Id)));
                }
                catch (InvalidOperationException)
                {
                    // A process can exit while the snapshot is being built.
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Windows may restrict individual system processes.
                }
            }
        }
        return new ProcessCounterSample(at, total, entries);
    }

    private ComponentTemperatures? ReadAuxiliary()
    {
        lock (auxiliaryLock)
        {
            if (!closing && (auxiliaryScan is null || auxiliaryScan.IsCompleted) &&
                (auxiliaryAt == 0 || Stopwatch.GetElapsedTime(auxiliaryAt).TotalSeconds >= 60))
            {
                auxiliaryAt = Stopwatch.GetTimestamp();
                auxiliaryScan = Task.Run(RefreshAuxiliary);
            }
            return auxiliary;
        }
    }

    private void RefreshAuxiliary()
    {
        var memory = new List<ComponentTemperature>();
        var disks = new List<ComponentTemperature>();
        try
        {
            if (auxiliaryComputer is null)
            {
                var candidate = new Computer { IsStorageEnabled = true, IsMemoryEnabled = true };
                try
                {
                    candidate.Open();
                    auxiliaryComputer = candidate;
                }
                catch
                {
                    candidate.Close();
                    throw;
                }
            }
            foreach (var hardware in auxiliaryComputer.Hardware)
                ScanComponent(hardware, memory, disks);
        }
        catch (Exception)
        {
            // The Windows counters and other sensor groups remain available.
        }
        var matched = new HashSet<int>();
        var libraryDiskCount = disks.Count;
        foreach (var disk in WindowsStorage.Temperatures())
        {
            var match = disk.Model.Length == 0 ? -1 : Enumerable.Range(0, libraryDiskCount)
                .FirstOrDefault(index => !matched.Contains(index) &&
                    disks[index].Name.Contains(disk.Model, StringComparison.OrdinalIgnoreCase), -1);
            if (match >= 0)
                matched.Add(match);
            else
                disks.Add(new ComponentTemperature(disk.Model.Length > 0 ? $"Диск {disk.Index} · {disk.Model}"
                    : $"Диск {disk.Index}", disk.TemperatureC));
        }
        lock (auxiliaryLock)
            auxiliary = new ComponentTemperatures(DateTimeOffset.UtcNow, memory, disks);
    }

    private static void ScanComponent(IHardware hardware, List<ComponentTemperature> memory,
        List<ComponentTemperature> disks)
    {
        try
        {
            var type = hardware.HardwareType;
            if (type is HardwareType.Memory or HardwareType.Storage)
            {
                hardware.Update();
                var candidates = hardware.Sensors.Where(sensor => sensor.SensorType == SensorType.Temperature &&
                    !sensor.Name.Contains("limit", StringComparison.OrdinalIgnoreCase) &&
                    !sensor.Name.Contains("threshold", StringComparison.OrdinalIgnoreCase));
                var actual = candidates.OrderBy(sensor => sensor.Name.Equals("Temperature", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .Select(sensor => ValidSensorValue(sensor, -20, 125)).FirstOrDefault(value => value.HasValue);
                if (actual.HasValue)
                    (type == HardwareType.Memory ? memory : disks).Add(new ComponentTemperature(hardware.Name, actual.Value));
            }
        }
        catch (Exception)
        {
            // Disk SMART and DIMM sensors are optional.
        }
        foreach (var child in hardware.SubHardware)
            ScanComponent(child, memory, disks);
    }

    private SensorSnapshot ReadSensors()
    {
        lock (sensorLock)
        {
            if (sensorSnapshot is not null && Stopwatch.GetElapsedTime(sensorsAt).TotalSeconds < 15)
                return sensorSnapshot;

            float? cpu = null;
            string? cpuSource = null;
            var gpus = new List<GpuEntry>();
            try
            {
                if (computer is null)
                {
                    var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
                    var newComputer = new Computer
                    {
                        IsCpuEnabled = architecture is System.Runtime.InteropServices.Architecture.X86
                            or System.Runtime.InteropServices.Architecture.X64,
                        IsGpuEnabled = true
                    };
                    try
                    {
                        newComputer.Open();
                        computer = newComputer;
                    }
                    catch
                    {
                        newComputer.Close();
                        throw;
                    }
                }

                foreach (var hardware in computer.Hardware)
                {
                    try
                    {
                        hardware.Update();
                        if (hardware.HardwareType == HardwareType.Cpu)
                        {
                            var temperatures = hardware.Sensors
                                .Where(sensor => sensor.SensorType == SensorType.Temperature)
                                .ToArray();
                            var package = temperatures
                                .Where(sensor => sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                                    sensor.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase) ||
                                    sensor.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase))
                                .Select(sensor => ValidSensorValue(sensor, -30, 160)).Max();
                            var temperature = package ??
                                temperatures.Select(sensor => ValidSensorValue(sensor, -30, 160)).Max();
                            if (temperature is { } value)
                                cpu = cpu is { } previous ? Math.Max(previous, value) : value;
                        }
                        else if (hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd
                            or HardwareType.GpuIntel)
                        {
                            var temperature = GpuCore(hardware, SensorType.Temperature, -30, 160)
                                ?? hardware.Sensors.Where(sensor => sensor.SensorType == SensorType.Temperature &&
                                    sensor.Name.Equals("GPU Temperature", StringComparison.OrdinalIgnoreCase))
                                    .Select(sensor => ValidSensorValue(sensor, -30, 160)).FirstOrDefault();
                            var load = GpuCore(hardware, SensorType.Load, 0, 100);
                            gpus.Add(new GpuEntry(hardware.Name, temperature, load));
                        }
                    }
                    catch (Exception)
                    {
                        // An unavailable device must not prevent Windows metrics or other sensors.
                    }
                }
            }
            catch (Exception)
            {
                // Hardware monitoring is optional on systems without compatible sensors.
            }

            GigabyteTelemetry? vendor = null;
            try
            {
                vendor = gigabyte.Value?.Read();
            }
            catch (Exception)
            {
                // A vendor driver can be absent or unavailable.
            }
            if (cpu.HasValue)
                cpuSource = "hardware_monitor";
            else if (vendor is not null)
            {
                cpu = vendor.CpuTemperatureC;
                cpuSource = "gigabyte_control_center";
            }

            sensorsAt = Stopwatch.GetTimestamp();
            sensorSnapshot = new SensorSnapshot(DateTimeOffset.UtcNow, cpu, cpuSource, gpus, vendor?.Cooling);
            return sensorSnapshot;
        }
    }

    private static float? GpuCore(IHardware hardware, SensorType type, float minimum, float maximum)
    {
        return hardware.Sensors
            .Where(sensor => sensor.SensorType == type &&
                (sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) ||
                 sensor.Name.Equals("GPU Total", StringComparison.OrdinalIgnoreCase) ||
                 sensor.Name.Equals("D3D 3D", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(sensor => sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) ? 0
                : sensor.Name.Equals("GPU Total", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .Select(sensor => ValidSensorValue(sensor, minimum, maximum))
            .FirstOrDefault(value => value.HasValue);
    }

    private static float? ValidSensorValue(ISensor sensor, float minimum, float maximum) =>
        sensor.Value is { } value && float.IsFinite(value) && value >= minimum && value <= maximum
            ? MathF.Round(value, 1) : null;

    private static double Percent(double value) => Math.Round(Math.Clamp(value, 0, 100), 1);

    private string CpuTemperatureStatus(SensorSnapshot? sample)
    {
        if (sample is null)
            return "off";
        if (sample.CpuTemperatureC.HasValue)
            return "ok";
        if (!PawnIoDriver.IsInstalled)
            return "driver_required";
        return elevated ? "unavailable" : "administrator_required";
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public void Dispose()
    {
        lock (sensorLock)
        {
            computer?.Close();
            if (gigabyte.IsValueCreated)
                gigabyte.Value?.Dispose();
        }
        Task? scan;
        lock (auxiliaryLock)
        {
            closing = true;
            scan = auxiliaryScan;
        }
        scan?.GetAwaiter().GetResult();
        auxiliaryComputer?.Close();
    }

    private sealed record OverviewSample(DateTimeOffset SampledAt, double? CpuPercent, MemoryEntry? Memory);
    private sealed record SensorSnapshot(DateTimeOffset SampledAt, float? CpuTemperatureC,
        string? CpuTemperatureSource, List<GpuEntry> Gpus, CoolingResponse? Cooling);
    private sealed record ProcessCounter(string Name, long WorkingSet, ProcessTimes? Time);
    private sealed record ProcessCounterSample(long At, ulong? Total, Dictionary<int, ProcessCounter> Processes);
}

sealed record OverviewResponse(string Host, DateTimeOffset SampledAt, double? CpuPercent,
    MemoryEntry? Memory, float? CpuTemperatureC, List<GpuEntry> Gpus,
    DateTimeOffset? SensorsSampledAt, bool SensorsEnabled, string CpuTemperatureStatus,
    string? CpuTemperatureSource, CoolingResponse? Cooling, ComponentTemperatures? ComponentTemperatures);
sealed record ComponentTemperatures(DateTimeOffset SampledAt, List<ComponentTemperature> Memory,
    List<ComponentTemperature> Disks);
sealed record ComponentTemperature(string Name, float TemperatureC);
sealed record TemperatureResponse(DateTimeOffset? SampledAt, float? CpuTemperatureC,
    List<GpuEntry> Gpus, string CpuTemperatureStatus);
sealed record MemoryEntry(ulong UsedBytes, ulong TotalBytes);
sealed record GpuEntry(string Name, float? TemperatureC, float? LoadPercent);
sealed record ProcessEntry(int Pid, string Name, long WorkingSetBytes, double? CpuPercent);
sealed record ProcessSnapshot(DateTimeOffset SampledAt, List<ProcessEntry> Processes, double? CpuWindowSeconds);
