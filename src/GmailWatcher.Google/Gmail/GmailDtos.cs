using System.Text.Json.Serialization;

namespace GmailWatcher.Google.Gmail;

// Shapes of the Gmail REST responses, reduced to the fields the archive uses. Unknown fields are ignored.

internal sealed class ProfileDto
{
    public string? EmailAddress { get; set; }

    public long? MessagesTotal { get; set; }

    public long? ThreadsTotal { get; set; }

    [JsonConverter(typeof(StringOrNumberConverter))]
    public string? HistoryId { get; set; }
}

internal sealed class LabelListDto
{
    public List<LabelDto>? Labels { get; set; }
}

internal sealed class LabelDto
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Type { get; set; }

    public LabelColorDto? Color { get; set; }
}

internal sealed class LabelColorDto
{
    public string? BackgroundColor { get; set; }

    public string? TextColor { get; set; }
}

internal sealed class MessageRefDto
{
    public string? Id { get; set; }

    public string? ThreadId { get; set; }

    public List<string>? LabelIds { get; set; }
}

internal sealed class MessageListDto
{
    public List<MessageRefDto>? Messages { get; set; }

    public string? NextPageToken { get; set; }
}

internal sealed class HistoryListDto
{
    public List<HistoryRecordDto>? History { get; set; }

    public string? NextPageToken { get; set; }

    [JsonConverter(typeof(StringOrNumberConverter))]
    public string? HistoryId { get; set; }
}

internal sealed class HistoryRecordDto
{
    public List<HistoryMessageDto>? MessagesAdded { get; set; }

    public List<HistoryMessageDto>? MessagesDeleted { get; set; }

    public List<HistoryLabelDto>? LabelsAdded { get; set; }

    public List<HistoryLabelDto>? LabelsRemoved { get; set; }
}

internal sealed class HistoryMessageDto
{
    public MessageRefDto? Message { get; set; }
}

internal sealed class HistoryLabelDto
{
    public MessageRefDto? Message { get; set; }

    public List<string>? LabelIds { get; set; }
}

/// <summary>A message in <c>format=raw</c>. <see cref="Raw"/> is decoded while parsing so the big base64 text is never held as a string.</summary>
internal sealed class MessageDto
{
    public string? Id { get; set; }

    public string? ThreadId { get; set; }

    public List<string>? LabelIds { get; set; }

    [JsonConverter(typeof(StringOrNumberConverter))]
    public string? HistoryId { get; set; }

    [JsonConverter(typeof(StringOrNumberConverter))]
    public string? InternalDate { get; set; }

    public long? SizeEstimate { get; set; }

    public string? Snippet { get; set; }

    [JsonConverter(typeof(Base64UrlBytesConverter))]
    public byte[]? Raw { get; set; }
}

// Google API error body: {"error":{"code":403,"message":"...","errors":[{"reason":"rateLimitExceeded"}],"status":"...","details":[{"reason":"..."}]}}

internal sealed class ApiErrorEnvelopeDto
{
    public ApiErrorDto? Error { get; set; }
}

internal sealed class ApiErrorDto
{
    public int? Code { get; set; }

    public string? Message { get; set; }

    public string? Status { get; set; }

    public List<ApiErrorItemDto>? Errors { get; set; }

    public List<ApiErrorItemDto>? Details { get; set; }
}

internal sealed class ApiErrorItemDto
{
    public string? Reason { get; set; }

    public string? Message { get; set; }
}
