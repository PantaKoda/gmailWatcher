using System.Diagnostics;
using GmailWatcher.Core.Platform;
using Microsoft.Extensions.Logging;

namespace GmailWatcher.Desktop.Platform;

/// <summary>Opens validated HTTPS URLs (GitHub release pages, Google sign-in and help pages) in the system browser.</summary>
public sealed class BrowserLauncher(ILogger<BrowserLauncher> logger) : IExternalBrowser
{
    public Task<LinkOpenResult> OpenAsync(Uri url)
    {
        if (!ExternalLinkPolicy.IsAllowed(url))
        {
            logger.LogWarning("Refused to open a link to {Host}", url?.Host);
            return Task.FromResult(LinkOpenResult.Refused);
        }

        try
        {
            // AbsoluteUri is percent-encoded, so it cannot inject shell arguments.
            var start = OperatingSystem.IsWindows() ? new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }
                : OperatingSystem.IsMacOS() ? new ProcessStartInfo("open", [url.AbsoluteUri])
                : new ProcessStartInfo("xdg-open", [url.AbsoluteUri]);
            using var process = Process.Start(start);
            return Task.FromResult(LinkOpenResult.Opened);
        }
        catch (Exception ex)
        {
            // Any launch failure (no handler, unsupported platform, ...) is reported, never thrown.
            logger.LogWarning(ex, "Could not launch the system browser");
            return Task.FromResult(LinkOpenResult.Failed);
        }
    }
}
