using System.Diagnostics;
using System.Text.Json;

sealed record ProtectionStatus(bool Supported, bool Enabled, int CpuCritical, int GpuCritical, string State);

sealed class ThermalProtection : IAsyncDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private readonly MetricsSampler sampler;
    private readonly string path = Path.Combine(PairingIdentity.StateDirectory, "thermal-protection.json");
    private readonly object gate = new();
    private readonly SemaphoreSlim settingsChanged = new(0, 1);
    private readonly CancellationTokenSource stopping = new();
    private readonly Task monitor;
    private readonly KeyboardTemperatureIndicator keyboard;
    private ProtectionSettings settings;
    private CoolingPolicy policy = new();

    public ThermalProtection(MetricsSampler sampler)
    {
        this.sampler = sampler;
        keyboard = new KeyboardTemperatureIndicator(sampler);
        settings = Load();
        monitor = Task.Run(MonitorAsync);
    }

    public KeyboardStatus GetKeyboardStatus()
    {
        lock (gate) return keyboard.Read();
    }

    public KeyboardStatus? ConfigureKeyboard(KeyboardThresholds requested)
    {
        lock (gate)
        {
            var result = keyboard.Configure(requested);
            if (result is not null && settingsChanged.CurrentCount == 0)
                settingsChanged.Release();
            return result;
        }
    }

    public ProtectionStatus GetStatus()
    {
        lock (gate)
        {
            var supported = sampler.SupportsCooling();
            return new ProtectionStatus(supported, settings.Enabled && supported, settings.CpuCritical,
                settings.GpuCritical, !supported ? "unsupported" : settings.Enabled ? policy.State : "off");
        }
    }

    public ProtectionStatus? Configure(ProtectionSettings requested)
    {
        if (!requested.IsValid || requested.Enabled && !sampler.SupportsCooling())
            return null;
        lock (gate)
        {
            if (settings == requested)
                return GetStatus();
            var previous = settings;
            Save(requested);
            settings = requested;
            if (previous.Enabled && !requested.Enabled)
                policy = new CoolingPolicy();
            else
                policy.ResetRecovery();
            if (settingsChanged.CurrentCount == 0)
                settingsChanged.Release();
            return GetStatus();
        }
    }

    public CoolingResponse? SetManualMode(bool maximum)
    {
        lock (gate)
        {
            if (!maximum && settings.Enabled)
            {
                if (policy.Critical || policy.OwnsMaximum)
                    return null;
                var reading = sampler.ReadTemperatures();
                if (reading.CpuTemperatureC >= settings.CpuCritical ||
                    reading.Gpus.Any(gpu => gpu.TemperatureC >= settings.GpuCritical))
                    return null;
            }
            var cooling = sampler.SetCoolingMode(maximum);
            if (cooling is not null)
                policy.ManualModeConfirmed();
            return cooling;
        }
    }

    private async Task MonitorAsync()
    {
        var token = stopping.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                bool active;
                lock (gate) active = settings.Enabled && sampler.SupportsCooling() || keyboard.Enabled;
                if (active)
                {
                    Sample(token);
                    await settingsChanged.WaitAsync(Interval, token);
                }
                else
                {
                    await settingsChanged.WaitAsync(token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void Sample(CancellationToken token)
    {
        try
        {
            var reading = sampler.ReadTemperatures();
            lock (gate)
            {
                if (token.IsCancellationRequested)
                    return;
                if (reading.SampledAt is not { } timestamp ||
                    (DateTimeOffset.UtcNow - timestamp).Duration() > Interval * 2)
                {
                    policy.SensorUnavailable();
                    return;
                }
                if (settings.Enabled)
                {
                    var request = policy.Evaluate(settings, reading, Stopwatch.GetTimestamp());
                    if (request != FanRequest.None)
                    {
                        var result = sampler.SetCoolingMode(request == FanRequest.Maximum);
                        policy.Applied(request, result is not null);
                    }
                }
                if (keyboard.Enabled)
                    keyboard.Apply(reading);
            }
        }
        catch (Exception)
        {
            lock (gate) policy.SensorUnavailable();
        }
    }

    private ProtectionSettings Load()
    {
        try
        {
            var saved = JsonSerializer.Deserialize<ProtectionSettings>(File.ReadAllBytes(path));
            return saved is { IsValid: true } ? saved : ProtectionSettings.Default;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return ProtectionSettings.Default;
        }
    }

    private void Save(ProtectionSettings values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(values));
            File.Move(temporary, path, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        await monitor;
        try { keyboard.Restore(); }
        catch (Exception) { }
        settingsChanged.Dispose();
        stopping.Dispose();
    }
}
