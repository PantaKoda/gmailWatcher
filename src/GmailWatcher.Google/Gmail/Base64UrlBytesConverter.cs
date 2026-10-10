using System.Buffers;
using System.Buffers.Text;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GmailWatcher.Google.Gmail;

/// <summary>
/// Decodes base64url JSON strings straight into bytes. A raw message can be tens of megabytes; going through
/// <c>GetString()</c> would first build a second, larger copy as a string.
/// </summary>
internal sealed class Base64UrlBytesConverter : JsonConverter<byte[]>
{
    public override byte[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Expected a base64 string.");
        }

        try
        {
            if (reader.ValueIsEscaped)
            {
                return Base64UrlDecoder.Decode(reader.GetString().AsSpan());
            }

            if (reader.HasValueSequence)
            {
                var sequence = reader.ValueSequence;
                var copy = ArrayPool<byte>.Shared.Rent(checked((int)sequence.Length));
                try
                {
                    sequence.CopyTo(copy);
                    return Base64UrlDecoder.Decode(copy.AsSpan(0, (int)sequence.Length));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(copy);
                }
            }

            return Base64UrlDecoder.Decode(reader.ValueSpan);
        }
        catch (FormatException ex)
        {
            throw new JsonException("The message content is not valid base64.", ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Base64Url.EncodeToString(value));
}

/// <summary>
/// A forgiving base64 decoder for Gmail's <c>raw</c> field: accepts the URL-safe alphabet and the standard one,
/// with or without padding, and skips line breaks. Anything else is a <see cref="FormatException"/>.
/// </summary>
internal static class Base64UrlDecoder
{
    private const sbyte Invalid = -1;
    private const sbyte Skip = -2;
    private const sbyte Padding = -3;

    private static readonly sbyte[] Table = BuildTable();

    public static byte[] Decode(ReadOnlySpan<byte> utf8) => Decode<byte>(utf8);

    public static byte[] Decode(ReadOnlySpan<char> chars) => Decode<char>(chars);

    private static byte[] Decode<T>(ReadOnlySpan<T> source)
        where T : unmanaged, IBinaryInteger<T>
    {
        // First pass: count the data characters so the result is allocated at its exact size (no trim-copy of a large array).
        var data = 0;
        foreach (var item in source)
        {
            var value = Classify(int.CreateTruncating(item));
            if (value == Padding)
            {
                break;
            }

            if (value == Invalid)
            {
                throw new FormatException("Invalid character in base64 data.");
            }

            if (value != Skip)
            {
                data++;
            }
        }

        if (data % 4 == 1)
        {
            throw new FormatException("Truncated base64 data.");
        }

        var result = new byte[data / 4 * 3 + (data % 4 == 0 ? 0 : data % 4 - 1)];
        var written = 0;
        var bits = 0;
        var accumulator = 0;
        foreach (var item in source)
        {
            var value = Classify(int.CreateTruncating(item));
            if (value == Padding)
            {
                break;
            }

            if (value == Skip)
            {
                continue;
            }

            int sextet = value;
            accumulator = (accumulator << 6) | sextet;
            bits += 6;
            if (bits >= 8)
            {
                bits -= 8;
                result[written++] = (byte)(accumulator >> bits);
                accumulator &= (1 << bits) - 1;
            }
        }

        return result;
    }

    private static sbyte Classify(int code) => code is >= 0 and < 256 ? Table[code] : Invalid;

    private static sbyte[] BuildTable()
    {
        var table = new sbyte[256];
        Array.Fill(table, Invalid);
        for (var i = 0; i < 26; i++)
        {
            table['A' + i] = (sbyte)i;
            table['a' + i] = (sbyte)(26 + i);
        }

        for (var i = 0; i < 10; i++)
        {
            table['0' + i] = (sbyte)(52 + i);
        }

        table['+'] = table['-'] = 62;
        table['/'] = table['_'] = 63;
        table['='] = Padding;
        table['\r'] = table['\n'] = table[' '] = table['\t'] = Skip;
        return table;
    }
}
