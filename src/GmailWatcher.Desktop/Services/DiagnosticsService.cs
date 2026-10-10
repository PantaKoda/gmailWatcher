using System.Globalization;
using System.IO.Compression;
using System.Text;
using GmailWatcher.Core.Accounts;
using GmailWatcher.Desktop.Infrastructure;

namespace GmailWatcher.Desktop.Services;

/// <summary>
/// Writes a redacted diagnostics archive for bug reports: versions, OS, counts and the rotated log files. Excluded: tokens
/// (never stored in files; redacted again here), settings, message content, subjects and attachment names, and mail
/// addresses (replaced by a placeholder).
/// </summary>
public sealed class DiagnosticsService(AppPaths paths, ArchiveHost archive, SyncService sync, ICredentialStore credentials, TimeProvider time)
{
    public string Export()
    {
        var now = time.GetLocalNow();
        var directory = Path.Combine(paths.DataDirectory, "diagnostics");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, $"gmailwatcher-diagnostics-{now:yyyyMMdd-HHmmss}.zip");
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        Write(zip, "summary.txt", Summary(now));

        if (Directory.Exists(paths.LogDirectory))
        {
            foreach (var log in Directory.GetFiles(paths.LogDirectory, "*.log").OrderBy(f => f, StringComparer.Ordinal))
            {
                string text;
                try
                {
                    using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    text = reader.ReadToEnd();
                }
                catch (IOException)
                {
                    continue;
                }

                Write(zip, "logs/" + Path.GetFileName(log), Redactor.Redact(text));
            }
        }

        return file;
    }

    private string Summary(DateTimeOffset now)
    {
        var text = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Gmail Watcher {AppInfo.Display}")
            .AppendLine(CultureInfo.InvariantCulture, $"Created: {now:O}")
            .AppendLine(CultureInfo.InvariantCulture, $"OS: {Environment.OSVersion}; .NET {Environment.Version}; 64-bit process: {Environment.Is64BitProcess}")
            .AppendLine(CultureInfo.InvariantCulture, $"Sign-in storage: {credentials.Description}; persistent: {credentials.IsPersistent}")
            .AppendLine(CultureInfo.InvariantCulture, $"Archive configured: {archive.IsConfigured}; open: {archive.IsOpen}; problem: {archive.Problem ?? "none"}")
            .AppendLine(CultureInfo.InvariantCulture, $"Background jobs running: {sync.BusyCount}");

        if (archive.IsOpen)
        {
            var accounts = archive.Runtime.Accounts.List();
            text.AppendLine(CultureInfo.InvariantCulture, $"Accounts: {accounts.Count}");
            foreach (var group in accounts.GroupBy(a => a.Connection).OrderBy(g => g.Key))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  {group.Key}: {group.Count()}");
            }

            foreach (var account in accounts)
            {
                var stats = archive.Runtime.Messages.GetStats(account.Id);
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"  account {account.Id}: {stats.Messages} messages, {stats.TotalBytes} bytes, last sync {account.LastSyncCompletedAt:O} ({account.LastSyncOutcome}), verified {account.LastVerifiedAt:O} ({account.LastVerificationVerdict})");
            }
        }

        return Redactor.Redact(text.ToString());
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
