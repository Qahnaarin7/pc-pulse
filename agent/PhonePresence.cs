using System.Diagnostics;

sealed class PhonePresence
{
    private readonly object gate = new();
    private long lastSeen;
    private string? route;

    internal void Seen(string transport)
    {
        lock (gate)
        {
            route = transport;
            lastSeen = Stopwatch.GetTimestamp();
        }
    }

    internal string? ActiveRoute()
    {
        lock (gate)
            return lastSeen != 0 && Stopwatch.GetElapsedTime(lastSeen).TotalSeconds < 16 ? route : null;
    }
}
