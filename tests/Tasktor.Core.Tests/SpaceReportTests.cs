using Tasktor.Contracts.Actions;
using Tasktor.Core.Features.Space;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>U2 I7: named caches from env vars then defaults then (last) the CLI; report on request, 24 h cache, never deletes.</summary>
public sealed class SpaceReportTests : IDisposable
{
    private readonly FeatureRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void Caches_resolve_env_first_then_defaults_and_only_existing_paths_show()
    {
        static string P(params string[] parts) => Path.Combine(parts); // the locator joins with the OS separator
        var env = new Dictionary<string, string> { ["HF_HOME"] = "/data/hf", ["NUGET_PACKAGES"] = "/nope" };
        var exists = new HashSet<string> { P("/data/hf", "hub"), P("/home/u", ".nuget", "packages"), P("/home/u", ".m2", "repository"), P("/la", "pip", "Cache") };
        var cli = new List<IReadOnlyList<string>>();
        var loc = new CacheLocator(env.GetValueOrDefault, k => k switch { "UserProfile" => "/home/u", "LocalAppData" => "/la", _ => null }, exists.Contains, argv =>
        {
            cli.Add(argv);
            return argv[0] == "npm" ? "/npm/cache\n" : null;
        });
        exists.Add("/npm/cache");
        var found = loc.Locate();
        Assert.Equal(["pip", "npm", "Hugging Face", "Maven", "NuGet"], found.Select(c => c.Name));
        Assert.Equal(P("/data/hf", "hub"), found.Single(c => c.Name == "Hugging Face").Path);
        Assert.Equal(P("/home/u", ".nuget", "packages"), found.Single(c => c.Name == "NuGet").Path); // env path missing → default
        Assert.Equal("/npm/cache", found.Single(c => c.Name == "npm").Path); // from the CLI only because nothing else existed
        Assert.DoesNotContain(cli, a => a[0] == "pip"); // pip's default existed: its CLI was never asked
    }

    [Fact]
    public void The_report_measures_on_request_caches_24_hours_and_offers_only_the_tools_own_command()
    {
        var root = _rig.Store.Root;
        var pip = Directory.CreateDirectory(Path.Combine(root, "la", "pip", "Cache")).FullName;
        File.WriteAllBytes(Path.Combine(pip, "wheel.bin"), new byte[3000]);
        var downloads = Directory.CreateDirectory(Path.Combine(root, "Downloads")).FullName;
        File.WriteAllBytes(Path.Combine(downloads, "big.iso"), new byte[5000]);
        var loc = new CacheLocator(_ => null, k => k == "LocalAppData" ? Path.Combine(root, "la") : null, Directory.Exists);
        var report = _rig.Add(new SpaceReport(_rig.Context, loc) { Folder = k => k == "Downloads" ? downloads : null, RecycleBinBytes = () => 1234 });
        Assert.Null(report.Last);

        var r = _rig.Act(new ActionRequest { Action = "space.report", Origin = Origin.Flyout });
        Assert.Equal(ActionStatus.Ok, r.Status);
        _rig.Loop.RunPending();
        Assert.Equal([("Downloads", 5000L), ("pip", 3000L), ("Recycle Bin", 1234L)], report.Last!.Items.Select(i => (i.Name, i.Bytes)));
        Assert.True(File.Exists(report.CachePath));

        File.WriteAllBytes(Path.Combine(downloads, "more.iso"), new byte[1000]);
        _rig.Clock.Advance(TimeSpan.FromHours(23));
        _rig.Act(new ActionRequest { Action = "space.report", Origin = Origin.Flyout });
        _rig.Loop.RunPending();
        Assert.Equal(5000, report.Last.Items[0].Bytes); // served from the 24 h cache

        var copy = _rig.Act(new ActionRequest { Action = "space.copy_cleanup", Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["cache"] = "pip" } });
        Assert.Equal("pip cache purge", copy.Message);
        Assert.True(File.Exists(Path.Combine(pip, "wheel.bin"))); // never deletes
        Assert.Empty(_rig.Launched);

        _rig.Act(new ActionRequest { Action = "space.report", Origin = Origin.Flyout, Args = new Dictionary<string, string> { ["refresh"] = "true" } });
        _rig.Loop.RunPending();
        Assert.Equal(6000, report.Last!.Items[0].Bytes);
    }

    [Fact]
    public void Onedrive_counts_only_files_kept_on_this_device()
    {
        var f = new FileInfo(Path.Combine(_rig.Store.Root, "x.bin"));
        File.WriteAllBytes(f.FullName, new byte[10]);
        Assert.Equal(10, SpaceReport.HydratedSize(f));
    }
}
