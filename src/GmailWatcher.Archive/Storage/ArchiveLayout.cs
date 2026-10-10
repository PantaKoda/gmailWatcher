using System.Globalization;
using System.Text;

namespace GmailWatcher.Archive.Storage;

/// <summary>
/// Where everything lives under the archive folder:
/// <code>
/// archive.db                      SQLite: the authoritative record (messages, labels, search index)
/// analytics.duckdb                DuckDB: derived, rebuildable from archive.db
/// accounts/&lt;account&gt;/mail/yyyy/MM/&lt;id&gt;.eml          the complete message exactly as Gmail has it
/// accounts/&lt;account&gt;/attachments/yyyy/MM/&lt;id&gt;/…   attachments as separate files
/// </code>
/// Names that come from mail (attachment file names, even addresses) are untrusted: this class is the only place
/// that turns them into paths, and it never lets one leave its folder.
/// </summary>
public sealed class ArchiveLayout
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private const int MaxFileNameLength = 120;

    public ArchiveLayout(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public string Root { get; }

    public string DatabaseFile => Path.Combine(Root, "archive.db");

    public string AnalyticsFile => Path.Combine(Root, "analytics.duckdb");

    public string AccountsDirectory => Path.Combine(Root, "accounts");

    /// <summary>The folder name for an account (relative to <see cref="AccountsDirectory"/>), derived from its address.</summary>
    public static string AccountFolderName(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var name = SanitizeSegment(email.Trim().ToLowerInvariant());
        return string.IsNullOrEmpty(name) ? "account" : name;
    }

    public string AccountDirectory(string folder) => Safe(Path.Combine(AccountsDirectory, SanitizeSegment(folder)));

    /// <summary>The relative path (from the account folder, forward slashes) of a message's .eml.</summary>
    public static string EmlRelativePath(string messageId, long internalDateMs) =>
        $"mail/{DatePart(internalDateMs)}/{SanitizeSegment(messageId)}.eml";

    /// <summary>The relative directory (from the account folder) that holds a message's attachments.</summary>
    public static string AttachmentRelativeDirectory(string messageId, long internalDateMs) =>
        $"attachments/{DatePart(internalDateMs)}/{SanitizeSegment(messageId)}";

    /// <summary>Resolves a relative path stored in the database to a full path, refusing anything outside the account folder.</summary>
    public string Resolve(string accountFolder, string relativePath)
    {
        var baseDirectory = AccountDirectory(accountFolder);
        var full = Path.GetFullPath(Path.Combine(baseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInside(baseDirectory, full))
        {
            throw new InvalidOperationException($"'{relativePath}' is outside the account folder.");
        }

        return full;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="directory"/> itself or somewhere inside it.</summary>
    public static bool IsInside(string directory, string path)
    {
        var outer = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var inner = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(inner, outer, StringComparison.OrdinalIgnoreCase)
            || inner.StartsWith(outer + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A safe single file name for an attachment: no directory parts, no characters Windows rejects, no reserved
    /// device names, no trailing dots or spaces, at most 120 characters with the extension kept.
    /// <paramref name="fallback"/> is used when nothing usable remains.
    /// </summary>
    public static string SafeFileName(string? name, string fallback)
    {
        // A name that looks like a path (....x.exe, C:dirx) is reduced to its last part, as every mail client does.
        var leaf = (name ?? "").Replace('\\', '/');
        var cleaned = SanitizeSegment(leaf[(leaf.LastIndexOf('/') + 1)..]);
        if (cleaned.Length == 0 || cleaned.Trim('.', '_', ' ').Length == 0)
        {
            cleaned = SanitizeSegment(fallback);
            if (cleaned.Length == 0)
            {
                cleaned = "attachment";
            }
        }

        var stem = Path.GetFileNameWithoutExtension(cleaned);
        var extension = Path.GetExtension(cleaned);
        if (extension.Length > 16)
        {
            stem = cleaned;
            extension = "";
        }

        if (ReservedNames.Contains(stem))
        {
            stem = "_" + stem;
        }

        var room = Math.Max(1, MaxFileNameLength - extension.Length);
        if (stem.Length > room)
        {
            stem = stem[..room].TrimEnd('.', ' ');
        }

        return (stem.Length == 0 ? "attachment" : stem) + extension;
    }

    /// <summary>One path segment from untrusted text.</summary>
    internal static string SanitizeSegment(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(char.IsControl(ch) || ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        // "." and ".." would walk out of the folder; trailing dots and spaces are silently dropped by Windows.
        var result = builder.ToString().Trim().TrimEnd('.', ' ');
        return result is "" or "." or ".." ? (text.Length == 0 ? "" : "_") : result;
    }

    private static string DatePart(long internalDateMs)
    {
        // Gmail always sets it; guard against garbage so a bad value can't create an odd folder.
        var date = internalDateMs is > 0 and < 253_402_300_799_000L
            ? DateTimeOffset.FromUnixTimeMilliseconds(internalDateMs).UtcDateTime
            : DateTime.UnixEpoch;
        return string.Create(CultureInfo.InvariantCulture, $"{date:yyyy}/{date:MM}");
    }

    private string Safe(string path)
    {
        var full = Path.GetFullPath(path);
        if (!IsInside(Root, full))
        {
            throw new InvalidOperationException($"'{path}' is outside the archive folder.");
        }

        return full;
    }
}
