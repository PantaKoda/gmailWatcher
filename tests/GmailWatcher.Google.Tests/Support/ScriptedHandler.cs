using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace GmailWatcher.Google.Tests.Support;

/// <summary>What one request looked like when it reached the fake network.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, AuthenticationHeaderValue? Authorization, string? Body, DateTimeOffset At)
{
    public string Url => Uri.AbsoluteUri;

    public string PathAndQuery => Uri.PathAndQuery;

    /// <summary>The query string as a name to values lookup (repeated names keep every value).</summary>
    public ILookup<string, string> Query => Parse(Uri.Query);

    /// <summary>A form body (x-www-form-urlencoded) as a lookup.</summary>
    public ILookup<string, string> Form => Parse(Body ?? string.Empty);

    public static ILookup<string, string> Parse(string text)
    {
        var parsed = System.Web.HttpUtility.ParseQueryString(text);
        return parsed.AllKeys
            .Where(key => key is not null)
            .SelectMany(key => parsed.GetValues(key)!, (key, value) => (Key: key!, Value: value))
            .ToLookup(pair => pair.Key, pair => pair.Value);
    }
}

internal delegate Task<HttpResponseMessage> ScriptStep(RecordedRequest request, CancellationToken cancellationToken);

/// <summary>
/// A fake network: answers requests from a queue of prepared steps, in the order they arrive, and records every request.
/// A request with nothing left in the queue fails the test loudly instead of hanging.
/// </summary>
internal sealed class ScriptedHandler(TimeProvider? time = null) : HttpMessageHandler
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Queue<ScriptStep> _script = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly object _lock = new();
    private ScriptStep? _fallback;

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return _requests.ToArray();
            }
        }
    }

    public ScriptedHandler Enqueue(HttpResponseMessage response) => Enqueue((_, _) => Task.FromResult(response));

    public ScriptedHandler Enqueue(Func<RecordedRequest, HttpResponseMessage> respond) => Enqueue((request, _) => Task.FromResult(respond(request)));

    public ScriptedHandler Enqueue(ScriptStep step)
    {
        lock (_lock)
        {
            _script.Enqueue(step);
        }

        return this;
    }

    public ScriptedHandler EnqueueJson(HttpStatusCode status, string json, TimeSpan? retryAfter = null) => Enqueue(Json(status, json, retryAfter));

    public ScriptedHandler EnqueueException(Exception exception) => Enqueue((_, _) => throw exception);

    /// <summary>Answers every request that finds the queue empty.</summary>
    public ScriptedHandler Always(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        _fallback = (request, _) => Task.FromResult(respond(request));
        return this;
    }

    public ScriptedHandler Always(ScriptStep step)
    {
        _fallback = step;
        return this;
    }

    public ScriptedHandler AlwaysJson(HttpStatusCode status, string json, TimeSpan? retryAfter = null) => Always(_ => Json(status, json, retryAfter));

    public static HttpResponseMessage Json(HttpStatusCode status, string json, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (retryAfter is { } wait)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
        }

        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, request.Headers.Authorization, body, _time.GetUtcNow());

        ScriptStep? step;
        lock (_lock)
        {
            _requests.Add(recorded);
            step = _script.Count > 0 ? _script.Dequeue() : _fallback;
        }

        if (step is null)
        {
            throw new InvalidOperationException($"The test scripted no answer for {request.Method} {request.RequestUri}.");
        }

        return await step(recorded, cancellationToken).ConfigureAwait(false);
    }
}
