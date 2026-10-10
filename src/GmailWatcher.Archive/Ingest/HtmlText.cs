using System.Net;
using System.Text.RegularExpressions;

namespace GmailWatcher.Archive.Ingest;

/// <summary>
/// Plain text from an HTML message body, for the search index and previews. It only removes markup; the HTML is never
/// rendered or executed anywhere in the app. NonBacktracking regexes keep hostile input (megabytes of nested tags)
/// linear; that engine has no back-references, hence one pattern per element.
/// </summary>
internal static partial class HtmlText
{
    public static string Extract(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var text = Comments().Replace(html, " ");
        text = Scripts().Replace(text, " ");
        text = Styles().Replace(text, " ");
        text = Head().Replace(text, " ");
        text = BlockBreaks().Replace(text, " ");
        text = Tags().Replace(text, "");
        return WebUtility.HtmlDecode(text);
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.NonBacktracking)]
    private static partial Regex Comments();

    [GeneratedRegex(@"<script\b[^>]*>.*?</script\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex Scripts();

    [GeneratedRegex(@"<style\b[^>]*>.*?</style\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex Styles();

    [GeneratedRegex(@"<head\b[^>]*>.*?</head\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex Head();

    [GeneratedRegex(@"</?(br|p|div|tr|li|ul|ol|table|h[1-6]|blockquote|hr|td)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex BlockBreaks();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.Singleline | RegexOptions.NonBacktracking)]
    private static partial Regex Tags();
}
