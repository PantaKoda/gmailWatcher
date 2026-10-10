using System.Globalization;
using System.Text.Json.Nodes;

namespace GmailWatcher.Core.Settings;

/// <summary>
/// Removes the node at a System.Text.Json error path (e.g. <c>$.watchlist[1].pullRequests</c> or
/// <c>$['odd.name']</c>) so one invalid value can be dropped without discarding the whole document.
/// </summary>
internal static class JsonPathRemoval
{
    public static bool TryRemove(JsonObject root, string path)
    {
        if (!TryParse(path, out var segments) || segments.Count == 0)
        {
            return false;
        }

        JsonNode? parent = root;
        foreach (var segment in segments[..^1])
        {
            parent = segment switch
            {
                string name when parent is JsonObject obj => obj[name],
                int index when parent is JsonArray array && index < array.Count => array[index],
                _ => null,
            };

            if (parent is null)
            {
                return false;
            }
        }

        switch (segments[^1])
        {
            case string name when parent is JsonObject obj && obj.ContainsKey(name):
                obj.Remove(name);
                return true;
            case int index when parent is JsonArray array && index < array.Count:
                array.RemoveAt(index);
                return true;
            default:
                return false;
        }
    }

    // Segments are property names (string) or array indexes (int).
    private static bool TryParse(string path, out List<object> segments)
    {
        segments = [];
        if (!path.StartsWith('$'))
        {
            return false;
        }

        var i = 1;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                var end = i + 1;
                while (end < path.Length && path[end] is not '.' and not '[')
                {
                    end++;
                }

                if (end == i + 1)
                {
                    return false;
                }

                segments.Add(path[(i + 1)..end]);
                i = end;
            }
            else if (path.AsSpan(i).StartsWith("['"))
            {
                var end = path.IndexOf("']", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    return false;
                }

                segments.Add(path[(i + 2)..end]);
                i = end + 2;
            }
            else if (path[i] == '[')
            {
                var end = path.IndexOf(']', i + 1);
                if (end < 0 || !int.TryParse(path.AsSpan(i + 1, end - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    return false;
                }

                segments.Add(index);
                i = end + 1;
            }
            else
            {
                return false;
            }
        }

        return true;
    }
}
