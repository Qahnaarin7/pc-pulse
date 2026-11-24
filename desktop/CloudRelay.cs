using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PcPulse.Desktop;

internal sealed record RelaySettings(bool Enabled, string Origin, string DeviceId, string HostKey, string Enrollment);
internal sealed record RelayStatus(bool Enabled, string Origin, string State, bool CanPair);

internal sealed class CloudRelay : IAsyncDisposable
{
    private const int MaxResponse = 512 * 1024;
    private const int MaxFrame = 8192;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex KeyFormat = new("^[A-Za-z0-9_-]{43}$");
    private static readonly Regex EnvelopeId = new("^[a-f0-9]{32}$");
    private static readonly Regex SavePath = new(
        @"^/v1/thermal-protection\?enabled=(true|false)&cpuCritical=[0-9]{2,3}&gpuCritical=[0-9]{2,3}$");
    private static readonly Regex ProcessActionPath = new(@"^/v1/processes/[0-9]{1,10}/terminate\?started=[0-9]{1,19}$");
    private static readonly Regex KeyboardPath = new(
        @"^/v1/keyboard-temperature\?enabled=(true|false)&cpuWarm=[0-9]{2,3}&cpuHot=[0-9]{2,3}&gpuWarm=[0-9]{2,3}&gpuHot=[0-9]{2,3}$");

    private readonly string file = Path.Combine(PairingIdentity.StateDirectory, "cloud-relay.json");
    private readonly object gate = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim changed = new(0, 1);
    private readonly HttpClient local;
    private readonly byte[] dataKey;
    private readonly string clientKey;
    private readonly Dictionary<string, long> replay = new(StringComparer.Ordinal);
    private readonly Task worker;
    private RelaySettings? settings;
    private CancellationTokenSource? connecting;
    private string state = "off";

    internal CloudRelay(PairingIdentity identity, int port)
    {
        dataKey = HMACSHA256.HashData(identity.Token, "PC Pulse relay data v1"u8);
        clientKey = PairingIdentity.Encode(HMACSHA256.HashData(identity.Token, "PC Pulse relay client v1"u8));
        var pin = identity.Certificate.GetCertHash(HashAlgorithmName.SHA256);
        local = new HttpClient(new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
                CryptographicOperations.FixedTimeEquals(pin, certificate.GetCertHash(HashAlgorithmName.SHA256))
        })
        {
            BaseAddress = new Uri($"https://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(7)
        };
        local.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", PairingIdentity.Encode(identity.Token));
        settings = Load();
        worker = Task.Run(RunAsync);
    }

    internal RelayStatus ReadStatus()
    {
        lock (gate)
            return new RelayStatus(settings?.Enabled == true, settings?.Origin ?? "", state,
                settings is { Enabled: true, Enrollment.Length: 0 });
    }

    internal string EncodedDataKey => PairingIdentity.Encode(dataKey);

    internal void Configure(bool enabled, string address, string enrollment)
    {
        lock (gate)
        {
            RelaySettings? next = settings;
            if (enabled)
            {
                var origin = ParseOrigin(address) ?? throw new ArgumentException("Вкажіть HTTPS-адресу Worker.");
                if (settings?.Origin == origin && settings.DeviceId.Length > 0)
                {
                    if (enrollment.Length > 0 && !KeyFormat.IsMatch(enrollment))
                        throw new ArgumentException("Ключ реєстрації має бути у форматі Base64URL.");
                    next = settings with { Enabled = true, Enrollment = enrollment.Length == 0 ? settings.Enrollment : enrollment };
                }
                else
                {
                    if (!KeyFormat.IsMatch(enrollment))
                        throw new ArgumentException("Введіть ключ реєстрації нової адреси Cloudflare.");
                    next = new RelaySettings(true, origin, PairingIdentity.Encode(RandomNumberGenerator.GetBytes(16)),
                        PairingIdentity.Encode(RandomNumberGenerator.GetBytes(32)), enrollment);
                }
            }
            else if (settings is not null)
            {
                next = settings with { Enabled = false };
            }
            if (next is null || next == settings)
                return;
            Save(next);
            settings = next;
            state = enabled ? "connecting" : "off";
            connecting?.Cancel();
            Signal();
        }
    }

    internal string? PhoneCode(string? localCode)
    {
        lock (gate)
        {
            if (settings is not { Enabled: true, Enrollment.Length: 0 } active)
                return null;
            var code = new RelayPairing(localCode, active.Origin, active.DeviceId, clientKey, PairingIdentity.Encode(dataKey));
            return "PCP3-" + PairingIdentity.Encode(JsonSerializer.SerializeToUtf8Bytes(code, Json));
        }
    }

    private async Task RunAsync()
    {
        var lifetime = stopping.Token;
        var delay = 1;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                RelaySettings? active;
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                lock (gate)
                {
                    active = settings;
                    if (active?.Enabled == true)
                    {
                        state = "connecting";
                        connecting = connection;
                    }
                }
                if (active?.Enabled != true)
                {
                    await changed.WaitAsync(lifetime);
                    delay = 1;
                    continue;
                }
                try
                {
                    await ConnectAsync(active, connection.Token);
                    delay = 1;
                }
                catch (OperationCanceledException) when (connection.IsCancellationRequested)
                {
                    delay = 1;
                }
                catch (Exception)
                {
                    lock (gate)
                    {
                        if (settings?.Enabled == true)
                            state = "offline";
                    }
                    delay = Math.Min(30, delay * 2);
                }
                finally
                {
                    lock (gate)
                    {
                        if (connecting == connection)
                            connecting = null;
                    }
                }
                await changed.WaitAsync(TimeSpan.FromSeconds(delay), lifetime);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task ConnectAsync(RelaySettings active, CancellationToken token)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
        socket.Options.SetRequestHeader("Authorization", "Bearer " + active.HostKey);
        socket.Options.SetRequestHeader("X-PC-Pulse-Client", clientKey);
        if (active.Enrollment.Length > 0)
            socket.Options.SetRequestHeader("X-PC-Pulse-Enrollment", active.Enrollment);
        var endpoint = new UriBuilder(active.Origin) { Scheme = "wss", Path = $"/v1/devices/{active.DeviceId}/host" }.Uri;
        using (var opening = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            opening.CancelAfter(TimeSpan.FromSeconds(12));
            await socket.ConnectAsync(endpoint, opening.Token);
        }
        lock (gate)
        {
            if (settings != active || token.IsCancellationRequested)
                return;
            if (active.Enrollment.Length > 0)
            {
                settings = active with { Enrollment = "" };
                Save(settings);
            }
            state = "online";
        }
        var buffer = new byte[MaxFrame];
        while (!token.IsCancellationRequested)
        {
            var size = 0;
            ValueWebSocketReceiveResult part;
            do
            {
                if (size == buffer.Length)
                    throw new IOException("Relay request frame exceeds its limit.");
                part = await socket.ReceiveAsync(buffer.AsMemory(size), token);
                if (part.MessageType == WebSocketMessageType.Close)
                    return;
                if (part.MessageType != WebSocketMessageType.Text)
                    throw new IOException("Unsupported relay frame kind.");
                size += part.Count;
            } while (!part.EndOfMessage);
            var text = buffer.AsSpan(0, size);
            var output = await HandleRequest(text.ToArray(), token);
            if (output is not null)
                await socket.SendAsync(output.AsMemory(), WebSocketMessageType.Text, true, token);
        }
    }

    internal async Task<byte[]?> HandleRequest(byte[] frame, CancellationToken token, string transport = "cloudflare")
    {
        string id;
        RelayRequest? request;
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(frame, Json);
            if (envelope is not { Id: { } frameId, Data: not null } || !EnvelopeId.IsMatch(frameId))
                return null;
            id = frameId;
            request = JsonSerializer.Deserialize<RelayRequest>(Decrypt(envelope), Json);
        }
        catch (Exception error) when (error is JsonException or CryptographicException or FormatException)
        {
            return null;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (request is null || request.At < now - 45_000 || request.At > now + 45_000 ||
            request.Method == "POST" && request.At < now - 15_000)
            return null;
        lock (replay)
        {
            foreach (var expired in replay.Where(entry => entry.Value < now - 90_000).Select(entry => entry.Key).ToArray())
                replay.Remove(expired);
            if (replay.Count >= 1500 || !replay.TryAdd(id, now))
                return null;
        }
        if (!Valid(request))
            return EncryptReply(id, 400, "");

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(7));
            var callToken = deadline.Token;
            using var call = new HttpRequestMessage(new HttpMethod(request.Method), request.Path);
            if (request.Phone)
                call.Headers.Add("X-PC-Pulse-Phone", transport);
            using var response = await local.SendAsync(call, HttpCompletionOption.ResponseHeadersRead, callToken);
            var length = response.Content.Headers.ContentLength;
            if (length > MaxResponse)
                return EncryptReply(id, 502, "");
            using var stream = await response.Content.ReadAsStreamAsync(callToken);
            using var payload = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, callToken)) > 0)
            {
                if (payload.Length + count > MaxResponse)
                    return EncryptReply(id, 502, "");
                payload.Write(buffer, 0, count);
            }
            return EncryptReply(id, (int)response.StatusCode, Encoding.UTF8.GetString(payload.ToArray()));
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            return EncryptReply(id, 503, "");
        }
    }

    private static bool Valid(RelayRequest request)
    {
        if (request.Method == "GET")
            return request.Path is "/v1/overview?sensors=true" or "/v1/overview?sensors=false"
                or "/v1/processes" or "/v1/temperatures" or "/v1/thermal-protection" or "/v1/keyboard-temperature";
        return request.Method == "POST" && (request.Path is "/v1/cooling/automatic" or "/v1/cooling/maximum" ||
            request.Path is not null && (SavePath.IsMatch(request.Path) || ProcessActionPath.IsMatch(request.Path) ||
                KeyboardPath.IsMatch(request.Path)));
    }

    private byte[] Decrypt(Envelope envelope)
    {
        var data = Decode(envelope.Data);
        if (data.Length < 28)
            throw new CryptographicException("Empty relay request.");
        var result = new byte[data.Length - 28];
        using var aead = new AesGcm(dataKey, 16);
        aead.Decrypt(data.AsSpan(0, 12), data.AsSpan(12, result.Length), data.AsSpan(data.Length - 16),
            result, AssociatedData("request", envelope.Id));
        return result;
    }

    private byte[] EncryptReply(string id, int status, string body)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(new RelayReply(status, body), Json);
        if (content.Length > MaxResponse)
            content = JsonSerializer.SerializeToUtf8Bytes(new RelayReply(502, ""), Json);
        var result = new byte[12 + content.Length + 16];
        RandomNumberGenerator.Fill(result.AsSpan(0, 12));
        using var aead = new AesGcm(dataKey, 16);
        aead.Encrypt(result.AsSpan(0, 12), content, result.AsSpan(12, content.Length),
            result.AsSpan(12 + content.Length), AssociatedData("response", id));
        return JsonSerializer.SerializeToUtf8Bytes(new Envelope(id, PairingIdentity.Encode(result)), Json);
    }

    private static byte[] AssociatedData(string direction, string id) =>
        Encoding.ASCII.GetBytes($"pcpulse/relay:v1:{direction}:{id}");

    private static byte[] Decode(string value)
    {
        var standard = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(standard.PadRight((standard.Length + 3) / 4 * 4, '='));
    }

    internal static string? ParseOrigin(string address) =>
        Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" &&
        uri.Fragment.Length == 0 && uri.Query.Length == 0
            ? uri.GetLeftPart(UriPartial.Authority) : null;

    private RelaySettings? Load()
    {
        try
        {
            var value = JsonSerializer.Deserialize<RelaySettings>(File.ReadAllBytes(file), Json);
            return value is not null && ParseOrigin(value.Origin) == value.Origin &&
                value.DeviceId is { Length: 22 } && KeyFormat.IsMatch(value.HostKey) &&
                value.Enrollment is not null ? value : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private void Save(RelaySettings next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(next, Json));
            File.Move(temporary, file, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private void Signal()
    {
        if (changed.CurrentCount == 0)
            changed.Release();
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        await worker;
        local.Dispose();
        changed.Dispose();
        stopping.Dispose();
        CryptographicOperations.ZeroMemory(dataKey);
    }

    private sealed record Envelope(string Id, string Data);
    private sealed record RelayRequest(string Method, string Path, long At, bool Phone);
    private sealed record RelayReply(int Status, string Body);
    private sealed record RelayPairing(string? Direct, string Origin, string DeviceId, string ClientKey, string DataKey);
}
