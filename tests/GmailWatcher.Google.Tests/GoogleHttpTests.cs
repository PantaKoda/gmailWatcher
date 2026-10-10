using System.Net;
using System.Text.RegularExpressions;

namespace GmailWatcher.Google.Tests;

public sealed partial class GoogleHttpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void AllowsSixtySecondsPerRequest()
    {
        using var client = GoogleHttp.CreateClient();

        Assert.Equal(TimeSpan.FromSeconds(60), client.Timeout);
    }

    [Fact]
    public async Task IdentifiesItselfWithItsVersionAndNoBuildMetadata()
    {
        var recorder = new RecordingHandler();
        using var client = GoogleHttp.CreateClient(recorder);

        await client.GetAsync("https://gmail.test/ping", Ct);

        var userAgent = Assert.Single(recorder.UserAgents);
        Assert.Matches(UserAgentPattern(), userAgent);
        Assert.DoesNotContain('+', userAgent);
    }

    [Fact]
    public async Task UsesTheHandlerItIsGiven()
    {
        var recorder = new RecordingHandler();
        using var client = GoogleHttp.CreateClient(recorder);

        using var response = await client.GetAsync("https://gmail.test/ping", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, recorder.Calls);
    }

    [Fact]
    public async Task TheDefaultHandlerReachesALocalServer()
    {
        var server = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        server.Start();
        try
        {
            var port = ((IPEndPoint)server.LocalEndpoint).Port;
            var served = Task.Run(async () =>
            {
                using var connection = await server.AcceptTcpClientAsync(Ct);
                var stream = connection.GetStream();
                var buffer = new byte[4096];
                var request = new System.Text.StringBuilder();
                while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer, Ct);
                    request.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, read));
                }

                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nhello"u8.ToArray(), Ct);
                return request.ToString();
            }, Ct);
            using var client = GoogleHttp.CreateClient();

            var body = await client.GetStringAsync($"http://127.0.0.1:{port}/", Ct);

            Assert.Equal("hello", body);
            var received = await served;
            Assert.Contains("User-Agent: GmailWatcher/", received, StringComparison.Ordinal);
            Assert.Contains("(gzip)", received, StringComparison.Ordinal);
        }
        finally
        {
            server.Stop();
        }
    }

    [GeneratedRegex(@"^GmailWatcher/\d+\.\d+\.\d+\S* \(gzip\)$")]
    private static partial Regex UserAgentPattern();

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> UserAgents { get; } = [];

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            UserAgents.Add(request.Headers.UserAgent.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
