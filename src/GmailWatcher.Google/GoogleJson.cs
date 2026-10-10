using System.Text.Json;
using System.Text.Json.Serialization;
using GmailWatcher.Google.Auth;
using GmailWatcher.Google.Gmail;

namespace GmailWatcher.Google;

/// <summary>
/// Source-generated serialization for every JSON shape the app reads from Google, so nothing depends on
/// reflection and nothing needs the Google client libraries.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(TokenResponseDto))]
[JsonSerializable(typeof(OAuthErrorDto))]
[JsonSerializable(typeof(ProfileDto))]
[JsonSerializable(typeof(LabelListDto))]
[JsonSerializable(typeof(MessageListDto))]
[JsonSerializable(typeof(HistoryListDto))]
[JsonSerializable(typeof(MessageDto))]
[JsonSerializable(typeof(ApiErrorEnvelopeDto))]
internal sealed partial class GoogleJsonContext : JsonSerializerContext;

/// <summary>Reads a JSON string or number into a string (Gmail documents history IDs as strings, but tolerate numbers).</summary>
internal sealed class StringOrNumberConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString(),
        JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(reader.ValueSpan),
        JsonTokenType.Null => null,
        _ => throw new JsonException("Expected a string or a number."),
    };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

internal sealed class TokenResponseDto
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("expires_in")]
    public long? ExpiresIn { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}

internal sealed class OAuthErrorDto
{
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }
}
