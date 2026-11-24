using System.Diagnostics;

sealed record ProtectionSettings(bool Enabled, int CpuCritical, int GpuCritical)
{
    public static ProtectionSettings Default { get; } = new(false, 95, 85);
    public bool IsValid => CpuCritical is >= 70 and <= 100 && GpuCritical is >= 60 and <= 95;
}

enum FanRequest { None, Maximum, Automatic }

sealed class CoolingPolicy
{
    private static readonly TimeSpan RecoveryTime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumSampleGap = TimeSpan.FromSeconds(6);
    private const int RecoveryGap = 5;
    private readonly HashSet<string> triggeredGpus = new(StringComparer.Ordinal);
    private bool triggeredCpu;
    private long? cooledAt;
    private long? lastSample;
    public bool OwnsMaximum { get; private set; }
    public bool Critical { get; private set; }
    public string State { get; private set; } = "waiting";

    public FanRequest Evaluate(ProtectionSettings limits, TemperatureResponse reading, long timestamp)
    {
        if (lastSample is { } last && Stopwatch.GetElapsedTime(last, timestamp) > MaximumSampleGap)
            cooledAt = null;
        lastSample = timestamp;
        var cooling = reading.Cooling;
        var cpu = reading.CpuTemperatureC;
        var hasReading = cpu.HasValue || reading.Gpus.Any(gpu => gpu.TemperatureC.HasValue);
        var cpuHot = cpu >= limits.CpuCritical;
        var gpuHot = false;
        foreach (var gpu in reading.Gpus)
        {
            if (gpu.TemperatureC >= limits.GpuCritical)
            {
                gpuHot = true;
                triggeredGpus.Add(gpu.Name);
            }
        }
        triggeredCpu |= cpuHot;
        Critical = cpuHot || gpuHot;
        if (Critical || !hasReading)
            cooledAt = null;

        // A missing fan reading never relinquishes an automatic Full Speed selection.
        if (cooling is { Mode: "automatic" or "custom" })
            OwnsMaximum = false;
        if (Critical)
        {
            State = cooling?.Mode == "maximum" ? (OwnsMaximum ? "maximum" : "manualMaximum") : "critical";
            return cooling is { CanControl: true, Mode: "automatic" } ? FanRequest.Maximum : FanRequest.None;
        }

        if (!OwnsMaximum)
        {
            ResetTriggers();
            State = !hasReading ? "waiting" : cooling?.CanControl == true ? "watching" : "restricted";
            return FanRequest.None;
        }

        var canRecover =
            hasReading &&
            (cpu.HasValue ? cpu.Value <= limits.CpuCritical - RecoveryGap : !triggeredCpu) &&
            reading.Gpus.All(gpu => gpu.TemperatureC is null || gpu.TemperatureC <= limits.GpuCritical - RecoveryGap) &&
            triggeredGpus.All(name => reading.Gpus.Any(gpu => gpu.Name == name && gpu.TemperatureC.HasValue));

        if (!canRecover)
            cooledAt = null;
        else
            cooledAt ??= timestamp;
        State = canRecover ? "recovering" : "maximum";
        return cooling is { CanControl: true, Mode: "maximum" } && cooledAt is { } start &&
            Stopwatch.GetElapsedTime(start, timestamp) >= RecoveryTime ? FanRequest.Automatic : FanRequest.None;
    }

    public void Applied(FanRequest request, bool confirmed)
    {
        if (!confirmed)
        {
            State = "unconfirmed";
            return;
        }
        OwnsMaximum = request == FanRequest.Maximum;
        State = OwnsMaximum ? "maximum" : "watching";
        if (!OwnsMaximum)
            ResetTriggers();
    }

    public void ManualModeConfirmed()
    {
        OwnsMaximum = false;
        ResetTriggers();
    }

    public void SensorUnavailable()
    {
        ResetRecovery();
        State = OwnsMaximum ? "maximum" : "waiting";
    }

    public void ResetRecovery()
    {
        cooledAt = null;
        lastSample = null;
    }

    private void ResetTriggers()
    {
        triggeredCpu = false;
        triggeredGpus.Clear();
        cooledAt = null;
    }
}
