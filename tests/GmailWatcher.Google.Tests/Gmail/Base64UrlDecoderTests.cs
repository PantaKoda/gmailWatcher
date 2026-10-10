using System.Text;
using GmailWatcher.Google.Gmail;

namespace GmailWatcher.Google.Tests.Gmail;

public sealed class Base64UrlDecoderTests
{
    [Theory]
    [InlineData("")]
    [InlineData("TQ")]
    [InlineData("TWE")]
    [InlineData("TWFu")]
    public void DecodesUnpaddedInput(string text)
    {
        var expected = Convert.FromBase64String(text + new string('=', (4 - text.Length % 4) % 4));

        Assert.Equal(expected, Base64UrlDecoder.Decode(text.AsSpan()));
        Assert.Equal(expected, Base64UrlDecoder.Decode(Encoding.ASCII.GetBytes(text)));
    }

    [Theory]
    [InlineData("TQ==", "M")]
    [InlineData("TWE=", "Ma")]
    public void DecodesPaddedInput(string text, string expected)
    {
        Assert.Equal(Encoding.ASCII.GetBytes(expected), Base64UrlDecoder.Decode(text.AsSpan()));
    }

    [Fact]
    public void EveryLengthRoundTripsInBothAlphabets()
    {
        var random = new Random(1234);
        for (var length = 0; length <= 300; length++)
        {
            var bytes = new byte[length];
            random.NextBytes(bytes);
            var standard = Convert.ToBase64String(bytes);
            var urlSafe = standard.TrimEnd('=').Replace('+', '-').Replace('/', '_');

            Assert.Equal(bytes, Base64UrlDecoder.Decode(standard.AsSpan()));
            Assert.Equal(bytes, Base64UrlDecoder.Decode(urlSafe.AsSpan()));
            Assert.Equal(bytes, Base64UrlDecoder.Decode(Encoding.ASCII.GetBytes(urlSafe)));
        }
    }

    [Fact]
    public void SkipsLineBreaks()
    {
        Assert.Equal("Many hands"u8.ToArray(), Base64UrlDecoder.Decode("TWFu\r\neSBo\r\nYW5kcw".AsSpan()));
    }

    [Theory]
    [InlineData("TW*u")]
    [InlineData("T")]
    [InlineData("TWFuT")]
    public void RejectsInvalidInput(string text)
    {
        Assert.Throws<FormatException>(() => Base64UrlDecoder.Decode(text.AsSpan()));
    }

    [Fact]
    public void RejectsBytesAboveAscii()
    {
        Assert.Throws<FormatException>(() => Base64UrlDecoder.Decode(new byte[] { (byte)'T', 0xC3, (byte)'F', (byte)'u' }));
    }
}
