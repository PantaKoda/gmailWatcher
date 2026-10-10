using System.Globalization;

namespace GmailWatcher.Desktop.Presentation;

/// <summary>Human-readable numbers, sizes and times for the UI. One place, so the whole app speaks the same way.</summary>
public static class Formatting
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Count(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    public static string Plural(long count, string singular, string? plural = null) =>
        $"{Count(count)} {(count == 1 ? singular : plural ?? singular + "s")}";

    /// <summary>1,536 becomes "1.5 KB": binary multiples (1 KB = 1024 B) as Windows Explorer shows them, three significant digits at most.</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 1024)
        {
            return string.Create(CultureInfo.CurrentCulture, $"{Math.Max(0, bytes)} B");
        }

        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    /// <summary>"just now", "5 minutes ago", "an hour ago", "yesterday", "3 Mar 2026".</summary>
    public static string Ago(DateTimeOffset when, DateTimeOffset now)
    {
        var span = now - when;
        if (span < TimeSpan.FromSeconds(45))
        {
            return "just now"; // also a time slightly in the future (clocks differ by a moment)
        }

        if (span < TimeSpan.FromSeconds(90))
        {
            return "a minute ago";
        }

        if (span < TimeSpan.FromMinutes(45))
        {
            return $"{(int)Math.Round(span.TotalMinutes)} minutes ago";
        }

        if (span < TimeSpan.FromMinutes(90))
        {
            return "an hour ago";
        }

        if (span < TimeSpan.FromHours(22))
        {
            return $"{(int)Math.Round(span.TotalHours)} hours ago";
        }

        var days = (now.LocalDateTime.Date - when.LocalDateTime.Date).Days;
        if (days <= 1)
        {
            return "yesterday";
        }

        return days < 30 ? $"{days} days ago" : when.LocalDateTime.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }

    /// <summary>"about 12 min", "about 2 h 10 min", "under a minute".</summary>
    public static string Remaining(TimeSpan span)
    {
        if (span < TimeSpan.FromSeconds(60))
        {
            return "under a minute";
        }

        if (span < TimeSpan.FromMinutes(60))
        {
            return $"about {Math.Round(span.TotalMinutes):0} min";
        }

        var hours = (int)span.TotalHours;
        var minutes = (int)Math.Round(span.Minutes / 5.0) * 5;
        if (minutes == 60)
        {
            hours++;
            minutes = 0;
        }

        return minutes == 0 ? $"about {hours} h" : $"about {hours} h {minutes} min";
    }

    public static string Range(DateTimeOffset? oldest, DateTimeOffset? newest) =>
        oldest is null || newest is null
            ? ""
            : oldest.Value.Year == newest.Value.Year
                ? oldest.Value.Year.ToString(CultureInfo.InvariantCulture)
                : $"{oldest.Value.Year}–{newest.Value.Year}";
}
