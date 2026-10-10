using System.IO.Compression;
using System.Text.Json;
using GmailWatcher.Core.Configuration;
using GmailWatcher.Desktop.Infrastructure;
using GmailWatcher.Desktop.Services;
using GmailWatcher.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace GmailWatcher.Desktop.Tests.Infrastructure;

public sealed class UserConfigWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmailwatcher-config-" + Guid.NewGuid().ToString("N"));

    private AppPaths Paths => new(_root, Path.Combine(_root, "appsettings.json"), Path.Combine(_root, "gmailwatcher.config.json"), Path.Combine(_root, "logs"));

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

    private JsonElement Read() => JsonDocument.Parse(File.ReadAllText(Paths.UserConfigFile)).RootElement;

    [Fact]
    public void Nested_keys_are_created_and_other_settings_are_kept()
    {
        var writer = new UserConfigWriter(Paths);

        writer.Set(("Archive:Directory", "D:\\Mail"));
        writer.Set(("Google:ClientId", "id"), ("Google:ClientSecret", "secret"));
        writer.Set(("Archive:Directory", "E:\\Other"));

        Assert.Equal("E:\\Other", Read().GetProperty("Archive").GetProperty("Directory").GetString());
        Assert.Equal("id", Read().GetProperty("Google").GetProperty("ClientId").GetString());
        Assert.Equal("secret", Read().GetProperty("Google").GetProperty("ClientSecret").GetString());
    }

    [Fact]
    public void A_null_value_removes_the_key()
    {
        var writer = new UserConfigWriter(Paths);
        writer.Set(("Google:ClientId", "id"), ("Google:ClientSecret", "secret"));

        writer.Set(("Google:ClientSecret", null));

        Assert.False(Read().GetProperty("Google").TryGetProperty("ClientSecret", out _));
        Assert.Equal("id", Read().GetProperty("Google").GetProperty("ClientId").GetString());
    }

    [Fact]
    public void An_unreadable_file_is_kept_aside_and_replaced()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Paths.UserConfigFile, "{ broken");

        new UserConfigWriter(Paths).Set(("Archive:Directory", "D:\\Mail"));

        Assert.Equal("D:\\Mail", Read().GetProperty("Archive").GetProperty("Directory").GetString());
        Assert.Equal("{ broken", File.ReadAllText(Paths.UserConfigFile + ".unreadable"));
    }

    [Fact]
    public void No_temporary_file_is_left_behind()
    {
        new UserConfigWriter(Paths).Set(("Archive:Directory", "D:\\Mail"));

        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }
}

public sealed class ConfigurationLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmailwatcher-load-" + Guid.NewGuid().ToString("N"));

    public ConfigurationLoaderTests() => Directory.CreateDirectory(_root);

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

    private AppPaths Paths => new(_root, Path.Combine(_root, "appsettings.json"), Path.Combine(_root, "gmailwatcher.config.json"), Path.Combine(_root, "logs"));

    [Fact]
    public void Defaults_then_the_user_file_then_the_environment_in_increasing_precedence()
    {
        File.WriteAllText(Paths.DefaultsFile, """{"Sync":{"Workers":4,"QuotaUnitsPerMinute":4000},"Updates":{"CheckIntervalHours":12}}""");
        File.WriteAllText(Paths.UserConfigFile, """{"Sync":{"Workers":8},"Archive":{"Directory":"D:\\Mail"}}""");
        var environment = new System.Collections.Hashtable { ["GMAILWATCHER__Sync__Workers"] = "10", ["GMAILWATCHER_DATA_DIR"] = "ignored" };

        var result = ConfigurationLoader.Load(Paths, environment);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal(10, result.Options.Sync.Workers);
        Assert.Equal(4000, result.Options.Sync.QuotaUnitsPerMinute);
        Assert.Equal(12, result.Options.Updates.CheckIntervalHours);
        Assert.Equal("D:\\Mail", result.Options.Archive.Directory);
    }

    [Fact]
    public void A_mistyped_setting_name_is_reported_with_a_fix_instead_of_being_ignored()
    {
        File.WriteAllText(Paths.UserConfigFile, """{"Sync":{"Workerz":4}}""");

        var result = ConfigurationLoader.Load(Paths, new System.Collections.Hashtable());

        Assert.False(result.IsValid);
        Assert.Contains("Workerz", result.Errors[0].Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Broken_json_is_reported_with_the_file_to_fix()
    {
        File.WriteAllText(Paths.UserConfigFile, "{ nope");

        var result = ConfigurationLoader.Load(Paths, new System.Collections.Hashtable());

        Assert.False(result.IsValid);
        Assert.Contains("gmailwatcher.config.json", result.Errors[0].Fix, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"Sync":{"Workers":99}}""", "Sync:Workers")]
    [InlineData("""{"Sync":{"QuotaUnitsPerMinute":10}}""", "Sync:QuotaUnitsPerMinute")]
    [InlineData("""{"Google":{"ClientId":"not-a-client-id"}}""", "Google:ClientId")]
    [InlineData("""{"Archive":{"Directory":"relative\\path"}}""", "Archive:Directory")]
    [InlineData("""{"Updates":{"Repository":"not a repo"}}""", "Updates:Repository")]
    [InlineData("""{"Updates":{"CheckIntervalHours":1000}}""", "Updates:CheckIntervalHours")]
    [InlineData("""{"Google":{"TokenEndpoint":"http://evil.example/token"}}""", "Google:TokenEndpoint")]
    public void Invalid_values_name_the_setting_that_is_wrong(string json, string key)
    {
        File.WriteAllText(Paths.UserConfigFile, json);

        var result = ConfigurationLoader.Load(Paths, new System.Collections.Hashtable());

        Assert.Contains(result.Errors, e => e.Key == key);
    }

    [Fact]
    public void Token_endpoints_must_be_https_except_for_loopback_testing()
    {
        File.WriteAllText(Paths.UserConfigFile, """{"Google":{"TokenEndpoint":"http://localhost:5000/token","ApiBaseUrl":"https://gmail.googleapis.com/gmail/v1/"}}""");

        Assert.True(ConfigurationLoader.Load(Paths, new System.Collections.Hashtable()).IsValid);
    }
}

public sealed class GoogleClientSetupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmailwatcher-gcs-" + Guid.NewGuid().ToString("N"));
    private readonly GmailWatcherOptions _options = new();
    private readonly GoogleClientSetup _setup;
    private readonly AppPaths _paths;

    public GoogleClientSetupTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new AppPaths(_root, "d", Path.Combine(_root, "gmailwatcher.config.json"), Path.Combine(_root, "logs"));
        _setup = new GoogleClientSetup(_options, new UserConfigWriter(_paths));
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

    private string File_(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void The_json_google_offers_for_a_desktop_client_is_read()
    {
        var path = File_("client_secret.json", """{"installed":{"client_id":"123456789-abcdef.apps.googleusercontent.com","project_id":"p","client_secret":"GOCSPX-abcdefghij1234567890","redirect_uris":["http://localhost"]}}""");

        var credentials = GoogleClientSetup.ReadClientFile(path, out var error);

        Assert.Null(error);
        Assert.Equal("123456789-abcdef.apps.googleusercontent.com", credentials!.ClientId);
        Assert.Equal("GOCSPX-abcdefghij1234567890", credentials.ClientSecret);
    }

    [Fact]
    public void A_web_application_client_is_refused_with_advice()
    {
        var path = File_("web.json", """{"web":{"client_id":"1-a.apps.googleusercontent.com","client_secret":"x"}}""");

        Assert.Null(GoogleClientSetup.ReadClientFile(path, out var error));
        Assert.Contains("Desktop app", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"installed":{}}""")]
    public void Anything_else_is_explained_not_crashed_on(string content)
    {
        Assert.Null(GoogleClientSetup.ReadClientFile(File_("x.json", content), out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void A_missing_file_is_explained()
    {
        Assert.Null(GoogleClientSetup.ReadClientFile(Path.Combine(_root, "nope.json"), out var error));
        Assert.Contains("couldn't be read", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Saving_a_valid_client_applies_it_at_once_and_persists_it()
    {
        Assert.False(_setup.IsConfigured);

        var problem = _setup.Save("  123456789-abcdef.apps.googleusercontent.com ", "GOCSPX-abcdefghij1234567890");

        Assert.Null(problem);
        Assert.True(_setup.IsConfigured);
        Assert.Equal("123456789-abcdef.apps.googleusercontent.com", _options.Google.ClientId);
        Assert.Equal("GOCSPX-abcdefghij1234567890", _options.Google.ClientSecret);
        var saved = JsonDocument.Parse(File.ReadAllText(_paths.UserConfigFile)).RootElement.GetProperty("Google");
        Assert.Equal("123456789-abcdef.apps.googleusercontent.com", saved.GetProperty("ClientId").GetString());
    }

    [Fact]
    public void The_secret_is_optional()
    {
        Assert.Null(_setup.Save("123456789-abcdef.apps.googleusercontent.com", "  "));
        Assert.Null(_options.Google.ClientSecret);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("hello", null)]
    [InlineData("123456789-abcdef.apps.googleusercontent.com", "short")]
    public void Invalid_values_are_refused_and_nothing_is_saved(string id, string? secret)
    {
        var problem = _setup.Save(id, secret);

        Assert.NotNull(problem);
        Assert.False(_setup.IsConfigured);
        Assert.False(File.Exists(_paths.UserConfigFile));
    }
}

public sealed class RedactorTests
{
    [Theory]
    [InlineData("access ya29.a0AfH6SMBx1234567890abcdefghijklmnop done", "ya29")]
    [InlineData("refresh 1//0gAbCdEfGhIjKlMnOpQrStUvWxYz0123456789 done", "1//0g")]
    [InlineData("secret GOCSPX-abcdefghij1234567890 here", "GOCSPX")]
    [InlineData("Authorization: Bearer abcdef1234567890abcdef", "abcdef1234567890")]
    [InlineData("{\"access_token\": \"abc.def.ghi-1234\", \"x\": 1}", "abc.def.ghi")]
    [InlineData("client_secret=superSecretValue123&grant_type=x", "superSecretValue123")]
    [InlineData("GET /?code=4/0AfJohXn&state=abc HTTP/1.1", "4/0AfJohXn")]
    [InlineData("failed for someone@gmail.com today", "someone@gmail.com")]
    public void Secrets_and_addresses_are_removed(string text, string secret)
    {
        var redacted = Redactor.Redact(text);

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains("[", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_text_is_unchanged()
    {
        const string Text = "Sync of account 3 completed: 120 messages, 4 attachments, 18 MB.";

        Assert.Equal(Text, Redactor.Redact(Text));
    }
}

public sealed class DiagnosticsAndHostTests
{
    [Fact]
    public async Task The_diagnostics_archive_holds_counts_and_redacted_logs_only()
    {
        using var kit = new TestKit(accounts: 1, messages: 20);
        var account = kit.Accounts.List()[0];
        kit.Sync.StartSync(account);
        await kit.WaitIdleAsync(account.Id);
        Directory.CreateDirectory(kit.Paths.LogDirectory);
        await File.WriteAllTextAsync(Path.Combine(kit.Paths.LogDirectory, "gmailwatcher-20261010.log"),
            "INFO sync failed for private.person@gmail.com with Bearer ya29.a0AfH6SMBx1234567890abcdefghijklmnop", TestContext.Current.CancellationToken);
        var diagnostics = new DiagnosticsService(kit.Paths, kit.Archive, kit.Sync, kit.Credentials, TimeProvider.System);

        var file = diagnostics.Export();

        using var zip = ZipFile.OpenRead(file);
        var summary = new StreamReader(zip.GetEntry("summary.txt")!.Open()).ReadToEnd();
        var log = new StreamReader(zip.GetEntry("logs/gmailwatcher-20261010.log")!.Open()).ReadToEnd();
        Assert.Contains("Accounts: 1", summary, StringComparison.Ordinal);
        Assert.Contains("20 messages", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("@", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("private.person", log, StringComparison.Ordinal);
        Assert.DoesNotContain("ya29", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Choosing_a_folder_is_remembered_by_the_next_start()
    {
        using var kit = new TestKit(openArchive: false);
        var folder = Path.Combine(kit.Root, "remembered");
        Assert.Null(kit.Archive.Choose(folder));

        var reload = ConfigurationLoader.Load(kit.Paths, new System.Collections.Hashtable());
        var next = new ArchiveHost(reload.Options, kit.Config, NullLoggerFactory.Instance, TimeProvider.System);

        Assert.True(next.IsConfigured);
        Assert.True(next.TryOpen());
        Assert.Equal(Path.GetFullPath(folder), next.Directory);
        Assert.True(File.Exists(Path.Combine(folder, "archive.db")));
    }

    [Fact]
    public void An_archive_written_by_a_newer_version_is_not_touched_and_is_explained()
    {
        using var kit = new TestKit(openArchive: false);
        var folder = Path.Combine(kit.Root, "future");
        Directory.CreateDirectory(folder);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(folder, "archive.db")}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99; CREATE TABLE from_the_future (x);";
            command.ExecuteNonQuery();
        }

        var host = new ArchiveHost(new GmailWatcherOptions { Archive = { Directory = folder } }, kit.Config, NullLoggerFactory.Instance, TimeProvider.System);

        Assert.True(host.IsConfigured);
        Assert.False(host.TryOpen());
        Assert.Contains("Update Gmail Watcher", host.Problem, StringComparison.Ordinal);

    }
}

public sealed class DialogViewModelTests
{
    [Fact]
    public void A_destructive_confirmation_stays_off_until_the_phrase_is_typed_exactly()
    {
        var viewModel = new ConfirmViewModel(new GmailWatcher.Desktop.Presentation.ConfirmRequest("Delete?", "Gone for good.", "Delete", true, "DELETE"));

        Assert.True(viewModel.NeedsPhrase);
        Assert.False(viewModel.CanConfirm);
        viewModel.TypedPhrase = "delete";
        Assert.False(viewModel.CanConfirm); // case matters
        viewModel.TypedPhrase = " DELETE ";
        Assert.True(viewModel.CanConfirm);
    }

    [Fact]
    public void An_ordinary_confirmation_needs_no_phrase()
    {
        var viewModel = new ConfirmViewModel(new GmailWatcher.Desktop.Presentation.ConfirmRequest("Disconnect?", "Ok.", "Disconnect"));

        Assert.False(viewModel.NeedsPhrase);
        Assert.True(viewModel.CanConfirm);
    }

    [Fact]
    public async Task Importing_the_google_json_fills_the_fields_and_saving_closes_the_dialog()
    {
        using var kit = new TestKit(googleConfigured: false);
        var file = Path.Combine(kit.Root, "client.json");
        await File.WriteAllTextAsync(file, """{"installed":{"client_id":"123456789-abcdef.apps.googleusercontent.com","client_secret":"GOCSPX-abcdefghij1234567890"}}""", TestContext.Current.CancellationToken);
        kit.Shell.ClientFileAnswer = file;
        var viewModel = new GoogleSetupViewModel(kit.Google, kit.Shell, kit.Browser);
        var saved = 0;
        viewModel.Saved += (_, _) => saved++;

        await viewModel.ImportFileCommand.ExecuteAsync(null);
        viewModel.SaveCommand.Execute(null);

        Assert.Equal("123456789-abcdef.apps.googleusercontent.com", viewModel.ClientId);
        Assert.Null(viewModel.Problem);
        Assert.Equal(1, saved);
        Assert.True(kit.Google.IsConfigured);
    }

    [Fact]
    public void Saving_bad_values_shows_the_problem_and_keeps_the_dialog_open()
    {
        using var kit = new TestKit(googleConfigured: false);
        var viewModel = new GoogleSetupViewModel(kit.Google, kit.Shell, kit.Browser) { ClientId = "nonsense" };
        var saved = 0;
        viewModel.Saved += (_, _) => saved++;

        viewModel.SaveCommand.Execute(null);

        Assert.True(viewModel.HasProblem);
        Assert.Equal(0, saved);
    }

    [Fact]
    public async Task The_setup_links_open_google_cloud_pages_through_the_checked_browser()
    {
        using var kit = new TestKit(googleConfigured: false);
        var viewModel = new GoogleSetupViewModel(kit.Google, kit.Shell, kit.Browser);

        await viewModel.OpenProjectCommand.ExecuteAsync(null);
        await viewModel.OpenGmailApiCommand.ExecuteAsync(null);
        await viewModel.OpenConsentCommand.ExecuteAsync(null);
        await viewModel.OpenCredentialsCommand.ExecuteAsync(null);

        Assert.All(kit.Browser.Opened, url => Assert.Equal("console.cloud.google.com", url.Host));
        Assert.Equal(4, kit.Browser.Opened.Count);
    }

    [Fact]
    public async Task Cancelling_the_sign_in_closes_the_dialog_without_an_account()
    {
        using var kit = new TestKit();
        var accounts = new AccountService(kit.Archive, new WaitingConnector(), NullLogger<AccountService>.Instance);
        using var viewModel = new ConnectViewModel(accounts, kit.Shell, new ImmediateDispatcher(), null);
        var closed = 0;
        viewModel.CloseRequested += (_, _) => closed++;

        viewModel.Start();
        Assert.True(viewModel.IsWaiting);
        viewModel.CancelCommand.Execute(null);
        await Tests.ViewModels.MainViewModelTests.WaitUntilAsync(() => closed > 0, what: "the dialog to close");

        Assert.Null(viewModel.Result?.Account);
        Assert.Empty(kit.Accounts.List());
    }

    [Fact]
    public async Task A_failed_sign_in_shows_the_reason_and_can_be_retried()
    {
        using var kit = new TestKit(googleConfigured: false);
        using var viewModel = new ConnectViewModel(kit.Accounts, kit.Shell, new ImmediateDispatcher(), null);

        viewModel.Start();
        await Tests.ViewModels.MainViewModelTests.WaitUntilAsync(() => viewModel.HasError, what: "the error");

        Assert.Contains("isn't set up", viewModel.Error, StringComparison.Ordinal);
        Assert.False(viewModel.IsWaiting);
        viewModel.TryAgainCommand.Execute(null);
        await Tests.ViewModels.MainViewModelTests.WaitUntilAsync(() => viewModel.HasError, what: "the error again");
    }

    [Fact]
    public async Task The_sign_in_link_is_offered_for_copying_when_the_browser_cannot_be_used()
    {
        using var kit = new TestKit();
        var connector = new LinkConnector();
        var accounts = new AccountService(kit.Archive, connector, NullLogger<AccountService>.Instance);
        using var viewModel = new ConnectViewModel(accounts, kit.Shell, new ImmediateDispatcher(), null);

        viewModel.Start();
        await Tests.ViewModels.MainViewModelTests.WaitUntilAsync(() => viewModel.AuthorizationLink is not null, what: "the link");
        await viewModel.CopyLinkCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasLink);
        Assert.Equal(["https://accounts.google.com/o/oauth2/v2/auth?x=1"], kit.Shell.Copied);
        viewModel.CancelCommand.Execute(null);
    }

    private sealed class WaitingConnector : IAccountConnector
    {
        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public async Task<string> SignInAsync(string? loginHint, IProgress<Uri>? authorizationUrl, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return "never@example.com";
        }

        public Task SignOutAsync(string email, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class LinkConnector : IAccountConnector
    {
        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public async Task<string> SignInAsync(string? loginHint, IProgress<Uri>? authorizationUrl, CancellationToken cancellationToken)
        {
            authorizationUrl?.Report(new Uri("https://accounts.google.com/o/oauth2/v2/auth?x=1"));
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return "never@example.com";
        }

        public Task SignOutAsync(string email, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
