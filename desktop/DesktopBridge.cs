using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PcPulse.Desktop;

internal sealed record DesktopStatus(string Server, string? Phone, ProtectionStatus Protection, RelayStatus? Relay,
    BluetoothStatus? Bluetooth, bool CanPair, KeyboardStatus Keyboard);
internal sealed record DesktopOverview(OverviewResponse Data, DesktopStatus Status);
internal sealed record DesktopProcesses(ProcessSnapshot Data, DesktopStatus Status);
internal sealed record DesktopCommand(string Name, bool Enabled = false, ProtectionSettings? Protection = null,
    string? Address = null, string? Enrollment = null, int Pid = 0, ulong Started = 0, KeyboardThresholds? Keyboard = null);
internal sealed record DesktopReply(JsonElement Payload, string? Error = null);

internal sealed class DesktopBridge(int port)
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private string PipeName => $"PcPulse.Desktop.{port}";

    internal async Task<T?> SendAsync<T>(DesktopCommand command, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(7));
        var effective = deadline.Token;
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(2000, effective);
        using var writer = new StreamWriter(pipe, Utf8, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Utf8, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), effective);
        var line = await reader.ReadLineAsync(effective) ?? throw new IOException("Desktop agent stopped responding.");
        var reply = JsonSerializer.Deserialize<DesktopReply>(line) ?? throw new IOException("Invalid response from the desktop agent.");
        if (reply.Error is { } error)
            throw new ArgumentException(error);
        return reply.Payload.Deserialize<T>();
    }

    internal async Task ListenAsync(Func<DesktopCommand, CancellationToken, Task<object?>> execute, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(8));
                var effective = deadline.Token;
                using var reader = new StreamReader(pipe, Utf8, leaveOpen: true);
                using var writer = new StreamWriter(pipe, Utf8, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(effective);
                if (line is not { Length: > 0 and < 4096 } || JsonSerializer.Deserialize<DesktopCommand>(line) is not { } request)
                    continue;
                DesktopReply reply;
                try
                {
                    var result = await execute(request, effective);
                    reply = new DesktopReply(JsonSerializer.SerializeToElement(result));
                }
                catch (ArgumentException error)
                {
                    reply = new DesktopReply(JsonSerializer.SerializeToElement<object?>(null), error.Message);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    reply = new DesktopReply(JsonSerializer.SerializeToElement<object?>(null), "Агент не зміг виконати цю дію.");
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply).AsMemory(), effective);
            }
            catch (Exception error) when (error is IOException or JsonException or OperationCanceledException) { }
        }
    }
}
