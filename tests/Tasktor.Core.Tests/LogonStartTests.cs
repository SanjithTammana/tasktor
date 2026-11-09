using Tasktor.Core.Platform;

namespace Tasktor.Core.Tests;

/// <summary>H8 portable build (owner, 2026-11-09: no MSIX): start at logon through one HKCU Run value, kept in line with the setting.</summary>
public sealed class LogonStartTests
{
    private string? _value;
    private int _writes;

    private LogonStart Start() => new(() => _value, v => { _value = v; _writes++; }, () => _value = null);

    [Fact]
    public void Turning_it_on_writes_the_quoted_agent_path_once()
    {
        var start = Start();
        Assert.Equal(LogonStartChange.Added, start.Apply(true, LogonStart.Command(@"C:\Apps\Tasktor\tasktor-core.exe", null)));
        Assert.Equal("\"C:\\Apps\\Tasktor\\tasktor-core.exe\"", _value);
        Assert.Equal(LogonStartChange.None, start.Apply(true, LogonStart.Command(@"C:\Apps\Tasktor\tasktor-core.exe", null)));
        Assert.Equal(1, _writes);
    }

    [Fact]
    public void A_moved_folder_rewrites_the_entry()
    {
        _value = "\"C:\\Old\\tasktor-core.exe\"";
        Assert.Equal(LogonStartChange.Updated, Start().Apply(true, LogonStart.Command(@"D:\New place\tasktor-core.exe", null)));
        Assert.Equal("\"D:\\New place\\tasktor-core.exe\"", _value);
    }

    [Fact]
    public void Turning_it_off_removes_the_entry_and_is_quiet_when_there_is_none()
    {
        _value = "\"C:\\Apps\\Tasktor\\tasktor-core.exe\"";
        var start = Start();
        Assert.Equal(LogonStartChange.Removed, start.Apply(false, LogonStart.Command(@"C:\Apps\Tasktor\tasktor-core.exe", null)));
        Assert.Null(_value);
        Assert.Equal(LogonStartChange.None, start.Apply(false, LogonStart.Command(@"C:\Apps\Tasktor\tasktor-core.exe", null)));
    }

    [Fact]
    public void A_portable_data_folder_is_passed_on_so_logon_opens_the_same_store()
    {
        Assert.Equal("\"C:\\T\\tasktor-core.exe\" --data-dir \"D:\\Tasktor data\"", LogonStart.Command(@"C:\T\tasktor-core.exe", @"D:\Tasktor data"));
        Assert.Equal("\"C:\\T\\tasktor-core.exe\"", LogonStart.Command(@"C:\T\tasktor-core.exe", null));
    }

    [Fact]
    public void A_path_with_a_quote_is_never_written()
    {
        // Windows paths cannot hold '"'; refusing it keeps the command line from being split differently than it reads.
        Assert.Throws<ArgumentException>(() => LogonStart.Command("C:\\a\"b\\tasktor-core.exe", null));
        Assert.Throws<ArgumentException>(() => LogonStart.Command(@"C:\T\tasktor-core.exe", "D:\\x\" --pipe evil"));
    }

    [Theory]
    [InlineData(null, false, false, true)]
    [InlineData("check-1", false, false, false)] // --pipe: a check instance beside the owner's agent
    [InlineData(null, true, false, false)] // --switcher-pid
    [InlineData(null, false, true, false)] // TASKTOR_PIPE in the environment
    public void Only_the_owners_own_instance_touches_the_entry(string? pipe, bool switcherPids, bool pipeEnv, bool allowed) =>
        Assert.Equal(allowed, LogonStart.AllowedFor(pipe, switcherPids ? [1u] : [], pipeEnv ? "tasktor-check" : null));
}
