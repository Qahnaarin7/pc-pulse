using System.Diagnostics;

var settings = new ProtectionSettings(true, 95, 85);
var cpuTrip = new CoolingPolicy();
Check(cpuTrip, Reading(95, 55), 0, FanRequest.Maximum, "CPU at the threshold requests Full Speed");
cpuTrip.Applied(FanRequest.Maximum, true);
for (var time = 2; time < 32; time += 2)
    Check(cpuTrip, Reading(89, 79, "maximum"), time, FanRequest.None, "Recovery must last 30 seconds");
Check(cpuTrip, Reading(89, 79, "maximum"), 32, FanRequest.Automatic, "Stable recovery requests Auto");
cpuTrip.Applied(FanRequest.Automatic, true);
if (cpuTrip.OwnsMaximum)
    throw new InvalidOperationException("Auto must relinquish ownership");

var gpuTrip = new CoolingPolicy();
Check(gpuTrip, Reading(null, 85), 0, FanRequest.Maximum, "GPU can trigger with no CPU sensor");
gpuTrip.Applied(FanRequest.Maximum, true);
for (var time = 2; time <= 40; time += 2)
    Check(gpuTrip, Reading(70, null, "maximum"), time, FanRequest.None, "Missing triggering GPU prevents recovery");
for (var time = 42; time < 72; time += 2)
    Check(gpuTrip, Reading(70, 79, "maximum"), time, FanRequest.None, "Fresh GPU readings restart recovery");
Check(gpuTrip, Reading(70, 79, "maximum"), 72, FanRequest.Automatic, "Recovered GPU allows Auto");

var interrupted = new CoolingPolicy();
Check(interrupted, Reading(97, 60), 0, FanRequest.Maximum, "Overheating CPU requests Full Speed");
interrupted.Applied(FanRequest.Maximum, true);
Check(interrupted, Reading(75, 60, "maximum"), 2, FanRequest.None, "Start recovery");
Check(interrupted, Reading(75, 60, "maximum"), 38, FanRequest.None, "A long sample gap resets recovery");
Check(interrupted, Reading(92, 60, "maximum"), 40, FanRequest.None, "Recovery needs the full temperature margin");
interrupted.SensorUnavailable();
Check(interrupted, Reading(75, 60, "maximum"), 42, FanRequest.None, "Sensor failure resets recovery");

var manual = new CoolingPolicy();
Check(manual, Reading(98, 80, "maximum"), 0, FanRequest.None, "Already at Full Speed");
Check(manual, Reading(60, 60, "maximum"), 40, FanRequest.None, "Do not restore Auto after manual Full Speed");
Check(manual, Reading(100, 90, "custom", false), 42, FanRequest.None, "Respect unavailable vendor profiles");

var retry = new CoolingPolicy();
Check(retry, Reading(97, 70), 0, FanRequest.Maximum, "First critical sample");
retry.Applied(FanRequest.Maximum, false);
Check(retry, Reading(97, 70), 2, FanRequest.Maximum, "Retry unconfirmed Full Speed");
Console.WriteLine("Thermal policy checks passed.");

void Check(CoolingPolicy policy, TemperatureResponse reading, int seconds, FanRequest expected, string description)
{
    var actual = policy.Evaluate(settings, reading, seconds * Stopwatch.Frequency);
    if (actual != expected)
        throw new InvalidOperationException($"{description}. Expected {expected}, got {actual}.");
}

static TemperatureResponse Reading(float? cpu, float? gpu, string mode = "automatic", bool control = true) =>
    new(DateTimeOffset.UtcNow, cpu, [new GpuEntry("Test GPU", gpu, null)], "ok", new CoolingResponse(mode, control, 0, 0));
