using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Server.Kestrel.Core;

static class MonitorServer
{
    public static async Task<WebApplication> StartAsync(IPAddress address, int port,
        PairingIdentity identity, MetricsSampler sampler, ThermalProtection protection,
        CancellationToken cancellationToken = default)
    {
        var expected = Encoding.ASCII.GetBytes("Bearer " + PairingIdentity.Encode(identity.Token));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            server.Limits.MaxRequestHeadersTotalSize = 4096;
            server.Limits.MaxRequestBodySize = 0;
            server.Listen(address, port, listener =>
            {
                listener.Protocols = HttpProtocols.Http1;
                listener.UseHttps(identity.Certificate);
            });
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (address.Equals(IPAddress.Any) &&
                (context.Connection.RemoteIpAddress is not { } remote || !IsPrivate(remote)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            var authorization = context.Request.Headers.Authorization;
            if (authorization.Count != 1 ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(authorization.ToString()), expected))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(context);
        });
        app.MapGet("/v1/overview", (bool? sensors) => sampler.ReadOverview(sensors ?? true));
        app.MapGet("/v1/processes", (CancellationToken token) => sampler.ReadProcessesAsync(token));
        app.MapGet("/v1/temperatures", () => sampler.ReadTemperatures());
        app.MapPost("/v1/cooling/{mode}", (string mode) =>
        {
            if (mode is not ("automatic" or "maximum"))
                return Results.BadRequest();
            var cooling = protection.SetManualMode(mode == "maximum");
            return cooling is null ? Results.Conflict() : Results.Ok(cooling);
        });
        app.MapGet("/v1/thermal-protection", () => protection.GetStatus());
        app.MapPost("/v1/thermal-protection", (bool enabled, int cpuCritical, int gpuCritical) =>
        {
            var requested = new ProtectionSettings(enabled, cpuCritical, gpuCritical);
            if (!requested.IsValid)
                return Results.BadRequest();
            var configured = protection.Configure(requested);
            return configured is null ? Results.Conflict() : Results.Ok(configured);
        });
        try
        {
            await app.StartAsync(cancellationToken);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    public static IReadOnlyList<MonitorAddress> PrivateAddresses() =>
        (from adapter in NetworkInterface.GetAllNetworkInterfaces()
         where adapter.OperationalStatus == OperationalStatus.Up
            && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback
         let properties = adapter.GetIPProperties()
         let gateway = properties.GatewayAddresses.Any(entry =>
             entry.Address.AddressFamily == AddressFamily.InterNetwork &&
             !entry.Address.Equals(IPAddress.Any))
         from entry in properties.UnicastAddresses
         let ip = entry.Address
         where ip.AddressFamily == AddressFamily.InterNetwork && IsPrivate(ip) && !IPAddress.IsLoopback(ip)
         let vpn = adapter.Name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
             adapter.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
             adapter.Description.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase) || IsShared(ip)
         orderby gateway descending, vpn descending
         select new MonitorAddress(ip, adapter.Name, vpn))
        .DistinctBy(entry => entry.Address).Take(6).ToArray();

    private static bool IsShared(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var bytes = address.GetAddressBytes();
        return IPAddress.IsLoopback(address) || bytes[0] == 10 ||
            bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
            bytes[0] == 192 && bytes[1] == 168 || IsShared(address);
    }
}

sealed record MonitorAddress(IPAddress Address, string Adapter, bool IsVpn);
