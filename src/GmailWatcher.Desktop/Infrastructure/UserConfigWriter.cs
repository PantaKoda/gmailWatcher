using System.Text.Json;
using System.Text.Json.Nodes;

namespace GmailWatcher.Desktop.Infrastructure;

/// <summary>
/// Writes individual settings ("Archive:Directory", "Google:ClientId") into the per-user override file, keeping everything
/// else in it. The file is replaced atomically, so a crash cannot leave half a configuration.
/// </summary>
public sealed class UserConfigWriter(AppPaths paths)
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private readonly Lock _gate = new();

    /// <summary>Sets each key (colon-separated path) to its value; a null value removes the key.</summary>
    public void Set(params (string Key, string? Value)[] values)
    {
        lock (_gate)
        {
            var root = Read();
            foreach (var (key, value) in values)
            {
                var segments = key.Split(':', StringSplitOptions.RemoveEmptyEntries);
                var node = root;
                foreach (var segment in segments[..^1])
                {
                    node = node[segment] as JsonObject ?? (JsonObject)(node[segment] = new JsonObject());
                }

                if (value is null)
                {
                    node.Remove(segments[^1]);
                }
                else
                {
                    node[segments[^1]] = value;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(paths.UserConfigFile)!);
            var temp = paths.UserConfigFile + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(Pretty));
            File.Move(temp, paths.UserConfigFile, overwrite: true);
        }
    }

    private JsonObject Read()
    {
        try
        {
            return File.Exists(paths.UserConfigFile) && JsonNode.Parse(File.ReadAllText(paths.UserConfigFile)) is JsonObject existing ? existing : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // An unreadable override file is replaced by what the user just chose; the bad one is kept beside it.
            File.Copy(paths.UserConfigFile, paths.UserConfigFile + ".unreadable", overwrite: true);
            return [];
        }
    }
}
