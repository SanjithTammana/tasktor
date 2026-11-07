using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Tasktor.Agent.Host;
using Tasktor.Contracts.Config;
using Tasktor.Core.Storage;
using Tasktor.Tests.Shared;

namespace Tasktor.Agent.Tests;

/// <summary>
/// U2 H-S3: G-net tightens to "the agent process never connects, with every Module on". Checked two ways: no assembly
/// the agent loads references a networking assembly, and the composed agent starts and runs with every Module on.
/// </summary>
public sealed class AllModulesTests
{
    private static readonly string[] Modules = ["ports", "agents", "next_event", "deadlines", "event_mode", "meeting_guard", "folders", "connectivity", "focus", "batteries", "tracking"];

    [Theory]
    [InlineData("tasktor-core.dll")]
    [InlineData("Tasktor.Core.dll")]
    [InlineData("Tasktor.Windows.dll")]
    [InlineData("Tasktor.Contracts.dll")]
    [InlineData("Tasktor.Hosts.Common.dll")]
    public void No_agent_assembly_references_a_networking_assembly(string file)
    {
        using var pe = new PEReader(File.OpenRead(Path.Combine(AppContext.BaseDirectory, file)));
        var md = pe.GetMetadataReader();
        var refs = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();
        Assert.NotEmpty(refs);
        Assert.DoesNotContain(refs, r => r.StartsWith("System.Net", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_composed_agent_samples_by_itself()
    {
        // Regression: nothing ticked the scheduler, so the real agent never sampled (empty System card on Windows).
        using var store = new TempStore();
        await using var host = Composition.Create(store.Layout);
        host.Start();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        long runs = 0;
        while (DateTime.UtcNow < deadline && runs == 0)
        {
            runs = await host.Loop.InvokeAsync(() => host.Scheduler.Runs(Core.Features.SystemMonitor.SystemTotals.SamplerName));
            await Task.Delay(100);
        }

        Assert.True(runs > 0, "the system sampler never ran");
        var (leased, apps) = await host.Loop.InvokeAsync(() => (host.Scheduler.LeaseCount(Core.Features.SystemMonitor.Apps.SamplerName), host.Scheduler.Runs(Core.Features.SystemMonitor.Apps.SamplerName)));
        Assert.True(leased == 0 || apps > 0, "the process sampler is leased but never ran");
    }

    [Fact]
    public async Task The_composed_agent_starts_with_every_module_on_and_adds_each_feature()
    {
        using var store = new TempStore();
        new SettingsStore(store.Layout).Save(new ConfigFile { Modules = Modules.ToDictionary(m => m, m => m != "tracking") });
        await using var host = Composition.Create(store.Layout);
        host.Start();
        var ids = await host.Loop.InvokeAsync(() => host.Features.Select(f => f.Id).ToList()).WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var id in new[] { "system", "apps", "jobs", "agents", "next_event", "deadlines", "event_mode", "power_guard", "meeting_guard", "folders", "connectivity", "space", "focus", "batteries" })
        {
            Assert.Contains(id, ids);
        }

        var cards = await host.Loop.InvokeAsync(() => host.Features.SelectMany(f => f.Cards()).Select(c => c.Id).ToList()).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("core.guards", cards);
        Assert.Contains("module.focus", cards);
        Assert.Contains("module.agents", cards);
    }

    [Fact]
    public async Task Modules_start_and_stop_without_a_restart_and_dependents_are_rebuilt()
    {
        using var store = new TempStore();
        await using var host = Composition.Create(store.Layout);
        host.Start();
        async Task<Contracts.Actions.ActionResult> Act(string action, string module) =>
            await (await host.Loop.InvokeAsync(() => host.Router.Submit(new Contracts.Actions.ActionRequest { Action = action, Origin = Contracts.Actions.Origin.Flyout, Target = new Dictionary<string, string> { ["module"] = module } }))).WaitAsync(TimeSpan.FromSeconds(10));
        Task<List<string>> Ids() => host.Loop.InvokeAsync(() => host.Features.Select(f => f.Id).ToList()).WaitAsync(TimeSpan.FromSeconds(10));
        Task<List<string>> CardIds() => host.Loop.InvokeAsync(() => host.Features.SelectMany(f => f.Cards()).Select(c => c.Id).ToList()).WaitAsync(TimeSpan.FromSeconds(10));
        Task<Core.Features.Feature?> Find(string id) => host.Loop.InvokeAsync(() => host.Features.FirstOrDefault(f => f.Id == id)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.DoesNotContain("focus", await Ids());
        var on = await Act("module.enable", "focus");
        Assert.Equal("Turned on Focus.", on.Message); // no "restart" any more
        Assert.Contains("focus", await Ids());
        Assert.Contains("module.focus", await CardIds());
        Assert.True(await host.Loop.InvokeAsync(() => host.ModuleRunning("focus")));

        await Act("module.enable", "projects");
        var focusBefore = await Find("focus");
        var projectsBefore = await Find("projects");
        await Act("module.enable", "tracking");
        Assert.Same(focusBefore, await Find("focus")); // Focus asks for tracking when it needs it: a running block survives
        var projectsAfter = await Find("projects");
        Assert.NotNull(projectsAfter);
        Assert.NotSame(projectsBefore, projectsAfter); // Projects subscribes to tracking's events: rebuilt onto the new one
        Assert.False(projectsBefore!.Enabled);
        await Act("module.disable", "tracking");
        Assert.Same(focusBefore, await Find("focus"));

        Assert.Equal("Turned off Focus.", (await Act("module.disable", "focus")).Message);
        Assert.DoesNotContain("focus", await Ids());
        Assert.DoesNotContain("module.focus", await CardIds());
        Assert.False(await host.Loop.InvokeAsync(() => host.ModuleRunning("focus")));

        // Event mode is threaded through always-on plumbing: it still applies at the next start.
        Assert.Equal("Turned on Event mode. Restart Tasktor to apply it.", (await Act("module.enable", "event_mode")).Message);
        Assert.False(await host.Loop.InvokeAsync(() => host.ModuleRunning("event_mode")));
    }
}
