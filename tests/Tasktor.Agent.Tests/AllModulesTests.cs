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
}
