using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GmailWatcher.Google.Auth;

/// <summary>
/// What Google's redirect carried back to the loopback address. <see cref="State"/> is not checked here; the caller
/// that created it does that. The authorization code is short-lived but still secret, so it is kept out of <see cref="ToString"/>.
/// </summary>
public sealed record LoopbackResult(string? Code, string? State, string? Error)
{
    public override string ToString() => "LoopbackResult { redacted }";
}

/// <summary>
/// Receives the OAuth redirect on <c>http://127.0.0.1:{port}/</c>. A plain <see cref="TcpListener"/> is used instead of
/// <c>HttpListener</c> so no URL reservation or administrator rights are needed. Anything that is not the one expected
/// request (browser pre-connects, favicon fetches, requests with a foreign Host header) is answered or dropped and
/// the wait continues. The port is held from construction until <see cref="Dispose"/>.
/// </summary>
public sealed class LoopbackListener : IDisposable
{
    internal const int MaxRequestBytes = 16 * 1024;
    private const int MaxOpenConnections = 32;
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpListener _listener;
    private readonly TimeProvider _time;
    private readonly TimeSpan _readTimeout;
    private readonly string _expectedHost;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public LoopbackListener(TimeProvider? timeProvider = null, TimeSpan? connectionReadTimeout = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _readTimeout = connectionReadTimeout ?? DefaultReadTimeout;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        if (OperatingSystem.IsWindows())
        {
            // Another local process must not be able to share the port and see the callback.
            _listener.ExclusiveAddressUse = true;
        }

        _listener.Start(backlog: 16);
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _expectedHost = $"127.0.0.1:{port}";
        RedirectUri = $"http://127.0.0.1:{port}/";
    }

    /// <summary>The redirect URI to register with the authorization request (it must match exactly).</summary>
    public string RedirectUri { get; }

    /// <summary>Waits for Google's redirect and answers the browser. Throws <see cref="TimeoutException"/> when nothing arrives in time.</summary>
    public async Task<LoopbackResult> WaitForCallbackAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var completion = new TaskCompletionSource<LoopbackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var acceptLoop = AcceptLoopAsync(completion, stop.Token);
        try
        {
            return await completion.Task.WaitAsync(timeout, _time, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await acceptLoop.ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _listener.Stop();
        _lifetime.Dispose();
    }

    private async Task AcceptLoopAsync(TaskCompletionSource<LoopbackResult> completion, CancellationToken ct)
    {
        var connections = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested && !completion.Task.IsCompleted)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    completion.TrySetException(ex);
                    break;
                }

                connections.RemoveAll(task => task.IsCompleted);
                if (connections.Count >= MaxOpenConnections)
                {
                    client.Dispose();
                    continue;
                }

                connections.Add(HandleConnectionAsync(client, completion, ct));
            }
        }
        finally
        {
            completion.TrySetCanceled(ct);
            await Task.WhenAll(connections).ConfigureAwait(false);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, TaskCompletionSource<LoopbackResult> completion, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var head = await ReadRequestHeadAsync(stream, ct).ConfigureAwait(false);
                if (head.Text is null && !head.TooLarge)
                {
                    // Opened and closed (or idle) without a request: a browser pre-connect. Nothing to answer.
                    return;
                }

                if (head.TooLarge)
                {
                    await RespondAsync(stream, 431, "Request Header Fields Too Large", "text/plain; charset=utf-8", "Request too large.", ct).ConfigureAwait(false);
                    return;
                }

                var result = await RespondToRequestAsync(stream, head.Text!, ct).ConfigureAwait(false);
                if (result is not null)
                {
                    completion.TrySetResult(result);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The peer went away, the read timed out or the wait ended. None of it matters to the sign-in.
        }
    }

    /// <returns>The callback when this was the real redirect; otherwise null (the request was answered and ignored).</returns>
    private async Task<LoopbackResult?> RespondToRequestAsync(NetworkStream stream, string head, CancellationToken ct)
    {
        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/", StringComparison.Ordinal))
        {
            await RespondAsync(stream, 400, "Bad Request", "text/plain; charset=utf-8", "Bad request.", ct).ConfigureAwait(false);
            return null;
        }

        // A web page that rebinds its own DNS name to 127.0.0.1 still sends its own name as Host.
        if (!string.Equals(FindHeader(lines, "Host"), _expectedHost, StringComparison.OrdinalIgnoreCase))
        {
            await RespondAsync(stream, 400, "Bad Request", "text/plain; charset=utf-8", "Unexpected host.", ct).ConfigureAwait(false);
            return null;
        }

        if (requestLine[0] != "GET")
        {
            await RespondAsync(stream, 405, "Method Not Allowed", "text/plain; charset=utf-8", "Only GET is supported.", ct, "Allow: GET").ConfigureAwait(false);
            return null;
        }

        var target = requestLine[1];
        var queryStart = target.IndexOf('?', StringComparison.Ordinal);
        var path = queryStart < 0 ? target : target[..queryStart];
        var query = queryStart < 0 ? string.Empty : target[(queryStart + 1)..];

        if (path != "/")
        {
            await RespondAsync(stream, 404, "Not Found", "text/plain; charset=utf-8", "Not found.", ct).ConfigureAwait(false);
            return null;
        }

        var parameters = ParseQuery(query);
        parameters.TryGetValue("code", out var code);
        parameters.TryGetValue("error", out var error);
        parameters.TryGetValue("state", out var state);
        if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(error))
        {
            await RespondAsync(stream, 400, "Bad Request", "text/plain; charset=utf-8", "Missing sign-in result.", ct).ConfigureAwait(false);
            return null;
        }

        var succeeded = string.IsNullOrEmpty(error);
        await RespondAsync(stream, 200, "OK", "text/html; charset=utf-8", succeeded ? SuccessPage : FailurePage, ct).ConfigureAwait(false);
        return new LoopbackResult(string.IsNullOrEmpty(code) ? null : code, state, string.IsNullOrEmpty(error) ? null : error);
    }

    private async Task<RequestHead> ReadRequestHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(_readTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        var buffer = new byte[MaxRequestBytes];
        var total = 0;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), linked.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    return default;
                }

                var searchFrom = Math.Max(0, total - 3);
                total += read;
                var end = buffer.AsSpan(searchFrom, total - searchFrom).IndexOf("\r\n\r\n"u8);
                if (end >= 0)
                {
                    return new RequestHead(Encoding.Latin1.GetString(buffer, 0, searchFrom + end), TooLarge: false);
                }

                if (total == buffer.Length)
                {
                    return new RequestHead(null, TooLarge: true);
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return default;
        }
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string reason, string contentType, string body, CancellationToken ct, string? extraHeader = null)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(payload.Length).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Referrer-Policy: no-referrer\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\n")
            .Append("Connection: close\r\n");
        if (extraHeader is not null)
        {
            head.Append(extraHeader).Append("\r\n");
        }

        head.Append("\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string? FindHeader(string[] lines, string name)
    {
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && lines[i].AsSpan(0, colon).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return lines[i][(colon + 1)..].Trim();
            }
        }

        return null;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Decode(equals < 0 ? pair : pair[..equals]);
            var value = equals < 0 ? string.Empty : Decode(pair[(equals + 1)..]);
            parameters.TryAdd(key, value);
        }

        return parameters;

        static string Decode(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));
    }

    private readonly record struct RequestHead(string? Text, bool TooLarge);

    // Static text only: nothing from the request is echoed into the page.
    private const string PageStart =
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
        + "<title>Gmail Watcher</title><style>body{font:16px/1.5 system-ui,sans-serif;max-width:32rem;margin:15vh auto;padding:0 1rem;color:#202124;background:#fff}"
        + "h1{font-size:1.25rem}@media(prefers-color-scheme:dark){body{color:#e8eaed;background:#202124}}</style></head><body>";

    private const string SuccessPage = PageStart
        + "<h1>Gmail Watcher is connected</h1><p>Sign-in worked. You can close this tab and go back to the app.</p></body></html>";

    private const string FailurePage = PageStart
        + "<h1>Sign-in was not completed</h1><p>Nothing was connected. You can close this tab; Gmail Watcher shows what happened and lets you try again.</p></body></html>";
}
