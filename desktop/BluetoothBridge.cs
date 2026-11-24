using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;

namespace PcPulse.Desktop;

internal sealed record BluetoothStatus(bool Enabled, string? Address, string State);

internal sealed class BluetoothBridge : IAsyncDisposable
{
    private static readonly Guid Service = new("c0c7c487-1f28-47c5-88f2-72af742ecb1c");
    private readonly string setting = Path.Combine(PairingIdentity.StateDirectory, "bluetooth-enabled");
    private readonly CloudRelay endpoint;
    private readonly object gate = new();
    private readonly SemaphoreSlim changes = new(1, 1);
    private BluetoothStatus status = new(false, null, "off");
    private CancellationTokenSource? stopped;
    private Task? worker;

    internal BluetoothBridge(CloudRelay endpoint)
    {
        this.endpoint = endpoint;
        if (File.Exists(setting))
            Start();
    }

    internal BluetoothStatus ReadStatus()
    {
        lock (gate) return status;
    }

    internal string? Pair(string? network)
    {
        var address = ReadStatus().Address;
        if (address is null)
            return null;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { bluetooth = address, dataKey = endpoint.EncodedDataKey, network });
        return "PCP4-" + PairingIdentity.Encode(payload);
    }

    internal async Task ConfigureAsync(bool enabled)
    {
        await changes.WaitAsync();
        try
        {
            if (enabled)
            {
                File.WriteAllText(setting, "enabled");
                if (worker is null || worker.IsCompleted)
                {
                    stopped?.Dispose();
                    Start();
                }
            }
            else
            {
                File.Delete(setting);
                await StopAsync();
                SetStatus(new(false, null, "off"));
            }
        }
        finally { changes.Release(); }
    }

    private void Start()
    {
        var lifetime = new CancellationTokenSource();
        stopped = lifetime;
        SetStatus(new(true, null, "starting"));
        worker = Task.Run(() => ListenAsync(lifetime.Token));
    }

    private void SetStatus(BluetoothStatus value)
    {
        lock (gate) status = value;
    }

    private async Task ListenAsync(CancellationToken token)
    {
        var sessions = new List<Task>();
        try
        {
            var radio = BluetoothRadio.Default;
            var local = radio?.LocalAddress.ToUInt64() ?? 0;
            if (local == 0)
            {
                SetStatus(new(true, null, "unavailable"));
                return;
            }
            var address = string.Join(":", local.ToString("X12").Chunk(2).Select(group => new string(group)));
            using var listener = new BluetoothListener(Service) { ServiceName = "PC Pulse" };
            listener.Start();
            using var stopListening = token.Register(() => { try { listener.Stop(); } catch (SocketException) { } });
            if (!token.IsCancellationRequested)
                SetStatus(new(true, address, "ready"));
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptBluetoothClientAsync();
                sessions.RemoveAll(session => session.IsCompleted);
                if (sessions.Count < 4)
                    sessions.Add(ServeAsync(client, token));
                else
                    client.Dispose();
            }
        }
        catch (Exception error) when (error is IOException or SocketException or InvalidOperationException or PlatformNotSupportedException)
        {
            if (!token.IsCancellationRequested)
                SetStatus(new(true, null, "unavailable"));
        }
        finally { await Task.WhenAll(sessions); }
    }

    private async Task ServeAsync(BluetoothClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                var length = new byte[4];
                while (!token.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                    idle.CancelAfter(TimeSpan.FromSeconds(35));
                    using var close = idle.Token.Register(client.Dispose);
                    await stream.ReadExactlyAsync(length, idle.Token);
                    var size = BinaryPrimitives.ReadInt32BigEndian(length);
                    if (size is <= 0 or > 8192)
                        return;
                    var request = new byte[size];
                    await stream.ReadExactlyAsync(request, idle.Token);
                    var reply = await endpoint.HandleRequest(request, idle.Token, "bluetooth");
                    if (reply is null)
                        return;
                    BinaryPrimitives.WriteInt32BigEndian(length, reply.Length);
                    await stream.WriteAsync(length, idle.Token);
                    await stream.WriteAsync(reply, idle.Token);
                    await stream.FlushAsync(idle.Token);
                }
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or InvalidOperationException) { }
        }
    }

    private async Task StopAsync()
    {
        if (stopped is null)
            return;
        await stopped.CancelAsync();
        if (worker is not null)
            await worker;
        stopped.Dispose();
        stopped = null;
        worker = null;
    }

    public async ValueTask DisposeAsync()
    {
        await changes.WaitAsync();
        try { await StopAsync(); }
        finally { changes.Release(); changes.Dispose(); }
    }
}
