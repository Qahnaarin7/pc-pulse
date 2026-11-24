using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

if (args.Contains("--help"))
{
    Console.WriteLine("PcPulse.Agent.exe [--bind <IPv4>] [--port <1-65535>] [--no-sensors]");
    return 0;
}

try
{
    var settings = ReadSettings(args);
    using var identity = PairingIdentity.Load();
    using var sampler = new MetricsSampler(settings.Sensors);
    await using var protection = new ThermalProtection(sampler);
    await using var app = await MonitorServer.StartAsync(settings.Address, settings.Port, identity, sampler, protection);
    Console.WriteLine($"PC Pulse · {Environment.MachineName}");
    Console.WriteLine($"Адреса ПК: {settings.Address}:{settings.Port}");
    Console.WriteLine("Вставте весь наступний рядок у PC Pulse на Android:");
    Console.WriteLine(identity.CreateCode(settings.Address, settings.Port));
    Console.WriteLine("Для зупинки натисніть Ctrl+C.");
    await app.WaitForShutdownAsync();
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"PC Pulse: {error.Message}");
    return 1;
}

static (IPAddress Address, int Port, bool Sensors) ReadSettings(string[] args)
{
    IPAddress? address = null;
    var port = 8787;
    var sensors = true;
    for (var index = 0; index < args.Length; index++)
    {
        switch (args[index])
        {
            case "--bind" when index + 1 < args.Length:
                var value = args[++index];
                if (!IPAddress.TryParse(value, out address) ||
                    address.AddressFamily != AddressFamily.InterNetwork ||
                    address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast) ||
                    address.GetAddressBytes()[0] >= 224)
                    throw new ArgumentException("--bind потребує IPv4-адресу одного мережевого інтерфейсу.");
                break;
            case "--port" when index + 1 < args.Length:
                if (!int.TryParse(args[++index], out port) || port is < 1 or > 65535)
                    throw new ArgumentException("--port потребує число від 1 до 65535.");
                break;
            case "--no-sensors":
                sensors = false;
                break;
            default:
                throw new ArgumentException("Невідомий або неповний аргумент. Використайте --help.");
        }
    }

    return (address ?? FindLanAddress(), port, sensors);
}

static IPAddress FindLanAddress()
{
    var addresses =
        from adapter in NetworkInterface.GetAllNetworkInterfaces()
        where adapter.OperationalStatus == OperationalStatus.Up
        where adapter.NetworkInterfaceType is NetworkInterfaceType.Wireless80211
            or NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
        let properties = adapter.GetIPProperties()
        let gateway = properties.GatewayAddresses.Any(entry =>
            entry.Address.AddressFamily == AddressFamily.InterNetwork &&
            !entry.Address.Equals(IPAddress.Any))
        from entry in properties.UnicastAddresses
        let address = entry.Address
        where address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address)
        where !address.ToString().StartsWith("169.254.", StringComparison.Ordinal)
        orderby gateway descending, adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 descending
        select address;

    return addresses.FirstOrDefault() ?? throw new InvalidOperationException(
        "Не знайдено IPv4-адресу. Під'єднайте ПК до локальної мережі або задайте --bind.");
}
