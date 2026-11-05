using System.Text.Json;
using System.Text.Json.Nodes;
using Tasktor.Contracts.Workflow;
using Tasktor.Core.Workflow;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>C4: the episode builder on the shared fixture streams (the Python reference must give the same records).</summary>
public sealed class EpisodeTests
{
    private static readonly string Dir = Path.Combine(RepoPaths.Root, "tests", "fixtures", "episodes");

    public static List<CaptureEvent> Events(JsonElement stream, long baseUtc)
    {
        var list = new List<CaptureEvent>();
        long jump = 0;
        var tz = 0;
        foreach (var e in stream.GetProperty("events").EnumerateArray())
        {
            var t = e.GetProperty("t").GetInt64();
            if (e.TryGetProperty("utc_jump", out var j))
            {
                jump += j.GetInt64();
            }

            if (e.TryGetProperty("tz", out var z))
            {
                tz = z.GetInt32();
            }

            var kind = JsonSerializer.Deserialize("\"" + e.GetProperty("k").GetString() + "\"", Contracts.ContractsJsonContext.Default.CaptureKind);
            list.Add(new CaptureEvent
            {
                Kind = kind,
                MonoMs = t,
                TsUtcMs = baseUtc + t + jump,
                TzMin = tz,
                App = e.TryGetProperty("app", out var a) ? a.GetString() : null,
                Win = e.TryGetProperty("win", out var w) ? w.GetInt32() : null,
                Title = e.TryGetProperty("title", out var ti) ? ti.GetString() : null,
                Domain = e.TryGetProperty("domain", out var d) ? d.GetString() : null,
                Idle = e.TryGetProperty("idle", out var i) ? (i.GetString() == "media" ? IdleKind.Media : IdleKind.Input) : null,
            });
        }

        return list;
    }

    public static JsonObject Build(IEnumerable<CaptureEvent> events, long end)
    {
        var b = new EpisodeBuilder(new EpisodeThresholds());
        foreach (var e in events)
        {
            b.On(e);
        }

        b.Flush(end);
        return new JsonObject
        {
            ["activities"] = new JsonArray([.. b.Activities.Select(a => (JsonNode)new JsonObject
            {
                ["index"] = a.Index, ["app"] = a.App, ["start_ms"] = a.StartMs, ["end_ms"] = a.EndMs, ["start_utc_ms"] = a.StartUtcMs, ["tz_min"] = a.TzMin,
                ["active_ms"] = a.ActiveMs, ["idle_ms"] = a.IdleMs, ["glances"] = a.Glances, ["title_changes"] = a.TitleChanges,
                ["prev_app"] = a.PrevApp, ["next_app"] = a.NextApp, ["job_frac"] = a.JobFrac,
                ["tokens"] = new JsonArray([.. a.Tokens.Select(t => (JsonNode)new JsonArray(t.Key, t.Value))]),
            })]),
            ["windows"] = new JsonArray([.. b.Windows.Select(w => (JsonNode)new JsonObject
            {
                ["index"] = w.Index, ["start_ms"] = w.StartMs, ["end_ms"] = w.EndMs, ["active_ms"] = w.ActiveMs,
                ["dwell"] = new JsonArray([.. w.Dwell.Select(d => (JsonNode)new JsonArray(d.Key, d.Value))]),
            })]),
            ["sessions"] = new JsonArray([.. b.Sessions.Select(s => (JsonNode)new JsonObject
            {
                ["index"] = s.Index, ["start_ms"] = s.StartMs, ["end_ms"] = s.EndMs, ["activities"] = new JsonArray([.. s.Activities.Select(x => (JsonNode)x)]),
            })]),
        };
    }

    [Fact]
    public void Fixture_streams_build_the_expected_records()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "streams.json")));
        var baseUtc = doc.RootElement.GetProperty("base_utc_ms").GetInt64();
        var all = new JsonObject();
        foreach (var stream in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            all[stream.GetProperty("name").GetString()!] = Build(Events(stream, baseUtc), stream.GetProperty("end").GetInt64());
        }

        var path = Path.Combine(Dir, "expected.json");
        var json = all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (Environment.GetEnvironmentVariable("TASKTOR_REGEN_FIXTURES") == "1")
        {
            File.WriteAllText(path, json);
        }

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), all), "episode records differ from tests/fixtures/episodes/expected.json");
        Assert.True(doc.RootElement.GetProperty("streams").GetArrayLength() >= 20);
    }

    private static JsonObject Run(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "streams.json")));
        var s = doc.RootElement.GetProperty("streams").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        return Build(Events(s, 0), s.GetProperty("end").GetInt64());
    }

    private static List<JsonNode> Acts(JsonObject r) => [.. r["activities"]!.AsArray().Select(x => x!)];

    [Fact]
    public void A_glance_is_absorbed_and_counted_and_a_45_second_switch_opens_an_activity()
    {
        var glance = Acts(Run("alt-tab glance 10 s"));
        var a = Assert.Single(glance);
        Assert.Equal(1, (int)a["glances"]!);
        Assert.Equal(600_000, (long)a["active_ms"]!); // the glance's time stays in the activity

        var sw = Acts(Run("switch 45 s opens an activity"));
        Assert.Equal(["code.exe", "chrome.exe", "code.exe"], sw.Select(x => (string)x["app"]!));
        Assert.Equal("chrome.exe", (string)sw[0]["next_app"]!);
        Assert.Equal(0, (int)Assert.Single(Acts(Run("flicker 1 s is not a glance")))["glances"]!);
    }

    [Fact]
    public void Active_time_sums_to_foreground_time_minus_idle_and_gaps()
    {
        Assert.Equal(420_000, (long)Assert.Single(Acts(Run("idle 3 min stays inside")))["active_ms"]!);
        Assert.Equal(180_000, (long)Assert.Single(Acts(Run("idle 3 min stays inside")))["idle_ms"]!);
        var split = Acts(Run("idle 6 min splits"));
        Assert.Equal(2, split.Count);
        Assert.Equal(200_000, (long)split[0]["end_ms"]!);
        var excluded = Acts(Run("excluded app in the middle"));
        Assert.Equal(200_000 + 240_000, excluded.Sum(x => (long)x["active_ms"]!));
    }

    [Fact]
    public void Gaps_close_and_marathons_chunk_and_no_activity_spans_an_agent_stop()
    {
        var gap = Acts(Run("agent crash gap"));
        Assert.Equal(2, gap.Count);
        Assert.True((long)gap[0]["end_ms"]! <= 300_000 && (long)gap[1]["start_ms"]! >= 1_300_000);
        var marathon = Acts(Run("marathon chunks at 60 min"));
        Assert.Equal([3_600_000L, 3_600_000L, 1_800_000L], marathon.Select(x => (long)x["active_ms"]!));
        Assert.Equal(0.5, (double)Assert.Single(Acts(Run("job running share")))["job_frac"]!); // job on 100–400 s of 600 s
    }

    [Fact]
    public void Windows_are_15_active_minutes_and_sessions_split_at_long_idle()
    {
        var w = Run("workflow windows of 15 active minutes")["windows"]!.AsArray();
        Assert.Equal([900_000L, 900_000L, 900_000L], w.Select(x => (long)x!["active_ms"]!));
        Assert.Empty(Run("short window dropped")["windows"]!.AsArray());
        Assert.Equal(2, Run("two sessions split by long idle")["sessions"]!.AsArray().Count);
    }

    [Fact]
    public void Tokens_are_dwell_weighted_without_suffixes_redaction_or_stopwords_and_with_the_domain()
    {
        var a = Assert.Single(Acts(Run("title changes weight tokens")));
        var tokens = a["tokens"]!.AsArray().ToDictionary(t => (string)t![0]!, t => (long)t![1]!);
        Assert.Equal(600_000, tokens["dom:github.com"]);
        Assert.Equal(320_000, tokens["request"]);
        Assert.Equal(280_000, tokens["failing"]);
        Assert.DoesNotContain("google", tokens.Keys);
        var r = Acts(Run("redaction and suffix tokens"));
        var all = r.SelectMany(x => x!["tokens"]!.AsArray().Select(t => (string)t![0]!)).ToList();
        Assert.DoesNotContain(all, t => t is "num" or "email" or "path" or "visual" or "code" or "for");
        Assert.Contains("invoice", all);
        Assert.Contains("notes", all);
    }
}
