using GmailWatcher.Desktop.Infrastructure;

namespace GmailWatcher.Desktop.ViewModels;

public sealed class ConfigurationErrorViewModel
{
    public ConfigurationErrorViewModel(ConfigurationLoadResult configuration, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);

        Errors = configuration.Errors.Select(e => new ErrorItem(e.Key, e.Problem, e.Fix)).ToList();
        Sources = string.Join(Environment.NewLine, configuration.Sources);
        UserConfigDirectory = paths.DataDirectory;
    }

    public IReadOnlyList<ErrorItem> Errors { get; }

    public string Sources { get; }

    public string UserConfigDirectory { get; }

    public sealed record ErrorItem(string Key, string Problem, string Fix);
}
