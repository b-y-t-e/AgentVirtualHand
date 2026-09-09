using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using AgentVirtualHand.Server;
using Tailcat.Link;

namespace AgentVirtualHand.Link;

/// <summary>
/// Trzyma otwarty link w tle. Bez tego każde wywołanie avh-link zestawiałoby połączenie
/// od nowa - zmierzone 11-38 s na polecenie, czyli tyle, że nie da się tym pracować.
/// Polecenia rozmawiają z tłem przez nazwany potok; pierwsze wywołanie podnosi je samo.
/// </summary>
public static class LinkDaemon
{
    private const string AppName = "agentvirtualhand";

    /// <summary>Po tylu minutach bez żądań tło gasi się samo - żeby nie zostawiać procesu na zawsze.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    public static string PipeName(string? storeRoot)
    {
        // Osobne tlo dla kazdego magazynu sparowania - inaczej dwa sparowania biłyby się o ten sam potok.
        var seed = storeRoot ?? "default";
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(seed.ToLowerInvariant())));
        return $"avh-link-{hash[..12]}";
    }

    /// <summary>Wysyła żądanie do tła, podnosząc je, jeśli jeszcze nie działa.</summary>
    public static async Task<LinkResponse> SendAsync(LinkRequest request, string? storeRoot, TimeSpan timeout)
    {
        var pipe = PipeName(storeRoot);

        if (!await TryPingAsync(pipe).ConfigureAwait(false))
            await StartBackgroundAsync(pipe, storeRoot).ConfigureAwait(false);

        return await ExchangeAsync(pipe, request, timeout).ConfigureAwait(false);
    }

    public static async Task<bool> StopAsync(string? storeRoot)
    {
        var pipe = PipeName(storeRoot);
        if (!await TryPingAsync(pipe).ConfigureAwait(false)) return false;

        await ExchangeAsync(pipe, Control("stop"), TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        return true;
    }

    public static Task<bool> IsRunningAsync(string? storeRoot) => TryPingAsync(PipeName(storeRoot));

    /// <summary>Pętla tła: jeden link, wiele poleceń.</summary>
    public static async Task RunAsync(string pipeName, LinkOptions options)
    {
        await using var link = await TailcatLink.JoinAsync(AppName, options: options).ConfigureAwait(false);

        var stop = new TaskCompletionSource();
        var lastUse = DateTime.UtcNow;

        using var idle = new Timer(_ =>
        {
            if (DateTime.UtcNow - lastUse > IdleTimeout) stop.TrySetResult();
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        while (!stop.Task.IsCompleted)
        {
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            var accept = server.WaitForConnectionAsync();
            if (await Task.WhenAny(accept, stop.Task).ConfigureAwait(false) != accept) break;

            lastUse = DateTime.UtcNow;

            try
            {
                var request = LinkCodec.Decode<LinkRequest>(await ReadAsync(server).ConfigureAwait(false));

                if (request.Path == "/__daemon/ping")
                {
                    await WriteAsync(server, LinkCodec.Encode(new LinkResponse { Body = "pong" })).ConfigureAwait(false);
                }
                else if (request.Path == "/__daemon/stop")
                {
                    await WriteAsync(server, LinkCodec.Encode(new LinkResponse { Body = "stop" })).ConfigureAwait(false);
                    stop.TrySetResult();
                }
                else
                {
                    var raw = await link.RequestAsync(LinkCodec.Encode(request)).ConfigureAwait(false);
                    await WriteAsync(server, raw).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                try { await WriteAsync(server, LinkCodec.Encode(LinkResponse.Error(502, ex.Message))).ConfigureAwait(false); }
                catch { /* klient zdazyl sie rozlaczyc */ }
            }
        }
    }

    private static LinkRequest Control(string action) => new() { Method = "GET", Path = $"/__daemon/{action}" };

    private static async Task<bool> TryPingAsync(string pipeName)
    {
        try
        {
            var response = await ExchangeAsync(pipeName, Control("ping"), TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            return response.Body == "pong";
        }
        catch
        {
            return false;
        }
    }

    private static async Task StartBackgroundAsync(string pipeName, string? storeRoot)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Nie znam ścieżki własnego procesu.");

        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--daemon");
        start.ArgumentList.Add(pipeName);
        if (storeRoot is not null)
        {
            start.ArgumentList.Add("--store");
            start.ArgumentList.Add(storeRoot);
        }

        Process.Start(start);

        // Pierwsze zestawienie linku trwa kilkanascie sekund - warto poczekac raz, a nie za kazdym poleceniem.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (await TryPingAsync(pipeName).ConfigureAwait(false)) return;
            await Task.Delay(500).ConfigureAwait(false);
        }

        throw new TimeoutException("Nie udało się zestawić linku w tle. Sprawdź sparowanie: avh-link join <kod>");
    }

    private static async Task<LinkResponse> ExchangeAsync(string pipeName, LinkRequest request, TimeSpan timeout)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(timeout);

        await client.ConnectAsync(cts.Token).ConfigureAwait(false);
        await WriteAsync(client, LinkCodec.Encode(request), cts.Token).ConfigureAwait(false);

        return LinkCodec.Decode<LinkResponse>(await ReadAsync(client, cts.Token).ConfigureAwait(false));
    }

    private static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var header = new byte[4];
        BitConverter.TryWriteBytes(header, payload.Length);

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);

        var length = BitConverter.ToInt32(header);
        if (length is < 0 or > 64 * 1024 * 1024) throw new InvalidOperationException("Niepoprawna długość ramki.");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return payload;
    }
}
