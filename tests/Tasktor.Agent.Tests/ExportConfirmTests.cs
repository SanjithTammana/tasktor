using Tasktor.Agent.Host;
using Tasktor.Contracts.Actions;
using Tasktor.Tests.Shared;

namespace Tasktor.Agent.Tests;

/// <summary>U2 D-S7: window titles and capture records leave only after a confirm that names them.</summary>
public sealed class ExportConfirmTests
{
    [Fact]
    public async Task The_confirm_names_what_the_include_flags_add_and_a_plain_export_needs_none()
    {
        using var store = new TempStore();
        await using var host = new AgentHost(new AgentOptions { Store = store.Layout, PipeName = "tasktor-test-" + Guid.NewGuid().ToString("N")[..12] });
        host.Start();
        var to = Path.Combine(store.Root, "out");
        async Task<ActionResult> Export(Dictionary<string, string> args, string? confirm = null) =>
            await (await host.Loop.InvokeAsync(() => host.Router.Submit(new ActionRequest { Action = "export.data", Origin = Origin.Flyout, Args = args, Confirm = confirm }))).WaitAsync(TimeSpan.FromSeconds(20));

        var ask = await Export(new() { ["to"] = to, ["include_raw"] = "true", ["include_capture"] = "true" });
        Assert.Equal(ActionStatus.NeedsConfirm, ask.Status);
        Assert.Contains("including window titles and decrypted capture records", ask.Summary, StringComparison.Ordinal);
        Assert.Contains("window titles", (await Export(new() { ["to"] = to, ["include_raw"] = "true" })).Summary, StringComparison.Ordinal);

        var plain = await Export(new() { ["to"] = to });
        Assert.Equal(ActionStatus.Ok, plain.Status);
        Assert.StartsWith("Exported ", plain.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(to, "manifest.json")));
    }

    [Fact]
    public async Task Forgetting_capture_records_without_the_capture_host_refuses_instead_of_reporting_none()
    {
        using var store = new TempStore();
        await using var host = new AgentHost(new AgentOptions { Store = store.Layout, PipeName = "tasktor-test-" + Guid.NewGuid().ToString("N")[..12] });
        host.Start();
        var r = await (await host.Loop.InvokeAsync(() => host.Router.Submit(new ActionRequest { Action = "capture.forget", Origin = Origin.Flyout, Args = new Dictionary<string, string> { ["since"] = "1h" } }))).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(ActionStatus.Refused, r.Status);
        Assert.Contains("Capture is not running", r.Message, StringComparison.Ordinal);
    }
}
