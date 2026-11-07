using System.Text.Json.Nodes;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Pipe;

namespace Tasktor.Core.State;

/// <summary>
/// Minimal JSON Patch (replace / add / remove) between two trees. Objects diff by key, equal-length arrays by index;
/// a length change replaces the array (arrays in state are small top-N lists).
/// </summary>
public static class JsonDiff
{
    public static List<PatchOp> Diff(JsonNode? before, JsonNode? after, string path = "")
    {
        var ops = new List<PatchOp>();
        Walk(before, after, path, ops);
        return ops;
    }

    private static void Walk(JsonNode? a, JsonNode? b, string path, List<PatchOp> ops)
    {
        if (JsonNode.DeepEquals(a, b))
        {
            return;
        }

        switch (a, b)
        {
            case (JsonObject oa, JsonObject ob):
                foreach (var kv in oa)
                {
                    var p = JsonPointer.Combine(path, kv.Key);
                    if (!ob.TryGetPropertyValue(kv.Key, out var bv))
                    {
                        ops.Add(new PatchOp { Op = "remove", Path = p });
                    }
                    else
                    {
                        Walk(kv.Value, bv, p, ops);
                    }
                }

                foreach (var kv in ob)
                {
                    if (!oa.ContainsKey(kv.Key))
                    {
                        ops.Add(new PatchOp { Op = "add", Path = JsonPointer.Combine(path, kv.Key), Value = kv.Value?.DeepClone() });
                    }
                }

                break;
            case (JsonArray aa, JsonArray ab) when aa.Count == ab.Count:
                for (var i = 0; i < aa.Count; i++)
                {
                    Walk(aa[i], ab[i], JsonPointer.Combine(path, i), ops);
                }

                break;
            default:
                ops.Add(new PatchOp { Op = "replace", Path = path, Value = b?.DeepClone() });
                break;
        }
    }
}
