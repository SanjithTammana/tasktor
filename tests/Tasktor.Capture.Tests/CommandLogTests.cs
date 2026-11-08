using System.Text;
using Tasktor.Capture.Commands;
using Tasktor.Capture.Deep;
using Tasktor.Capture.Recording;
using Tasktor.Contracts.Platform;

namespace Tasktor.Capture.Tests;

/// <summary>C22 CLI command log: PSReadLine history tail, reconciled with Enter-terminated runs; scrubbed and block-checked.</summary>
public sealed class CommandLogTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly Actor Human = new(ActorKind.Human, null, ActorEvidence.Hardware);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tasktor-cmd-" + Guid.NewGuid().ToString("N")[..8]);

    public CommandLogTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void The_history_tail_reads_only_new_commands_joins_continuations_and_survives_truncation()
    {
        var path = Path.Combine(_dir, "ConsoleHost_history.txt");
        File.WriteAllText(path, "old command before capture\n");
        var tail = new HistoryTail(path);
        Assert.Empty(tail.ReadNew()); // the past is never imported

        File.AppendAllText(path, "git status\nGet-ChildItem `\n  -Recurse\npartial");
        Assert.Equal(["git status", "Get-ChildItem \n  -Recurse"], tail.ReadNew().Select(c => c.Reveal()));
        File.AppendAllText(path, " line\n");
        Assert.Equal(["partial line"], tail.ReadNew().Select(c => c.Reveal())); // a line counts once it ends

        File.AppendAllText(path, "dotnet test\n");
        Assert.Equal(["dotnet test"], tail.ReadNew().Select(c => c.Reveal()));
        Assert.Empty(new HistoryTail(Path.Combine(_dir, "missing.txt")).ReadNew());
    }

    [Fact]
    public void A_trimmed_history_imports_nothing_old_and_a_recreated_one_keeps_its_first_new_command()
    {
        var path = Path.Combine(_dir, "ConsoleHost_history.txt");
        File.WriteAllText(path, "older one\nolder two\n"); // before capture: known, never imported
        var tail = new HistoryTail(path);
        File.AppendAllText(path, "git status\n");
        Assert.Equal(["git status"], tail.ReadNew().Select(c => c.Reveal()));

        // PSReadLine trims the file to its newest entries (all seen before): nothing is imported.
        File.WriteAllText(path, "older two\ngit status\n");
        Assert.Empty(tail.ReadNew());
        // ...and what is appended after the trim is new.
        File.AppendAllText(path, "dotnet build\n");
        Assert.Equal(["dotnet build"], tail.ReadNew().Select(c => c.Reveal()));

        // A trim and a new command between two reads: only the command after the last known line.
        File.WriteAllText(path, "git status\ndotnet build\nnpm test\n");
        Assert.Equal(["npm test"], tail.ReadNew().Select(c => c.Reveal()));

        // The file deleted and recreated by the next command: nothing in it was seen, so all of it is new.
        File.Delete(path);
        File.WriteAllText(path, "Get-Date\n");
        Assert.Equal(["Get-Date"], tail.ReadNew().Select(c => c.Reveal()));
        File.AppendAllText(path, "whoami\n");
        Assert.Equal(["whoami"], tail.ReadNew().Select(c => c.Reveal())); // and the tail carries on from there
    }

    [Fact]
    public void A_restored_old_history_file_is_never_imported()
    {
        var path = Path.Combine(_dir, "ConsoleHost_history.txt");
        var backup = Path.Combine(_dir, "backup.txt");
        File.WriteAllText(backup, "secret-era command one\nsecret-era command two\n"); // an old file the tail never saw
        File.SetLastWriteTimeUtc(backup, DateTime.UtcNow.AddDays(-30));
        File.WriteAllText(path, "current command\n");
        var tail = new HistoryTail(path);

        File.Copy(backup, path, overwrite: true); // a copy keeps the old last-write time
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
        Assert.Empty(tail.ReadNew());
        File.AppendAllText(path, "after restore\n"); // the tail carries on from the restored file's end
        Assert.Equal(["after restore"], tail.ReadNew().Select(c => c.Reveal()));

        // Freshly written but holding more than could have been typed since the last read: not imported either.
        File.WriteAllText(path, string.Join('\n', Enumerable.Range(0, HistoryTail.MaxFresh + 1).Select(i => "bulk " + i)) + "\n");
        Assert.Empty(tail.ReadNew());
    }

    [Fact]
    public void History_wins_over_a_typed_run_within_a_second_and_each_alone_still_counts()
    {
        var r = new CommandReconciler();
        r.Typed("pwsh.exe", Sensitive.Of("git stauts"), T0, Human); // typo the history line fixes (edited before Enter)
        r.History(Sensitive.Of("git status"), T0.AddMilliseconds(300));
        r.Typed("cmd.exe", Sensitive.Of("dir"), T0.AddSeconds(5), Human); // cmd has no history file
        r.History(Sensitive.Of("Get-Date"), T0.AddSeconds(10)); // no keystrokes seen (hook off): history alone
        Assert.Empty(r.Flush(T0.AddMilliseconds(500))); // still within the second: wait for a match

        var all = r.Flush(T0.AddSeconds(20));
        Assert.Equal(
            [("pwsh.exe", "git status", CommandSource.Both), ("cmd.exe", "dir", CommandSource.Typed), (null, "Get-Date", CommandSource.History)],
            all.Select(c => (c.App, c.Text.Reveal(), c.Source)));
        Assert.Empty(r.Flush(T0.AddSeconds(30)));
    }

    [Fact]
    public void Commands_are_scrubbed_block_checked_and_join_the_activity_document()
    {
        var blocks = new CaptureBlocks(Path.Combine(_dir, "blocks.json"), TimeZoneInfo.Utc);
        var vault = new RawVault(Path.Combine(_dir, "raw"), new byte[32]);
        var now = T0;
        var recorder = new CaptureRecorder(vault, blocks, () => now);
        var runs = new KeyRunBuilder(new KeyDecoder(), new ActorAttributor(() => []), new SecretRedactor(), (new Suppressor(blocks) { SecureDesktop = () => false }).Check, ms => T0.AddMilliseconds(ms));
        var deep = new DeepCapture(new InputRing(), runs, new SnapshotDiffer(), new SecretRedactor(), recorder, () => now, () => 0);
        deep.Foreground("windowsterminal.exe");

        deep.Command(new CommandEvent(T0, "pwsh.exe", Sensitive.Of("curl -H \"Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789ABCD\" https://api.example.com"), CommandSource.History, Human));
        blocks.Add(new CaptureBlock { Id = "", Kind = BlockKind.App, Value = "powershell.exe", AddedBy = "user" });
        deep.Command(new CommandEvent(T0, null, Sensitive.Of("secret-admin-thing"), CommandSource.History, Human)); // history alone: either PowerShell's block applies
        deep.CloseActivity();

        var bodies = vault.Read(T0.AddDays(-1), T0.AddDays(1)).Select(r => (r.Kind, Body: Encoding.UTF8.GetString(r.Body))).ToList();
        var cmd = Assert.Single(bodies, b => b.Kind == RecordKind.Command).Body;
        Assert.Contains("curl", cmd, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz0123456789ABCD", cmd, StringComparison.Ordinal);
        Assert.DoesNotContain(bodies, b => b.Body.Contains("secret-admin-thing", StringComparison.Ordinal));
        Assert.Contains(bodies, b => b.Kind == RecordKind.Document && b.Body.Contains("ran", StringComparison.Ordinal) && b.Body.Contains("curl", StringComparison.Ordinal));
    }

    [Fact]
    public void An_enter_in_a_terminal_meets_its_history_line_and_an_enter_in_an_editor_is_not_a_command()
    {
        var blocks = new CaptureBlocks(Path.Combine(_dir, "blocks.json"), TimeZoneInfo.Utc);
        var vault = new RawVault(Path.Combine(_dir, "raw"), new byte[32]);
        var ring = new InputRing(1024);
        long ms = 0;
        DateTimeOffset Now() => T0.AddMilliseconds(ms);
        var recorder = new CaptureRecorder(vault, blocks, Now);
        var runs = new KeyRunBuilder(new KeyDecoder(), new ActorAttributor(() => []), new SecretRedactor(), (new Suppressor(blocks) { SecureDesktop = () => false }).Check, m => T0.AddMilliseconds(m));
        var deep = new DeepCapture(ring, runs, new SnapshotDiffer(), new SecretRedactor(), recorder, Now, () => ms);

        void TypeLine(string text)
        {
            foreach (var k in Keys.Type(text, ms))
            {
                ring.Write(k);
                ms = Math.Max(ms, k.TimeMs);
            }

            ms += 50;
            ring.Write(Keys.Down(0x0D, ms));
            ring.Write(Keys.Up(0x0D, ms + 30));
            ms += 60;
            deep.Pump();
        }

        deep.Focus(new FocusTarget("w1", "e1", "WindowsTerminal.exe") { IsPassword = false });
        TypeLine("git stat");
        ms += 300;
        deep.History([Sensitive.Of("git status")]); // completion finished the word: history has the line as it ran
        deep.Focus(new FocusTarget("w2", "e1", "notepad.exe") { IsPassword = false });
        TypeLine("dear team");
        ms += 2000;
        deep.Pump();

        var commands = vault.Read(T0.AddDays(-1), T0.AddDays(1)).Where(r => r.Kind == RecordKind.Command).Select(r => Encoding.UTF8.GetString(r.Body)).ToList();
        var only = Assert.Single(commands);
        Assert.Contains("\"text\":\"git status\"", only, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"both\"", only, StringComparison.Ordinal);
        Assert.Contains("\"app\":\"WindowsTerminal.exe\"", only, StringComparison.Ordinal);
        Assert.Equal(1, deep.CommandsWritten);
    }
}
