using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using GmailWatcher.Archive.Demo;
using GmailWatcher.Core.Mail;

namespace GmailWatcher.Desktop.Tests.Integration;

/// <summary>
/// Gmail's REST API as documented, in front of an <see cref="InMemoryMailbox"/>: the same JSON shapes (string history IDs,
/// base64url raw messages, an absent "messages" array for an empty page, 404 for history Gmail forgot...), paging with
/// <c>nextPageToken</c>, and bearer-token checking. The real <c>GmailRestMailbox</c> talks to it over HttpClient, so a
/// disagreement between the client and the API's documented shapes shows up here and not on the first real sync.
/// </summary>
internal sealed class FakeGmailHandler(InMemoryMailbox mailbox, int pageSize = 50) : HttpMessageHandler
{
    public const string Token = "test-access-token";

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _requests = new();

    /// <summary>Every request seen, in arrival order. Workers call concurrently, so this must be thread-safe.</summary>
    public IReadOnlyCollection<string> Requests => _requests;

    public int MessageGets => Requests.Count(r => r.Contains("/messages/", StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Enqueue($"{request.Method} {request.RequestUri!.PathAndQuery}");
        if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: Token })
        {
            return Json(HttpStatusCode.Unauthorized, Error(401, "Invalid Credentials", "authError"));
        }

        var path = request.RequestUri.AbsolutePath;
        var at = path.IndexOf("/users/me/", StringComparison.Ordinal);
        var resource = path[(at + "/users/me/".Length)..];
        var query = HttpUtility.ParseQueryString(request.RequestUri.Query);

        try
        {
            return resource switch
            {
                "profile" => await ProfileAsync(cancellationToken),
                "labels" => await LabelsAsync(cancellationToken),
                "messages" => await ListAsync(query, cancellationToken),
                "history" => await HistoryAsync(query, cancellationToken),
                _ when resource.StartsWith("messages/", StringComparison.Ordinal) => await MessageAsync(resource["messages/".Length..], cancellationToken),
                _ when resource.StartsWith("settings/", StringComparison.Ordinal) => await SettingAsync(resource["settings/".Length..], cancellationToken),
                _ => Json(HttpStatusCode.NotFound, Error(404, "Not Found", "notFound")),
            };
        }
        catch (HistoryExpiredException)
        {
            return Json(HttpStatusCode.NotFound, Error(404, "Requested entity was not found.", "notFound"));
        }
    }

    private async Task<HttpResponseMessage> ProfileAsync(CancellationToken ct)
    {
        var profile = await mailbox.GetProfileAsync(ct);
        return Json(HttpStatusCode.OK, new JsonObject
        {
            ["emailAddress"] = profile.EmailAddress,
            ["messagesTotal"] = profile.MessagesTotal,
            ["threadsTotal"] = profile.ThreadsTotal,
            ["historyId"] = profile.HistoryId,
        });
    }

    private async Task<HttpResponseMessage> LabelsAsync(CancellationToken ct)
    {
        var labels = new JsonArray();
        foreach (var label in await mailbox.GetLabelsAsync(ct))
        {
            labels.Add(new JsonObject { ["id"] = label.Id, ["name"] = label.Name, ["type"] = label.Kind == LabelKind.System ? "system" : "user" });
        }

        return Json(HttpStatusCode.OK, new JsonObject { ["labels"] = labels });
    }

    private async Task<HttpResponseMessage> ListAsync(System.Collections.Specialized.NameValueCollection query, CancellationToken ct)
    {
        Assert.Equal("true", query["includeSpamTrash"]); // the archive must include Spam and Trash
        var ids = new List<string>();
        await foreach (var id in mailbox.ListMessageIdsAsync(query["labelIds"], ct))
        {
            ids.Add(id);
        }

        var start = int.TryParse(query["pageToken"], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ? offset : 0;
        var page = ids.Skip(start).Take(Math.Min(pageSize, int.TryParse(query["maxResults"], out var max) ? max : pageSize)).ToList();
        var body = new JsonObject { ["resultSizeEstimate"] = ids.Count };
        if (page.Count > 0)
        {
            // Gmail omits "messages" altogether when there is nothing to list.
            body["messages"] = new JsonArray([.. page.Select(id => (JsonNode)new JsonObject { ["id"] = id, ["threadId"] = id })]);
        }

        if (start + page.Count < ids.Count)
        {
            body["nextPageToken"] = (start + page.Count).ToString(CultureInfo.InvariantCulture);
        }

        return Json(HttpStatusCode.OK, body);
    }

    private async Task<HttpResponseMessage> MessageAsync(string id, CancellationToken ct)
    {
        var message = await mailbox.GetMessageAsync(id, ct);
        if (message is null)
        {
            return Json(HttpStatusCode.NotFound, Error(404, "Requested entity was not found.", "notFound"));
        }

        return Json(HttpStatusCode.OK, new JsonObject
        {
            ["id"] = message.Id,
            ["threadId"] = message.ThreadId,
            ["labelIds"] = new JsonArray([.. message.LabelIds.Select(l => (JsonNode)JsonValue.Create(l)!)]),
            ["snippet"] = message.Snippet,
            ["historyId"] = message.HistoryId,
            ["internalDate"] = message.InternalDateMs.ToString(CultureInfo.InvariantCulture),
            ["sizeEstimate"] = message.SizeEstimate,
            ["raw"] = Convert.ToBase64String(message.Raw).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
        });
    }

    private async Task<HttpResponseMessage> HistoryAsync(System.Collections.Specialized.NameValueCollection query, CancellationToken ct)
    {
        var result = await mailbox.GetHistoryAsync(query["startHistoryId"]!, ct);
        var start = int.TryParse(query["pageToken"], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ? offset : 0;
        var page = result.Changes.Skip(start).Take(pageSize).ToList();

        var records = new JsonArray();
        var counter = start;
        foreach (var change in page)
        {
            JsonObject Message() => new() { ["id"] = change.MessageId, ["threadId"] = change.MessageId }; // a node can have only one parent
            var record = new JsonObject { ["id"] = (1000 + counter++).ToString(CultureInfo.InvariantCulture) };
            switch (change.Kind)
            {
                case HistoryChangeKind.MessageAdded:
                    record["messagesAdded"] = new JsonArray(new JsonObject { ["message"] = Message() });
                    break;
                case HistoryChangeKind.MessageDeleted:
                    record["messagesDeleted"] = new JsonArray(new JsonObject { ["message"] = Message() });
                    break;
                default:
                    if (change.LabelsAdded.Count > 0)
                    {
                        record["labelsAdded"] = new JsonArray(new JsonObject
                        {
                            ["message"] = Message(),
                            ["labelIds"] = new JsonArray([.. change.LabelsAdded.Select(l => (JsonNode)JsonValue.Create(l)!)]),
                        });
                    }

                    if (change.LabelsRemoved.Count > 0)
                    {
                        record["labelsRemoved"] = new JsonArray(new JsonObject
                        {
                            ["message"] = Message(),
                            ["labelIds"] = new JsonArray([.. change.LabelsRemoved.Select(l => (JsonNode)JsonValue.Create(l)!)]),
                        });
                    }

                    break;
            }

            records.Add(record);
        }

        var body = new JsonObject { ["historyId"] = result.LatestHistoryId };
        if (records.Count > 0)
        {
            body["history"] = records;
        }

        if (start + page.Count < result.Changes.Count)
        {
            body["nextPageToken"] = (start + page.Count).ToString(CultureInfo.InvariantCulture);
        }

        return Json(HttpStatusCode.OK, body);
    }

    private async Task<HttpResponseMessage> SettingAsync(string name, CancellationToken ct)
    {
        var documents = await mailbox.GetSettingsAsync(ct);
        var document = documents.FirstOrDefault(d => d.Name == name);
        return document is null
            ? Json(HttpStatusCode.NotFound, Error(404, "Requested entity was not found.", "notFound"))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(document.Json, Encoding.UTF8, "application/json") };
    }

    private static JsonObject Error(int code, string message, string reason) => new()
    {
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
            ["errors"] = new JsonArray(new JsonObject { ["message"] = message, ["domain"] = "global", ["reason"] = reason }),
        },
    };

    private static HttpResponseMessage Json(HttpStatusCode status, JsonObject body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
