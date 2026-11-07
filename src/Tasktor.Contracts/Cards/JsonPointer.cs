using System.Globalization;
using System.Text.Json.Nodes;

namespace Tasktor.Contracts.Cards;

/// <summary>RFC 6901 JSON Pointers: the binding syntax of U1 D-H2. Shared by the agent and every viewer.</summary>
public static class JsonPointer
{
    /// <summary>Resolve <paramref name="pointer"/> against <paramref name="root"/>. Missing → false (renderers show "—").</summary>
    public static bool TryResolve(JsonNode? root, string pointer, out JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        value = root;
        if (pointer.Length == 0)
        {
            return root is not null;
        }

        if (pointer[0] != '/')
        {
            value = null;
            return false;
        }

        var node = root;
        foreach (var raw in pointer[1..].Split('/'))
        {
            var token = Unescape(raw);
            switch (node)
            {
                case JsonObject obj when obj.TryGetPropertyValue(token, out var child):
                    node = child;
                    break;
                case JsonArray arr when int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < arr.Count:
                    node = arr[i];
                    break;
                default:
                    value = null;
                    return false;
            }
        }

        value = node;
        return true;
    }

    public static string Escape(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }

    public static string Unescape(string token) =>
        token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

    public static string Combine(string parent, string token) => parent + "/" + Escape(token);

    public static string Combine(string parent, int index) => parent + "/" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>True when <paramref name="pointer"/> equals <paramref name="prefix"/> or lies under it.</summary>
    public static bool IsUnder(string pointer, string prefix)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        ArgumentNullException.ThrowIfNull(prefix);
        return prefix.Length == 0
            || pointer == prefix
            || (pointer.StartsWith(prefix, StringComparison.Ordinal) && pointer.Length > prefix.Length && pointer[prefix.Length] == '/')
            || (prefix.StartsWith(pointer, StringComparison.Ordinal) && prefix.Length > pointer.Length && prefix[pointer.Length] == '/');
    }

    /// <summary>Apply JSON Patch ops (replace / add / remove) to <paramref name="root"/>; returns the new root.</summary>
    public static JsonNode? Apply(JsonNode? root, string op, string path, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        {
            return op == "remove" ? null : value?.DeepClone();
        }

        var cut = path.LastIndexOf('/');
        var parentPath = path[..cut];
        var token = Unescape(path[(cut + 1)..]);
        if (!TryResolve(root, parentPath, out var parent) || parent is null)
        {
            throw new InvalidOperationException("patch parent missing: " + parentPath);
        }

        switch (parent)
        {
            case JsonObject obj:
                if (op == "remove")
                {
                    obj.Remove(token);
                }
                else
                {
                    obj[token] = value?.DeepClone();
                }

                break;
            case JsonArray arr:
                var i = token == "-" ? arr.Count : int.Parse(token, CultureInfo.InvariantCulture);
                if (op == "remove")
                {
                    arr.RemoveAt(i);
                }
                else if (op == "add")
                {
                    arr.Insert(i, value?.DeepClone());
                }
                else
                {
                    arr[i] = value?.DeepClone();
                }

                break;
            default:
                throw new InvalidOperationException("patch parent is not a container: " + parentPath);
        }

        return root;
    }
}
