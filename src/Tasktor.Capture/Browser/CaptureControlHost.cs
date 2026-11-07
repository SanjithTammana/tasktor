using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using Tasktor.Capture.Deep;
using Tasktor.Contracts;
using Tasktor.Contracts.Capture;
using Tasktor.Hosts.Common.Pipe;

namespace Tasktor.Capture.Browser;

/// <summary>
/// The capture host's side of <c>tasktor.capture-control/1</c>: the agent's card and rules ask for a pause, a resume, an
/// exclude, a tab pause or a forget. A command without <c>by_user</c> (a rule) may add blocks but never lifts one, and a
/// user's lift of a block a rule added is allowed (the user always may). Every reply is the full status. Thread-safe.
/// </summary>
public sealed class CaptureControl
{
    private readonly CaptureBlocks _blocks;
    private readonly Func<DateTimeOffset> _now;
    private readonly Lock _gate = new();

    public CaptureControl(CaptureBlocks blocks, Func<DateTimeOffset> now)
    {
        _blocks = blocks;
        _now = now;
    }

    /// <summary>Whether capture is turned on at all (the Module and its consent).</summary>
    public Func<bool> On { get; init; } = () => true;

    /// <summary>Records stored today (the vault's count).</summary>
    public Func<long> RecordsToday { get; init; } = () => 0;

    /// <summary>The browsers with a connected extension (the browser endpoint).</summary>
    public Func<IReadOnlyList<string>> Extensions { get; init; } = () => [];

    /// <summary>Forget records since a time; returns how many were removed (the vault).</summary>
    public Func<DateTimeOffset, long> ForgetSince { get; init; } = _ => 0;

    /// <summary>A tab id (or none = the active tab) → its block key (the browser endpoint knows which browser it is).</summary>
    public Func<int?, string?> ResolveTab { get; init; } = _ => null;

    /// <summary>C21 search over the content index (query, since); null when there is no index on this PC.</summary>
    public Func<string, DateTimeOffset?, IReadOnlyList<ActivityHit>?>? Search { get; init; }

    /// <summary>Raised after a block change so the browser sessions get the new state.</summary>
    public event Action? Changed;

    public CaptureStatus Handle(CaptureCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        if (cmd.Verb == CaptureVerb.Search)
        {
            // Outside the block lock: a search never holds up pause or exclude. A person's search only, never a rule's.
            if (!cmd.ByUser || cmd.Query is not { Length: > 0 and <= 200 } query)
            {
                return Status() with { Refusal = "invalid" };
            }

            return Search?.Invoke(query, cmd.Since) is { } hits ? Status() with { Hits = hits } : Status() with { Refusal = "no_index" };
        }

        string? refusal = null;
        long? forgotten = null;
        var changed = false;
        lock (_gate)
        {
            var now = _now();
            _blocks.Expire(now);
            var by = cmd.ByUser ? "user" : "rule";
            switch (cmd.Verb)
            {
                case CaptureVerb.Status:
                    break;
                case CaptureVerb.Pause when cmd.Until is null || cmd.Until > now:
                    _blocks.Add(new CaptureBlock { Id = "", Kind = BlockKind.Pause, Until = cmd.Until, AddedBy = by, AddedAt = now });
                    changed = true;
                    break;
                case CaptureVerb.Resume:
                    var pauses = _blocks.Blocks.Where(b => b.Kind == BlockKind.Pause).Select(b => b.Id).ToList();
                    refusal = !cmd.ByUser ? "rule_owned" : pauses.Count == 0 ? "not_found" : null;
                    if (refusal is null)
                    {
                        pauses.ForEach(id => _blocks.Remove(id, byUser: true));
                        changed = true;
                    }

                    break;
                case CaptureVerb.Exclude when cmd.App is { Length: > 0 } || cmd.Site is { Length: > 0 }:
                    var kind = cmd.App is not null ? BlockKind.App : BlockKind.Host;
                    var value = (cmd.App ?? cmd.Site)!.ToLowerInvariant();
                    if (!_blocks.Blocks.Any(b => b.Kind == kind && b.Value == value))
                    {
                        _blocks.Add(new CaptureBlock { Id = "", Kind = kind, Value = value, AddedBy = by, AddedAt = now });
                        changed = true;
                    }

                    break;
                case CaptureVerb.Unexclude when cmd.BlockId is { } id:
                    refusal = !cmd.ByUser ? "rule_owned" : _blocks.Blocks.Any(b => b.Id == id) ? null : "not_found";
                    if (refusal is null)
                    {
                        _blocks.Remove(id, byUser: true);
                        changed = true;
                    }

                    break;
                case CaptureVerb.TabPause:
                    if (ResolveTab(cmd.Tab) is not { } key)
                    {
                        refusal = "unknown_tab";
                    }
                    else if (!_blocks.Blocks.Any(b => b.Kind == BlockKind.Tab && b.Value == key))
                    {
                        _blocks.Add(new CaptureBlock { Id = "", Kind = BlockKind.Tab, Value = key, AddedBy = by, AddedAt = now });
                        changed = true;
                    }

                    break;
                case CaptureVerb.Forget when cmd.ByUser && cmd.Since is { } since && since <= now:
                    forgotten = ForgetSince(since);
                    break;
                case CaptureVerb.Forget:
                    refusal = "invalid"; // forget is destructive: only a person, never a rule
                    break;
                default:
                    refusal = "invalid";
                    break;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return Status() with { Refusal = refusal, Forgotten = forgotten };
    }

    public CaptureStatus Status()
    {
        lock (_gate)
        {
            var now = _now();
            _blocks.Expire(now);
            var pauses = _blocks.Blocks.Where(b => b.Kind == BlockKind.Pause).ToList();
            return new CaptureStatus
            {
                On = On(),
                PausedIndefinitely = pauses.Any(p => p.Until is null),
                PausedUntil = pauses.Count > 0 && pauses.All(p => p.Until is not null) ? pauses.Max(p => p.Until) : null,
                Blocks = [.. _blocks.Blocks.Select(b => new CaptureBlockInfo { Id = b.Id, Kind = Kind(b.Kind), Text = Text(b), Until = b.Until, ByRule = b.AddedBy != "user" })],
                RecordsToday = RecordsToday(),
                Extensions = Extensions(),
            };
        }
    }

    private static string Kind(BlockKind k) => k switch
    {
        BlockKind.Pause => "pause",
        BlockKind.Schedule => "schedule",
        BlockKind.App => "app",
        BlockKind.Host => "site",
        _ => "tab",
    };

    private static string Text(CaptureBlock b) => b.Kind switch
    {
        BlockKind.Pause => b.Until is { } u ? "Paused until " + u.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : "Paused until you resume",
        BlockKind.Schedule => "Off " + (b.Days ?? "daily") + " " + b.From + "–" + b.To,
        BlockKind.App => "Not capturing " + b.Value,
        BlockKind.Host => "Not capturing " + b.Value,
        _ => "Paused for a tab",
    };
}

/// <summary>The control pipe (<c>PipeName.ForCapture() + "-ctl"</c>): one command line in, one status line out, per connection.</summary>
public sealed class ControlPipeServer : IAsyncDisposable
{
    private readonly CaptureControl _control;
    private readonly string _name;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public ControlPipeServer(CaptureControl control, string? name = null)
    {
        _control = control;
        _name = name ?? Name();
    }

    public static string Name() => PipeName.ForCapture() + "-ctl";

    public void Start() => _loop = LoopAsync(_cts.Token);

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var line = await new CappedLineReader(pipe, 64 * 1024).ReadLineAsync(ct).ConfigureAwait(false);
                CaptureStatus reply;
                try
                {
                    reply = line is not null && JsonSerializer.Deserialize(line, ContractsJsonContext.Default.CaptureCommand) is { } cmd
                        ? _control.Handle(cmd)
                        : _control.Status() with { Refusal = "invalid" };
                }
                catch (JsonException)
                {
                    reply = _control.Status() with { Refusal = "invalid" };
                }

                await PipeFraming.WriteAsync(pipe, JsonSerializer.Serialize(reply, ContractsJsonContext.Default.CaptureStatus), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }

        _cts.Dispose();
    }
}
