using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using GmailWatcher.Google.Auth;
using Microsoft.Extensions.Time.Testing;

namespace GmailWatcher.Google.Tests.Auth;

public sealed partial class LoopbackListenerTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromMinutes(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void RedirectUriIsALoopbackAddressOnARealPort()
    {
        using var first = new LoopbackListener();
        using var second = new LoopbackListener();

        Assert.Matches(RedirectPattern(), first.RedirectUri);
        Assert.Matches(RedirectPattern(), second.RedirectUri);
        Assert.NotEqual(first.RedirectUri, second.RedirectUri);
        Assert.NotEqual("http://127.0.0.1:0/", first.RedirectUri);
    }

    [Fact]
    public async Task ReturnsCodeAndStateFromTheRedirect()
    {
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync(Generous, Ct);

        var response = await SendAsync(listener, Get(listener, "/?code=abc123&state=xyz789"));
        var result = await wait.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal("abc123", result.Code);
        Assert.Equal("xyz789", result.State);
        Assert.Null(result.Error);
        Assert.StartsWith("HTTP/1.1 200 OK", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecodesPercentEscapesAndPlusSigns()
    {
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync(Generous, Ct);

        await SendAsync(listener, Get(listener, "/?code=4%2F0AbC-d_e&state=a+b%3D&scope=ignored"));
        var result = await wait.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal("4/0AbC-d_e", result.Code);
        Assert.Equal("a b=", result.State);
    }

    [Fact]
    public async Task AnswersTheBrowserWithASelfContainedUncachedPage()
    {
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync(Generous, Ct);

        var response = await SendAsync(listener, Get(listener, "/?code=abc&state=s"));
        await wait.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Contains("Content-Type: text/html; charset=utf-8", response, StringComparison.Ordinal);
        Assert.Contains("Cache-Control: no-store", response, StringComparison.Ordinal);
        Assert.Contains("Connection: close", response, StringComparison.Ordinal);
        var body = response[(response.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        Assert.Contains("close this tab", body, StringComparison.Ordinal);
        Assert.Contains("connected", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", body, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"Content-Length: {Encoding.UTF8.GetByteCount(body)}", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsAnErrorFromGoogleAndSaysSoOnThePage()
    {
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync(Generous, Ct);

        var response = await SendAsync(listener, Get(listener, "/?error=access_denied&state=s1"));
        var result = await wait.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal("access_denied", result.Error);
        Assert.Equal("s1", result.State);
        Assert.Null(result.Code);
        Assert.Contains("not completed", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NeverEchoesTheQueryIntoThePage()
    {
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync(Generous, Ct);

        var response = await SendAsync(listener, Get(listener, "/?error=%3Cscript%3Ealert(1)%3C%2Fscript%3E&state=%3Cb%3E"));
        var result = await wait.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.DoesNotContain("<script>", response, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", response, StringComparison.Ordinal);
        Assert.Equal("<script>alert(1)</script>", result.Error);
    }

    [Fact]
    public async Task UnrelatedPathsGetA404AndTheWaitContinues()
    {
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync(Generous, Ct);

        var favicon = await SendAsync(listener, Get(listener, "/favicon.ico"));
        var other = await SendAsync(listener, Get(listener, "/admin?code=evil&state=evil"));

        Assert.StartsWith("HTTP/1.1 404", favicon, StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 404", other, StringComparison.Ordinal);
        Assert.False(wait.IsCompleted);

        await SendAsync(listener, Get(listener, "/?code=real&state=s"));
        var result = await wait.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("real", result.Code);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/?")]
    [InlineData("/?foo=bar&state=s")]
    public async Task ARootRequestWithoutCodeOrErrorIsRejectedAndIgnored(string target)
    {
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync(Generous, Ct);

        var response = await SendAsync(listener, Get(listener, target));

        Assert.StartsWith("HTTP/1.1 400", response, StringComparison.Ordinal);
        Assert.False(wait.IsCompleted);

        await SendAsync(listener, Get(listener, "/?code=c&state=s"));
        Assert.Equal("c", (await wait.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Code);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task OnlyGetIsAccepted(string method)
    {
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync(Generous, Ct);
        var host = HostOf(listener);

        var response = await SendAsync(listener, $"{method} /?code=c&state=s HTTP/1.1\r\nHost: {host}\r\nContent-Length: 0\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 405", response, StringComparison.Ordinal);
        Assert.False(wait.IsCompleted);
    }

    [Fact]
    public async Task ABrowserPreConnectThatSendsNothingIsIgnored()
    {
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync(Generous, Ct);

        using var idle = new TcpClient();
        await idle.ConnectAsync(IPAddress.Loopback, PortOf(listener), Ct);
        using var closedAtOnce = new TcpClient();
        await closedAtOnce.ConnectAsync(IPAddress.Loopback, PortOf(listener), Ct);
        closedAtOnce.Close();

        // The idle connection is still open and silent; the real request on another connection must not wait for it.
        await SendAsync(listener, Get(listener, "/?code=real&state=s"));
        var result = await wait.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal("real", result.Code);
    }

    [Fact]
    public async Task ASilentConnectionIsClosedAfterTheReadTimeout()
    {
        var time = new FakeTimeProvider();
        using var listener = new LoopbackListener(time, TimeSpan.FromSeconds(10));

        // The clock below can run far ahead before the server has even accepted the connection, so the overall wait is made very long.
        var wait = listener.WaitForCallbackAsync(TimeSpan.FromDays(30), Ct);

        using var idle = new TcpClient();
        await idle.ConnectAsync(IPAddress.Loopback, PortOf(listener), Ct);
        var read = idle.GetStream().ReadAsync(new byte[16], Ct).AsTask();

        for (var i = 0; i < 100_000 && !read.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        var count = await read.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(0, count);
        Assert.False(wait.IsCompleted);
    }

    [Fact]
    public async Task ARequestWithAForeignHostHeaderIsRejectedAndIgnored()
    {
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync(Generous, Ct);
        var port = PortOf(listener);

        foreach (var host in new[] { $"evil.example:{port}", $"localhost:{port}", $"127.0.0.1:{port + 1}", "127.0.0.1" })
        {
            var response = await SendAsync(listener, $"GET /?code=stolen&state=s HTTP/1.1\r\nHost: {host}\r\n\r\n");
            Assert.StartsWith("HTTP/1.1 400", response, StringComparison.Ordinal);
        }

        var withoutHost = await SendAsync(listener, "GET /?code=stolen&state=s HTTP/1.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 400", withoutHost, StringComparison.Ordinal);
        Assert.False(wait.IsCompleted);

        await SendAsync(listener, Get(listener, "/?code=real&state=s"));
        Assert.Equal("real", (await wait.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Code);
    }

    [Fact]
    public async Task TheHostHeaderIsMatchedWithoutRegardToCase()
    {
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync(Generous, Ct);

        await SendAsync(listener, $"GET /?code=c&state=s HTTP/1.1\r\nhOsT:   {HostOf(listener)}  \r\n\r\n");

        Assert.Equal("c", (await wait.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Code);
    }

    [Fact]
    public async Task AnOversizedRequestIsRefusedAndTheWaitContinues()
    {
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync(Generous, Ct);

        using (var flood = new TcpClient())
        {
            await flood.ConnectAsync(IPAddress.Loopback, PortOf(listener), Ct);
            var stream = flood.GetStream();
            var junk = Encoding.ASCII.GetBytes("GET /?code=x HTTP/1.1\r\nX-Junk: " + new string('a', 20 * 1024));
            await stream.WriteAsync(junk, Ct);

            // The server may reset the connection instead of finishing its answer; either way it must not complete the wait.
            try
            {
                var buffer = new byte[256];
                var read = await stream.ReadAsync(buffer, Ct);
                Assert.True(read == 0 || Encoding.ASCII.GetString(buffer, 0, read).StartsWith("HTTP/1.1 431", StringComparison.Ordinal));
            }
            catch (IOException)
            {
            }
        }

        Assert.False(wait.IsCompleted);
        await SendAsync(listener, Get(listener, "/?code=real&state=s"));
        Assert.Equal("real", (await wait.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Code);
    }

    [Fact]
    public async Task ARequestSplitAcrossSeveralPacketsIsReassembled()
    {
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync(Generous, Ct);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, PortOf(listener), Ct);
        client.NoDelay = true;
        var stream = client.GetStream();
        var request = Get(listener, "/?code=split&state=s");
        foreach (var piece in request.Chunk(7))
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(new string(piece)), Ct);
            await stream.FlushAsync(Ct);
        }

        Assert.Equal("split", (await wait.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Code);
    }

    [Fact]
    public async Task TimesOutWhenNobodyCallsBack()
    {
        var time = new FakeTimeProvider();
        using var listener = new LoopbackListener(time);
        var wait = listener.WaitForCallbackAsync(TimeSpan.FromMinutes(5), Ct);

        time.Advance(TimeSpan.FromMinutes(4));
        Assert.False(wait.IsCompleted);
        time.Advance(TimeSpan.FromMinutes(1));

        await Assert.ThrowsAsync<TimeoutException>(() => wait);
    }

    [Fact]
    public async Task CancellationEndsTheWait()
    {
        using var listener = new LoopbackListener();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var wait = listener.WaitForCallbackAsync(Generous, cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task DisposingWhileWaitingEndsTheWait()
    {
        var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync(Generous, Ct);

        listener.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task WaitingOnADisposedListenerThrows()
    {
        var listener = new LoopbackListener();
        listener.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => listener.WaitForCallbackAsync(Generous, Ct));
    }

    [Fact]
    public async Task ThePortIsReleasedOnDispose()
    {
        var listener = new LoopbackListener();
        var port = PortOf(listener);
        _ = listener.WaitForCallbackAsync(Generous, Ct);

        listener.Dispose();
        listener.Dispose();

        await Assert.ThrowsAnyAsync<SocketException>(async () =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, Ct);
        });
        var rebound = new TcpListener(IPAddress.Loopback, port);
        rebound.Start();
        rebound.Stop();
    }

    [Fact]
    public async Task ThePortIsReleasedAfterACompletedCallback()
    {
        int port;
        using (var listener = new LoopbackListener())
        {
            port = PortOf(listener);
            var wait = listener.WaitForCallbackAsync(Generous, Ct);
            await SendAsync(listener, Get(listener, "/?code=c&state=s"));
            await wait.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }

        var rebound = new TcpListener(IPAddress.Loopback, port);
        rebound.Start();
        rebound.Stop();
    }

    [Fact]
    public void TheResultNeverPrintsTheCode()
    {
        var result = new LoopbackResult("secret-code", "state", null);

        Assert.DoesNotContain("secret-code", result.ToString(), StringComparison.Ordinal);
    }

    private static int PortOf(LoopbackListener listener) => new Uri(listener.RedirectUri).Port;

    private static string HostOf(LoopbackListener listener) => $"127.0.0.1:{PortOf(listener)}";

    private static string Get(LoopbackListener listener, string target) =>
        $"GET {target} HTTP/1.1\r\nHost: {HostOf(listener)}\r\nUser-Agent: test\r\nConnection: close\r\n\r\n";

    /// <summary>Sends raw text over a real TCP connection and returns everything the server answers until it closes.</summary>
    private static async Task<string> SendAsync(LoopbackListener listener, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, PortOf(listener), Ct);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), Ct);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    [GeneratedRegex(@"^http://127\.0\.0\.1:\d{4,5}/$")]
    private static partial Regex RedirectPattern();
}
