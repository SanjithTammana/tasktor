using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Rules;
using Tasktor.Core.Apps;

namespace Tasktor.Core.Tests;

public sealed class AppGroupingTests
{
    private static long _t = 1000;

    private static ProcessRow P(uint pid, uint parent, string exe, double mb = 100, bool own = true, string? path = "C:/apps/x.exe", long? created = null, bool elevated = false) => new()
    {
        Key = new ProcKey(pid, created ?? _t++),
        ParentPid = parent,
        Exe = exe,
        Path = path,
        OwnUser = own,
        PrivateBytes = mb * 1024 * 1024,
        Elevated = elevated,
    };

    /// <summary>The A2 acceptance fixture: Chrome + renderers, Teams + WebView2, terminal + node.</summary>
    private static List<ProcessRow> Fixture() =>
    [
        P(4, 0, "System", own: false, path: null),
        P(500, 4, "services.exe", own: false, path: null),
        P(600, 500, "svchost.exe", own: false, path: null),
        P(1000, 900, "explorer.exe"),
        P(1100, 1000, "chrome.exe", 300),
        P(1101, 1100, "chrome.exe", 200),
        P(1102, 1100, "chrome.exe", 150),
        P(1103, 1100, "crashpad_handler.exe", 5),
        P(1200, 1000, "ms-teams.exe", 400),
        P(1201, 1200, "msedgewebview2.exe", 100),
        P(1202, 1201, "msedgewebview2.exe", 80),
        P(1300, 1000, "windowsterminal.exe", 60),
        P(1301, 1300, "openconsole.exe", 10),
        P(1302, 1300, "pwsh.exe", 50),
        P(1303, 1302, "node.exe", 700),
        P(1304, 1303, "node.exe", 200),
        P(1400, 600, "searchhost.exe", 90),
        P(1401, 1400, "msedgewebview2.exe", 40),
    ];

    private static Dictionary<string, AppGroup> Group(List<ProcessRow> rows, AppIdentity? id = null)
    {
        var cpu = rows.Select((_, i) => (double)i).ToArray();
        return new AppGrouper(id ?? new AppIdentity()).Group(rows, cpu).ToDictionary(g => g.AppId);
    }

    [Fact]
    public void Fixture_tree_groups_as_the_rules_say()
    {
        var g = Group(Fixture());
        Assert.Equal(4, g["chrome.exe"].Count); // renderers + crashpad fold in
        Assert.Equal(3, g["ms-teams.exe"].Count); // WebView2 → host app
        Assert.Equal(5, g["windowsterminal.exe"].Count); // pwsh + node count under the launching app
        Assert.Equal(2, g["searchhost.exe"].Count);
        Assert.Equal(3, g[AppGrouper.SystemAppId].Count);
        Assert.Equal(AppGrouper.SystemName, g[AppGrouper.SystemAppId].Name);
        Assert.True(g.ContainsKey("explorer.exe")); // launchers never swallow children
        Assert.Equal(1, g["explorer.exe"].Count);
    }

    [Fact]
    public void Group_columns_are_the_sum_of_members()
    {
        // A2 acceptance 2.
        var rows = Fixture();
        var g = Group(rows);
        Assert.Equal((300 + 200 + 150 + 5) * 1024.0 * 1024, g["chrome.exe"].PrivateBytes);
        Assert.Equal(4 + 5 + 6 + 7, g["chrome.exe"].CpuPct); // cpu = row index
        Assert.Equal(rows.Sum(r => r.PrivateBytes), g.Values.Sum(x => x.PrivateBytes));
    }

    [Fact]
    public void A_user_rule_overrides_a_default()
    {
        // A2 acceptance 4.
        var id = new AppIdentity([new GroupingRule { Match = new GroupingMatch { Exe = "node.exe" }, Attach = "never" }]);
        var g = Group(Fixture(), id);
        Assert.Equal(3, g["windowsterminal.exe"].Count);
        Assert.Equal(2, g["node.exe"].Count); // "never" makes each node its own anchor; both share the node.exe app id
    }

    [Fact]
    public void Group_as_names_its_own_group()
    {
        var id = new AppIdentity([new GroupingRule { Match = new GroupingMatch { Exe = "chrome*.exe" }, GroupAs = "Browser" }]);
        var g = Group(Fixture(), id);
        Assert.Equal("Browser", g["group:browser"].Name);
        Assert.Equal(4, g["group:browser"].Count); // 3 browser processes + the crash-handler helper folded in
    }

    [Fact]
    public void Pid_reuse_guard_ignores_a_younger_parent()
    {
        var rows = new List<ProcessRow>
        {
            P(10, 1, "app.exe", created: 500),
            P(11, 10, "app.exe", created: 100), // claims pid 10 as parent, but 10 was created later
        };
        var grouper = new AppGrouper(new AppIdentity());
        grouper.Group(rows, [0, 0]);
        Assert.Equal(1, grouper.AnchorOf(1));
    }

    [Fact]
    public void Elevated_members_are_flagged()
    {
        var rows = new List<ProcessRow> { P(10, 1, "tool.exe"), P(11, 10, "tool.exe", elevated: true) };
        var g = Group(rows);
        Assert.True(g["tool.exe"].AnyElevated);
        Assert.False(g["tool.exe"].AllElevated);
    }

    [Fact]
    public void Identity_is_resolved_once_per_distinct_exe()
    {
        var id = new AppIdentity();
        var rows = Fixture();
        new AppGrouper(id).Group(rows, new double[rows.Count]);
        var distinct = rows.Select(r => r.Exe + "|" + r.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.Equal(distinct, id.CacheSize);
    }

    [Theory]
    [InlineData("code*.exe", "Code-Insiders.exe", true)]
    [InlineData("*.pdf", "report.PDF", true)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "abbc", false)]
    [InlineData("*x*y", "axby", true)]
    [InlineData("python*.exe", "pythonw.exe", true)]
    [InlineData("python*.exe", "node.exe", false)]
    public void Glob_matches_case_insensitively(string pattern, string text, bool expected) =>
        Assert.Equal(expected, Glob.IsMatch(pattern, text));

    [Fact]
    public void Denylist_covers_the_critical_processes()
    {
        foreach (var exe in new[] { "csrss.exe", "lsass.exe", "dwm.exe", "explorer.exe", "tasktor-core.exe" })
        {
            Assert.True(AppIdentity.IsCritical(exe));
        }

        Assert.False(AppIdentity.IsCritical("chrome.exe"));
    }
}
