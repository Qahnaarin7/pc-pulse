using System.Diagnostics;
using System.Text.Json;

sealed record KeyboardThresholds(bool Enabled, int CpuWarm, int CpuHot, int GpuWarm, int GpuHot)
{
    internal static readonly KeyboardThresholds Default = new(false, 85, 95, 80, 90);
    public bool IsValid => CpuWarm >= 35 && CpuWarm < CpuHot && CpuHot <= 100 &&
        GpuWarm >= 35 && GpuWarm < GpuHot && GpuHot <= 95;
}

sealed record KeyboardStatus(bool Supported, KeyboardThresholds Settings, string State);

sealed class KeyboardTemperatureIndicator
{
    private const int Hysteresis = 3;
    private readonly MetricsSampler sampler;
    private readonly string path = Path.Combine(PairingIdentity.StateDirectory, "keyboard-temperature.json");
    private KeyboardThresholds settings;
    private int applied = -1;
    private int attempted = -1;
    private long attemptedAt;

    public KeyboardTemperatureIndicator(MetricsSampler sampler)
    {
        this.sampler = sampler;
        try
        {
            var previous = JsonSerializer.Deserialize<KeyboardThresholds>(File.ReadAllBytes(path));
            settings = previous is { IsValid: true } ? previous : KeyboardThresholds.Default;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            settings = KeyboardThresholds.Default;
        }
    }

    public bool Enabled => settings.Enabled && sampler.SupportsKeyboard();

    public KeyboardStatus Read()
    {
        var supported = sampler.SupportsKeyboard();
        var state = !supported ? "unsupported" : !settings.Enabled ? "off" : applied switch
        {
            0 => "green", 1 => "yellow", 2 => "red", _ => attempted < 0 ? "waiting" : "unavailable"
        };
        return new KeyboardStatus(supported, settings, state);
    }

    public KeyboardStatus? Configure(KeyboardThresholds requested)
    {
        if (!requested.IsValid || requested.Enabled && !sampler.SupportsKeyboard())
            return null;
        if (requested == settings)
            return Read();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(requested));
        File.Move(temporary, path, true);
        settings = requested;
        attempted = -1;
        if (!requested.Enabled)
            Restore();
        return Read();
    }

    public void Apply(TemperatureResponse sample)
    {
        var cpu = sample.CpuTemperatureC;
        float? gpu = sample.Gpus.Select(device => device.TemperatureC).Max();
        if (cpu is null && gpu is null)
            return;
        var redMargin = applied == 2 ? Hysteresis : 0;
        var yellowMargin = applied >= 1 ? Hysteresis : 0;
        var wanted = cpu >= settings.CpuHot - redMargin || gpu >= settings.GpuHot - redMargin ? 2 :
            cpu >= settings.CpuWarm - yellowMargin || gpu >= settings.GpuWarm - yellowMargin ? 1 : 0;
        if (wanted == applied || wanted == attempted && Stopwatch.GetElapsedTime(attemptedAt).TotalSeconds < 20)
            return;
        attempted = wanted;
        attemptedAt = Stopwatch.GetTimestamp();
        var rgb = wanted switch
        {
            2 => new KeyboardColor(255, 0, 0),
            1 => new KeyboardColor(255, 208, 0),
            _ => new KeyboardColor(0, 255, 0)
        };
        if (sampler.ChangeKeyboardColor(rgb))
            applied = wanted;
    }

    public void Restore()
    {
        if (applied >= 0 && sampler.ChangeKeyboardColor(null))
            applied = -1;
    }
}
