using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using GmailWatcher.Core.Configuration;
using GmailWatcher.Core.Updates;
using GmailWatcher.Desktop.Infrastructure;
using GmailWatcher.Desktop.Updates;
using GmailWatcher.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace GmailWatcher.Desktop.Tests.Updates;

/// <summary>Serves releases and their files from memory, the way GitHub would.</summary>
internal sealed class FakeReleaseSource : IReleaseSource
{
    public List<ReleaseInfo> Releases { get; } = [];

    public string? Error { get; set; }

    public Dictionary<Uri, byte[]> Files { get; } = [];

    /// <summary>Progress reports per download, like a real download reading many small chunks.</summary>
    public int ProgressSteps { get; set; } = 1;

    /// <summary>When set, zip downloads wait for this (or cancellation) after reporting their progress.</summary>
    public TaskCompletionSource? HoldZip { get; set; }

    public Task<ReleaseListResult> GetReleasesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Error is null ? new ReleaseListResult(Releases.ToList(), null) : new ReleaseListResult(null, Error));

    public async Task DownloadAsync(Uri url, Stream destination, long maxBytes, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var bytes = Files[url];
        for (var step = 1; step <= ProgressSteps; step++)
        {
            progress?.Report((double)step / ProgressSteps);
        }

        if (HoldZip is not null && url.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal))
        {
            await HoldZip.Task.WaitAsync(cancellationToken);
        }

        await destination.WriteAsync(bytes, cancellationToken);
    }

    /// <summary>Publishes a release whose zip holds a real-looking GmailWatcher folder (exe + release.json).</summary>
    public ReleaseInfo Publish(string version, string? notes = null, bool corruptChecksum = false, string? manifestVersion = null)
    {
        var zipName = $"GmailWatcher-{version}-win-x64.zip";
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "GmailWatcher/GmailWatcher.exe", "new exe " + version);
            Write(zip, "GmailWatcher/release.json", $$"""{"version":"{{manifestVersion ?? version}}","runtime":"win-x64"}""");
            Write(zip, "GmailWatcher/runtimes/native.dll", "native");
        }

        var bytes = buffer.ToArray();
        var hash = corruptChecksum ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(bytes));
        var zipUrl = new Uri($"https://github.com/PantaKoda/gmailWatcher/releases/download/v{version}/{zipName}");
        var sumUrl = new Uri(zipUrl + ".sha256");
        Files[zipUrl] = bytes;
        Files[sumUrl] = Encoding.UTF8.GetBytes($"{hash}  {zipName}");
        AppVersion.TryParse(version, out var parsed);
        var release = new ReleaseInfo
        {
            Version = parsed,
            Tag = "v" + version,
            Title = "Gmail Watcher " + version,
            Notes = notes ?? $"## Changed\n- Things in {version}",
            HtmlUrl = new Uri($"https://github.com/PantaKoda/gmailWatcher/releases/tag/v{version}"),
            PublishedAt = new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero),
            Assets = [new ReleaseAsset(zipName, zipUrl, bytes.Length), new ReleaseAsset(zipName + ".sha256", sumUrl, 90)],
        };
        Releases.Add(release);
        return release;
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }
}

internal sealed class RecordingLauncher : IProcessLauncher
{
    public List<(string Executable, IReadOnlyList<string> Arguments)> Started { get; } = [];

    public bool Start(string executable, IReadOnlyList<string> arguments)
    {
        Started.Add((executable, arguments.ToList()));
        return true;
    }
}

/// <summary>A data folder and an install folder ("…/Programs/GmailWatcher") in a temporary directory.</summary>
internal sealed class UpdateKit : IDisposable
{
    public UpdateKit(string version = "0.2.0", bool portable = true, string? dataDirectory = null, string? updateFailed = null)
    {
        Directory.CreateDirectory(InstallDirectory);
        File.WriteAllText(Path.Combine(InstallDirectory, "GmailWatcher.exe"), "old exe");
        AppVersion.TryParse(version, out var current);
        var data = dataDirectory is null ? Path.Combine(Root, "data") : Path.Combine(Root, dataDirectory);
        Paths = new AppPaths(data, "d.json", "u.json", Path.Combine(data, "logs"));
        Service = new UpdateService(new GmailWatcherOptions(), Source, Paths,
            new InstallInfo(current, InstallDirectory, portable, UpdateFailed: InstallInfo.FailureMessage(updateFailed, current)), Launcher,
            TimeProvider.System, NullLogger<UpdateService>.Instance);
        Service.ExitRequested += (_, _) => ExitRequests++;
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "gmailwatcher-update-" + Guid.NewGuid().ToString("N"));

    public string InstallDirectory => Path.Combine(Root, "Programs", "GmailWatcher");

    public AppPaths Paths { get; }

    public FakeReleaseSource Source { get; } = new();

    public RecordingLauncher Launcher { get; } = new();

    public UpdateService Service { get; }

    public int ExitRequests { get; private set; }

    public void Dispose()
    {
        Service.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task A_check_lists_every_newer_release_newest_first()
    {
        using var kit = new UpdateKit("0.2.0");
        kit.Source.Publish("0.1.0");
        kit.Source.Publish("0.3.0");
        kit.Source.Publish("0.4.0");

        var result = await kit.Service.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.Available, result.Outcome);
        Assert.Equal(["0.4.0", "0.3.0"], kit.Service.Available.Select(r => r.Version.ToString()));
        Assert.Equal(UpdateStage.Available, kit.Service.Stage);
    }

    [Fact]
    public async Task Without_a_newer_release_the_app_is_up_to_date()
    {
        using var kit = new UpdateKit("0.2.0");
        kit.Source.Publish("0.2.0");

        var result = await kit.Service.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
        Assert.False(kit.Service.IsUpdateAvailable);
    }

    [Fact]
    public async Task Installing_downloads_verifies_stages_and_hands_over_to_the_new_version()
    {
        using var kit = new UpdateKit("0.2.0");
        kit.Source.Publish("0.3.0");
        await kit.Service.CheckNowAsync(TestContext.Current.CancellationToken);
        Assert.Null(kit.Service.CannotInstallReason);

        await kit.Service.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateStage.Restarting, kit.Service.Stage);
        Assert.Equal(1, kit.ExitRequests);
        var (executable, arguments) = Assert.Single(kit.Launcher.Started);
        var staged = Path.GetDirectoryName(executable)!;
        Assert.StartsWith(Path.Combine(kit.Paths.DataDirectory, "updates"), staged, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("new exe 0.3.0", File.ReadAllText(executable));
        Assert.Equal(
            [UpdateArguments.Apply, UpdateArguments.Target, kit.InstallDirectory, UpdateArguments.WaitPid, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), UpdateArguments.From, "0.2.0"],
            arguments);
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(kit.InstallDirectory, "GmailWatcher.exe"))); // nothing replaced until the app quits
    }

    [Fact]
    public async Task A_download_that_doesnt_match_its_checksum_is_never_installed()
    {
        using var kit = new UpdateKit("0.2.0");
        kit.Source.Publish("0.3.0", corruptChecksum: true);
        await kit.Service.CheckNowAsync(TestContext.Current.CancellationToken);

        await kit.Service.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateStage.InstallFailed, kit.Service.Stage);
        Assert.Contains("checksum", kit.Service.Message, StringComparison.Ordinal);
        Assert.Empty(kit.Launcher.Started);
        Assert.Equal(0, kit.ExitRequests);
        Assert.False(Directory.Exists(Path.Combine(kit.Paths.DataDirectory, "updates")));
    }

    [Fact]
    public async Task A_zip_that_contains_another_version_is_refused()
    {
        using var kit = new UpdateKit("0.2.0");
        kit.Source.Publish("0.3.0", manifestVersion: "0.2.5");
        await kit.Service.CheckNowAsync(TestContext.Current.CancellationToken);

        await kit.Service.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateStage.InstallFailed, kit.Service.Stage);
        Assert.Empty(kit.Launcher.Started);
    }

    [Fact]
    public async Task A_copy_built_from_source_never_replaces_itself()
    {
        using var kit = new UpdateKit("0.2.0", portable: false);
        kit.Source.Publish("0.3.0");
        await kit.Service.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Contains("built from source", kit.Service.CannotInstallReason, StringComparison.Ordinal);
        await kit.Service.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateStage.InstallFailed, kit.Service.Stage);
        Assert.Empty(kit.Launcher.Started);
    }

    [Fact]
    public async Task Download_progress_is_reported_in_whole_percent_only()
    {
        using var kit = new UpdateKit("0.2.0");
        kit.Source.Publish("0.3.0");
        kit.Source.ProgressSteps = 5000; // thousands of chunk reads
        await kit.Service.CheckNowAsync(TestContext.Current.CancellationToken);
        var changes = 0;
        kit.Service.Changed += (_, _) => changes++;

        await kit.Service.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateStage.Restarting, kit.Service.Stage);
        Assert.InRange(changes, 1, 110); // ~one per percent plus the stage changes, not one per chunk
    }

    [Fact]
    public async Task A_download_can_be_cancelled_and_then_tried_again()
    {
        using var kit = new UpdateKit("0.2.0");
        kit.Source.Publish("0.3.0");
        kit.Source.HoldZip = new TaskCompletionSource(); // a stalled connection
        await kit.Service.CheckNowAsync(TestContext.Current.CancellationToken);

        var install = kit.Service.InstallAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateStage.Downloading, kit.Service.Stage);
        Assert.True(kit.Service.CanCancelInstall);
        kit.Service.CancelInstall();
        await install;

        Assert.Equal(UpdateStage.InstallFailed, kit.Service.Stage);
        Assert.Equal("The update was cancelled. Nothing was changed.", kit.Service.Message);
        Assert.False(Directory.Exists(Path.Combine(kit.Paths.DataDirectory, "updates")));
        Assert.Empty(kit.Launcher.Started);

        kit.Source.HoldZip = null; // the connection is fine again
        await kit.Service.InstallAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateStage.Restarting, kit.Service.Stage);
    }

    [Theory]
    [InlineData(@"Programs\GmailWatcher")]
    [InlineData(@"Programs\GmailWatcher\data")]
    public async Task A_data_folder_inside_the_install_folder_prevents_installing(string dataDirectory)
    {
        using var kit = new UpdateKit("0.2.0", dataDirectory: dataDirectory);
        kit.Source.Publish("0.3.0");
        await kit.Service.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Contains("keeps its data inside its own folder", kit.Service.CannotInstallReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_check_is_explained_and_the_app_stays_usable()
    {
        using var kit = new UpdateKit("0.2.0");
        kit.Source.Error = "Couldn't reach GitHub to check for updates.";

        var result = await kit.Service.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.Equal(UpdateStage.CheckFailed, kit.Service.Stage);
        Assert.Equal("Couldn't reach GitHub to check for updates.", kit.Service.Message);
        Assert.False(kit.Service.IsUpdateAvailable);
    }
}

public sealed class UpdateViewModelTests
{
    [Fact]
    public async Task After_a_failed_hand_over_the_update_window_says_so()
    {
        using var kit = new UpdateKit("0.2.0", updateFailed: UpdateFailures.CouldNotMove);
        kit.Source.Publish("0.3.0");
        await kit.Service.CheckNowAsync(TestContext.Current.CancellationToken);

        using var window = new UpdateViewModel(kit.Service, new RecordingBrowser(), new ImmediateDispatcher());

        Assert.StartsWith("The last update couldn't be applied: Gmail Watcher's folder couldn't be moved aside", window.PreviousFailure, StringComparison.Ordinal);
        Assert.Contains("You're still on version 0.2.0", window.PreviousFailure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Progress_updates_keep_the_release_notes_list_as_it_is()
    {
        using var kit = new UpdateKit("0.2.0");
        kit.Source.Publish("0.3.0");
        kit.Source.ProgressSteps = 50;
        kit.Source.HoldZip = new TaskCompletionSource();
        await kit.Service.CheckNowAsync(TestContext.Current.CancellationToken);
        using var viewModel = new UpdateViewModel(kit.Service, new RecordingBrowser(), new ImmediateDispatcher());
        var notes = viewModel.Releases;

        var install = viewModel.InstallCommand.ExecuteAsync(null);

        Assert.Same(notes, viewModel.Releases); // selection and scroll position survive the download
        Assert.Equal(100, viewModel.ProgressPercent);
        Assert.True(viewModel.CanCancel);
        viewModel.CancelCommand.Execute(null);
        await install;
        Assert.False(viewModel.CanCancel);
        Assert.Same(notes, viewModel.Releases);
    }

    [Fact]
    public async Task The_update_window_lists_the_changes_of_every_newer_release_as_plain_text()
    {
        using var kit = new UpdateKit("0.2.0");
        kit.Source.Publish("0.3.0", notes: "## Fixed\n- **Faster** sync, see [the issue](https://github.com/x/y/issues/1)\n* `code` stays readable");
        kit.Source.Publish("0.4.0");
        await kit.Service.CheckNowAsync(TestContext.Current.CancellationToken);

        using var viewModel = new UpdateViewModel(kit.Service, new RecordingBrowser(), new ImmediateDispatcher());

        Assert.Equal("Gmail Watcher 0.4.0 is available", viewModel.Heading);
        Assert.Equal(["Gmail Watcher 0.4.0", "Gmail Watcher 0.3.0"], viewModel.Releases.Select(r => r.Title));
        Assert.Equal("Fixed\n• Faster sync, see the issue\n• code stays readable", viewModel.Releases[1].Notes);
        Assert.True(viewModel.CanInstall);
    }
}

public sealed class UpdateApplierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmailwatcher-apply-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingLauncher _launcher = new();
    private readonly List<string> _log = [];

    private string Target => Path.Combine(_root, "Programs", "GmailWatcher");

    private string Staged => Path.Combine(_root, "updates", "0.3.0", "GmailWatcher");

    public UpdateApplierTests()
    {
        Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Target, "GmailWatcher.exe"), "old");
        File.WriteAllText(Path.Combine(Target, "old-only.dll"), "old");
        Directory.CreateDirectory(Path.Combine(Staged, "runtimes"));
        File.WriteAllText(Path.Combine(Staged, "GmailWatcher.exe"), "new");
        File.WriteAllText(Path.Combine(Staged, "runtimes", "native.dll"), "new");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private UpdateApplier Applier(bool exits = true) => new((_, _) => exits, _launcher, _log.Add);

    [Fact]
    public void The_new_version_replaces_the_folder_and_the_previous_one_is_kept_for_rollback()
    {
        var code = Applier().Apply(Staged, Target, 1234, "0.2.0");

        Assert.Equal(0, code);
        Assert.Equal("new", File.ReadAllText(Path.Combine(Target, "GmailWatcher.exe")));
        Assert.True(File.Exists(Path.Combine(Target, "runtimes", "native.dll")));
        Assert.False(File.Exists(Path.Combine(Target, "old-only.dll"))); // a clean folder, no leftovers
        Assert.Equal("old", File.ReadAllText(Path.Combine(Target + ".previous", "GmailWatcher.exe")));
        var (executable, arguments) = Assert.Single(_launcher.Started);
        Assert.Equal(Path.Combine(Target, "GmailWatcher.exe"), executable);
        Assert.Equal([UpdateArguments.UpdatedFrom, "0.2.0"], arguments);
    }

    [Fact]
    public void A_refused_hand_over_starts_the_previous_version_again_and_says_why()
    {
        File.Delete(Path.Combine(Staged, "GmailWatcher.exe")); // an unusable staged copy

        var code = Applier().Apply(Staged, Target, 1234, "0.2.0");

        Assert.Equal(1, code);
        var (executable, arguments) = Assert.Single(_launcher.Started);
        Assert.Equal(Path.Combine(Target, "GmailWatcher.exe"), executable);
        Assert.Equal([UpdateArguments.UpdateFailed, UpdateFailures.Refused], arguments);
    }

    [Fact]
    public void Nothing_changes_when_the_old_app_doesnt_quit()
    {
        var code = Applier(exits: false).Apply(Staged, Target, 1234, "0.2.0");

        Assert.Equal(2, code);
        Assert.Equal("old", File.ReadAllText(Path.Combine(Target, "GmailWatcher.exe")));
        Assert.Empty(_launcher.Started);
    }

    [Fact]
    public void A_failed_copy_puts_the_previous_version_back_and_starts_it()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // relies on Windows file sharing to make the copy fail
        }

        using (new FileStream(Path.Combine(Staged, "runtimes", "native.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var code = Applier().Apply(Staged, Target, 1234, "0.2.0");

            Assert.Equal(4, code);
        }

        Assert.Equal("old", File.ReadAllText(Path.Combine(Target, "GmailWatcher.exe")));
        Assert.True(File.Exists(Path.Combine(Target, "old-only.dll")));
        var (executable, arguments) = Assert.Single(_launcher.Started);
        Assert.Equal(Path.Combine(Target, "GmailWatcher.exe"), executable);
        Assert.Equal([UpdateArguments.UpdateFailed, UpdateFailures.CopyFailed], arguments);
        Assert.Contains(_log, l => l.Contains("restoring the previous version", StringComparison.Ordinal));
    }
}
