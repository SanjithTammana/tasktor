using System.Text.Json.Nodes;
using Tasktor.Hosts.Common.Render;

namespace Tasktor.Hosts.Common.Tests;

/// <summary>U4 D-G1: sparklines from inline points or the agent's hour series.</summary>
public sealed class SparkTests
{
    [Fact]
    public void Percentages_keep_a_fixed_scale()
    {
        var s = Sparks.From(null, JsonNode.Parse("""{ "hour": [10, 50, 25] }"""), "%")!;
        Assert.Equal([0.1, 0.5, 0.25], s.Values);
        Assert.Equal(("25%", "peak 50%"), (s.Last, s.Peak));
    }

    [Fact]
    public void Other_units_scale_to_their_peak_and_inline_points_win()
    {
        var s = Sparks.From([2, 4, 8], JsonNode.Parse("""{ "hour": [1000] }"""), "MB/s")!;
        Assert.Equal([0.25, 0.5, 1.0], s.Values);
        Assert.Equal(("8 MB/s", "peak 8 MB/s"), (s.Last, s.Peak));
    }

    [Fact]
    public void Nothing_to_draw_is_null_and_bad_values_are_flat()
    {
        Assert.Null(Sparks.From(null, null, "%"));
        Assert.Null(Sparks.From([], JsonNode.Parse("{}"), "%"));
        Assert.Equal([0.0, 0.0], Sparks.From([double.NaN, 0], null, null)!.Values);
    }
}
