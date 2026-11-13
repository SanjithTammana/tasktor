using System.Text.Json.Nodes;
using Tasktor.Widget;

namespace Tasktor.Widget.Tests;

public sealed class WidgetGlanceTests
{
    [Fact]
    public void Active_instances_are_tracked_independently_across_deactivation_and_reactivation()
    {
        var activity = new WidgetActivity();
        Assert.False(activity.HasActive);
        activity.Activate("one", "TasktorSystem");
        activity.Activate("two", "TasktorJobs");
        activity.Deactivate("one");
        Assert.True(activity.HasActive);
        Assert.Equal(new[] { "two" }, activity.ActiveIds());
        activity.Deactivate("two");
        Assert.False(activity.HasActive);
        activity.Activate("one", "TasktorWorkflow");
        Assert.True(activity.IsActive("one"));
        Assert.Equal("TasktorWorkflow", activity.Definition("one"));
    }

    [Fact]
    public async Task Deactivation_cancels_inflight_work_and_wakes_the_loop()
    {
        var activity = new WidgetActivity();
        activity.Activate("one", "TasktorSystem");
        Assert.True(await activity.WaitForChangeAsync(TimeSpan.Zero));
        var snapshot = activity.Capture();
        var pull = Task.Delay(Timeout.InfiniteTimeSpan, snapshot.Token);
        var wake = activity.WaitForChangeAsync(TimeSpan.FromSeconds(5));

        activity.Deactivate("one");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pull);
        Assert.True(await wake);
        Assert.False(activity.IsCurrent(snapshot));
        Assert.Empty(activity.Capture().Instances);
    }

    [Fact]
    public void Reactivation_invalidates_old_snapshots_even_for_the_same_id()
    {
        var activity = new WidgetActivity();
        activity.Activate("one", "TasktorSystem");
        var old = activity.Capture();
        activity.Deactivate("one");
        activity.Activate("one", "TasktorJobs");
        var current = activity.Capture();

        Assert.True(old.Token.IsCancellationRequested);
        Assert.False(activity.IsCurrent(old));
        Assert.True(activity.IsCurrent(current));
        Assert.Equal("TasktorJobs", Assert.Single(current.Instances).Value);
    }
    [Fact]
    public void Definitions_use_their_own_safe_aggregates_and_never_copy_names()
    {
        var snapshot = JsonNode.Parse("""{"state":{"system":{"cpu_pct":34},"jobs":[{"label":"PrivateJob","status":"working"},{"label":"PrivateJob2","status":"silent"},{"status":"done"}],"workflow":{"name":"PrivateWorkflow","switches_today":4}}}""");
        var jobs = WidgetGlance.Render(true, snapshot, null, "TasktorJobs");
        var workflow = WidgetGlance.Render(true, snapshot, null, "TasktorWorkflow");
        Assert.Equal("2 active jobs. 1 silent.", jobs.Data["sentence"]!.GetValue<string>());
        Assert.Equal("4 workflow switches today.", workflow.Data["sentence"]!.GetValue<string>());
        Assert.DoesNotContain("Private", jobs.DataJson + workflow.DataJson);
        Assert.Equal("Widget data is unavailable.", WidgetGlance.Render(true, snapshot, null, "unknown").Data["sentence"]!.GetValue<string>());
    }

    [Fact]
    public void Missing_or_unrecognized_definition_data_is_unavailable()
    {
        var snapshot = JsonNode.Parse("""{"state":{"jobs":[{"status":"new_status"}],"workflow":{"switches_today":-1}}}""");
        Assert.Equal("Job readings are unavailable.", WidgetGlance.Render(true, snapshot, null, "TasktorJobs").Data["sentence"]!.GetValue<string>());
        Assert.Equal("Workflow readings are unavailable.", WidgetGlance.Render(true, snapshot, null, "TasktorWorkflow").Data["sentence"]!.GetValue<string>());
        Assert.Equal("Job readings are unavailable.", WidgetGlance.Render(true, JsonNode.Parse("""{"state":{}}"""), null, "TasktorJobs").Data["sentence"]!.GetValue<string>());
    }

    [Fact]
    public void Connected_snapshot_uses_only_aggregate_values_even_when_host_is_locked()
    {
        var snapshot = JsonNode.Parse("""{"state":{"system":{"cpu_pct":34,"ram_used_pct":86},"apps":[{"name":"PrivateApp"}],"jobs":[{"name":"PrivateJob"}]}}""");
        var series = JsonNode.Parse("""{"hour":[10,40,70]}""");
        var output = WidgetGlance.Render(true, snapshot, series);
        Assert.Equal("CPU 34%. Memory 86%.", output.Data["sentence"]!.GetValue<string>());
        Assert.True(output.Data["hasStrip"]!.GetValue<bool>());
        Assert.DoesNotContain("PrivateApp", output.DataJson);
        Assert.DoesNotContain("PrivateJob", output.DataJson);
        Assert.DoesNotContain("Action.Execute", output.TemplateJson);
    }

    [Fact]
    public void Disconnect_drops_cached_readings_and_history()
    {
        var snapshot = JsonNode.Parse("""{"state":{"system":{"cpu_pct":34,"ram_used_pct":86}}}""");
        var output = WidgetGlance.Render(false, snapshot, JsonNode.Parse("""{"hour":[75]}"""));
        Assert.Equal("Tasktor isn't running.", output.Data["sentence"]!.GetValue<string>());
        Assert.Equal(string.Empty, output.Data["strip"]!.GetValue<string>());
        Assert.False(output.Data["hasStrip"]!.GetValue<bool>());
    }

    [Fact]
    public void Missing_or_invalid_aggregate_readings_are_not_presented_as_zero()
    {
        var output = WidgetGlance.Render(true, JsonNode.Parse("""{"state":{"system":{"cpu_pct":-1,"ram_used_pct":101}}}"""), null);
        Assert.Equal("System readings are unavailable.", output.Data["sentence"]!.GetValue<string>());
    }
}
