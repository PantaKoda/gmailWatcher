using System.Globalization;
using GmailWatcher.Desktop.Presentation;

namespace GmailWatcher.Desktop.Tests.Presentation;

public sealed class FormattingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    // The tests pin the culture so they pass on any machine.
    private static T WithInvariantCulture<T>(Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(10 * 1024 * 1024, "10 MB")]
    [InlineData(123L * 1024 * 1024, "123 MB")]
    [InlineData(13_314_398_618, "12.4 GB")]
    public void Sizes_use_binary_units_with_three_significant_digits(long bytes, string expected) =>
        Assert.Equal(expected, WithInvariantCulture(() => Formatting.Bytes(bytes)));

    [Theory]
    [InlineData(20, "just now")]
    [InlineData(60, "a minute ago")]
    [InlineData(300, "5 minutes ago")]
    [InlineData(3600, "an hour ago")]
    [InlineData(3 * 3600, "3 hours ago")]
    public void Recent_times_are_relative(int secondsAgo, string expected) =>
        Assert.Equal(expected, Formatting.Ago(Now.AddSeconds(-secondsAgo), Now));

    [Fact]
    public void Older_times_become_days_and_then_dates()
    {
        Assert.Equal("yesterday", Formatting.Ago(Now.AddDays(-1), Now));
        Assert.Equal("5 days ago", Formatting.Ago(Now.AddDays(-5), Now));
        Assert.Equal("1 Jan 2026", WithInvariantCulture(() => Formatting.Ago(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), Now)));
    }

    [Fact]
    public void A_time_in_the_future_never_reads_as_negative() => Assert.Equal("just now", Formatting.Ago(Now.AddMinutes(5), Now));

    [Theory]
    [InlineData(20, "under a minute")]
    [InlineData(12 * 60, "about 12 min")]
    [InlineData(2 * 3600, "about 2 h")]
    [InlineData((2 * 3600) + (10 * 60), "about 2 h 10 min")]
    [InlineData((2 * 3600) + (58 * 60), "about 3 h")]
    public void Time_left_is_approximate(int seconds, string expected) => Assert.Equal(expected, Formatting.Remaining(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Plurals_and_ranges()
    {
        Assert.Equal("1 message", WithInvariantCulture(() => Formatting.Plural(1, "message")));
        Assert.Equal("2 messages", WithInvariantCulture(() => Formatting.Plural(2, "message")));
        Assert.Equal("1,200 attachments", WithInvariantCulture(() => Formatting.Plural(1200, "attachment")));
        Assert.Equal("2 other messages", WithInvariantCulture(() => Formatting.Plural(2, "other message")));
        Assert.Equal("2019–2026", Formatting.Range(new DateTimeOffset(2019, 5, 1, 0, 0, 0, TimeSpan.Zero), Now));
        Assert.Equal("2026", Formatting.Range(Now.AddDays(-3), Now));
        Assert.Equal("", Formatting.Range(null, Now));
    }
}
