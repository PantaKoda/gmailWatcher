using System.Collections;
using GmailWatcher.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace GmailWatcher.Desktop.Infrastructure;

public sealed record ConfigurationLoadResult(GmailWatcherOptions Options, IReadOnlyList<ConfigurationError> Errors, IReadOnlyList<string> Sources)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Loads configuration in increasing precedence: built-in defaults, shipped appsettings.json, the per-user override file
/// (gmailwatcher.config.json in the data folder), then GMAILWATCHER__-prefixed environment variables
/// (e.g. GMAILWATCHER__Google__ClientId). Failures are returned, not thrown, so the app can explain them.
/// </summary>
public static class ConfigurationLoader
{
    // Double underscore keeps process-level variables such as GMAILWATCHER_DATA_DIR out of the options tree.
    public const string EnvironmentPrefix = "GMAILWATCHER__";

    public static ConfigurationLoadResult Load(AppPaths paths, IDictionary? environment = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var sources = new List<string> { paths.DefaultsFile, paths.UserConfigFile, $"environment variables {EnvironmentPrefix}*" };

        IConfigurationRoot root;
        try
        {
            var builder = new ConfigurationBuilder()
                .AddJsonFile(paths.DefaultsFile, optional: true, reloadOnChange: false)
                .AddJsonFile(paths.UserConfigFile, optional: true, reloadOnChange: false);

            if (environment is null)
            {
                builder.AddEnvironmentVariables(EnvironmentPrefix);
            }
            else
            {
                builder.AddInMemoryCollection(FromEnvironment(environment));
            }

            root = builder.Build();
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or IOException or UnauthorizedAccessException)
        {
            return Failed(sources, new ConfigurationError("configuration file", ex.Message,
                $"Fix the file's JSON syntax or delete {paths.UserConfigFile} to use the defaults."));
        }

        GmailWatcherOptions options;
        try
        {
            options = root.Get<GmailWatcherOptions>(o => o.ErrorOnUnknownConfiguration = true) ?? new GmailWatcherOptions();
        }
        catch (InvalidOperationException ex)
        {
            // Raised for unknown keys (usually typos) and values of the wrong type.
            return Failed(sources, new ConfigurationError("configuration value", ex.Message,
                "Correct the setting name or value; see README 'Configuration' for valid keys."));
        }

        return new ConfigurationLoadResult(options, GmailWatcherOptionsValidator.Validate(options), sources);
    }

    private static ConfigurationLoadResult Failed(List<string> sources, ConfigurationError error) =>
        new(new GmailWatcherOptions(), [error], sources);

    // Mirrors the environment-variable provider: strip the prefix, map "__" to ":".
    private static IEnumerable<KeyValuePair<string, string?>> FromEnvironment(IDictionary environment)
    {
        foreach (DictionaryEntry entry in environment)
        {
            if (entry.Key is string key && key.StartsWith(EnvironmentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                yield return new(key[EnvironmentPrefix.Length..].Replace("__", ":", StringComparison.Ordinal), entry.Value as string);
            }
        }
    }
}
