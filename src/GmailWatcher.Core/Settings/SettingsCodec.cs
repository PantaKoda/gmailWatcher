using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace GmailWatcher.Core.Settings;

public enum SettingsLoadStatus
{
    Loaded,
    /// <summary>Loaded from an older schema and upgraded in memory; save to persist the upgrade.</summary>
    Migrated,
    /// <summary>
    /// Loaded, but invalid values were dropped (listed in the problem) and use their defaults.
    /// The caller should back up the original document.
    /// </summary>
    Repaired,
    /// <summary>Nothing stored; defaults returned.</summary>
    Missing,
    /// <summary>Stored data was unreadable; defaults returned. The caller should back up the original.</summary>
    Corrupt,
    /// <summary>Written by a newer app version; defaults returned and the stored data must not be overwritten.</summary>
    NewerVersion,
}

public sealed record SettingsLoadResult<T>(T Value, SettingsLoadStatus Status, int? StoredVersion, string? Problem = null);

/// <summary>Upgrades a settings document from <see cref="FromVersion"/> to <c>FromVersion + 1</c>.</summary>
public sealed record SettingsMigration(int FromVersion, Func<JsonObject, JsonObject> Apply);

/// <summary>
/// Versioned JSON encoding for a settings document.
/// <para>
/// Migration strategy: every document carries <c>schemaVersion</c>. On load, migrations run in
/// order on the raw JSON from the stored version up to <see cref="CurrentVersion"/>, then the
/// result is deserialized and normalized.
/// </para>
/// <list type="bullet">
/// <item>Adding a field with a default needs no version bump. Unknown fields are preserved through
/// <see cref="SettingsRecord.ExtensionData"/>, so an older version saving does not erase them.</item>
/// <item>Renaming, moving or reinterpreting a field, changing its type, or adding an enum member
/// requires a migration and a version bump. Older versions then see a newer schema and never
/// overwrite the document.</item>
/// <item>An individual invalid value (wrong type, unknown enum name, missing required field) is
/// dropped by its JSON path and falls back to its default; the rest of the document is kept.</item>
/// </list>
/// </summary>
public sealed class SettingsCodec<T> where T : class
{
    public const string VersionProperty = "schemaVersion";

    // Bounds the repair loop; a document needing more repairs than this is treated as corrupt.
    private const int MaxRepairs = 32;

    private readonly JsonTypeInfo<T> _typeInfo;
    private readonly Func<T> _createDefault;
    private readonly Func<T, T> _normalize;
    private readonly Dictionary<int, SettingsMigration> _migrations;

    public SettingsCodec(int currentVersion, JsonTypeInfo<T> typeInfo, Func<T> createDefault, Func<T, T> normalize, IEnumerable<SettingsMigration> migrations)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(currentVersion);
        CurrentVersion = currentVersion;
        _typeInfo = typeInfo;
        _createDefault = createDefault;
        _normalize = normalize;
        _migrations = migrations.ToDictionary(m => m.FromVersion);

        for (var version = 1; version < currentVersion; version++)
        {
            if (!_migrations.ContainsKey(version))
            {
                throw new ArgumentException($"Missing settings migration from version {version}.", nameof(migrations));
            }
        }
    }

    public int CurrentVersion { get; }

    public T CreateDefault() => _normalize(_createDefault());

    public string Serialize(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var node = JsonSerializer.SerializeToNode(_normalize(value), _typeInfo)!.AsObject();
        node.Insert(0, VersionProperty, CurrentVersion);
        return node.ToJsonString();
    }

    public SettingsLoadResult<T> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new(CreateDefault(), SettingsLoadStatus.Missing, null);
        }

        JsonObject document;
        int version;
        try
        {
            document = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("The settings document is not a JSON object.");
            version = document[VersionProperty]?.GetValue<int>() ?? throw new JsonException($"The settings document has no {VersionProperty}.");
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return new(CreateDefault(), SettingsLoadStatus.Corrupt, null, ex.Message);
        }

        if (version > CurrentVersion)
        {
            return new(CreateDefault(), SettingsLoadStatus.NewerVersion, version,
                $"Settings were saved by a newer version of Gmail Watcher (schema {version}; this version understands {CurrentVersion}).");
        }

        if (version < 1)
        {
            return new(CreateDefault(), SettingsLoadStatus.Corrupt, version, $"Invalid {VersionProperty} {version}.");
        }

        try
        {
            for (var v = version; v < CurrentVersion; v++)
            {
                document = _migrations[v].Apply(document);
            }

            document.Remove(VersionProperty);
            var dropped = new List<string>();
            while (true)
            {
                try
                {
                    var value = document.Deserialize(_typeInfo) ?? throw new JsonException("The settings document is empty.");
                    if (dropped.Count > 0)
                    {
                        return new(_normalize(value), SettingsLoadStatus.Repaired, version,
                            "Invalid values were reset to defaults: " + string.Join(", ", dropped));
                    }

                    return new(_normalize(value), version < CurrentVersion ? SettingsLoadStatus.Migrated : SettingsLoadStatus.Loaded, version);
                }
                catch (JsonException ex) when (dropped.Count < MaxRepairs && ex.Path is { } path && JsonPathRemoval.TryRemove(document, path))
                {
                    dropped.Add(path);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            return new(CreateDefault(), SettingsLoadStatus.Corrupt, version, ex.Message);
        }
    }
}
