namespace Tasktor.Capture.Deep;

/// <summary>One low-level input as the hook copies it (D-C19): five numbers, no text. The ring holds these.</summary>
public readonly record struct RawInput(ushort Vk, ushort Scan, uint Flags, long TimeMs, bool KeyUp)
{
    public const uint Injected = 0x10;          // LLKHF_INJECTED
    public const uint LowerIlInjected = 0x02;   // LLKHF_LOWER_IL_INJECTED
    public const uint Extended = 0x01;          // LLKHF_EXTENDED

    public bool IsInjected => (Flags & Injected) != 0;

    public bool IsLowerIlInjected => (Flags & LowerIlInjected) != 0;
}

/// <summary>What one decoded key does to the run.</summary>
public enum KeyAction : byte
{
    None,
    Text,
    Backspace,
    Delete,
    Enter,
    Tab,
    Navigate,
    Chord,
}

/// <summary>A decoded key: text, an edit, a navigation key or a chord (<c>Ctrl+S</c>), with the injected flags kept.</summary>
public readonly record struct DecodedKey(KeyAction Action, string? Text, long TimeMs, bool Injected, bool LowerIlInjected);

/// <summary>Why a run stores only its length and duration (D-C19 suppression; D-G6 blocks).</summary>
public enum Suppression : byte
{
    None,
    Password,
    SecureDesktop,
    SecureField,
    Excluded,
    OwnWindow,
    Sensitive,
    Blocked,
}

/// <summary>How a run ended.</summary>
public enum RunEnd : byte
{
    Pause,
    FocusOut,
    Enter,
    Tab,
    Flush,
}

/// <summary>D-C17 actors. A name is never guessed: injected input from an unknown source is <see cref="Injected"/>.</summary>
public enum ActorKind : byte
{
    Human,
    Agent,
    Cli,
    Injected,
    App,
    System,
    Unknown,
}

/// <summary>The evidence behind an actor label (bits are kept on the record so a label can be audited).</summary>
[Flags]
public enum ActorEvidence : ushort
{
    None = 0,
    InjectedFlag = 1,
    LowerIlInjected = 2,
    AgentActive = 4,
    ShellParent = 8,
    EnterBefore = 16,
    AgentAncestor = 32,
    Hardware = 64,
}

/// <summary>An actor label: kind, the agent or shell exe it ran under (never a person), and its evidence.</summary>
public readonly record struct Actor(ActorKind Kind, string? Via, ActorEvidence Evidence)
{
    public override string ToString() => Via is null ? Kind.ToString().ToLowerInvariant() : Kind.ToString().ToLowerInvariant() + ":" + Via;
}

/// <summary>
/// A closed key run (D-C19 / C17): typed text after edits (null when suppressed), counts, chords, how it ended and who
/// drove it. <see cref="Text"/> has already passed the redactor.
/// </summary>
public sealed record KeyRun
{
    public required long Id { get; init; }

    public required string View { get; init; }

    public required string ElementRef { get; init; }

    public required long StartMs { get; init; }

    public required long EndMs { get; init; }

    public string? Text { get; init; }

    public required int Keys { get; init; }

    public int Backspaces { get; init; }

    public IReadOnlyList<string> Chords { get; init; } = [];

    public RunEnd End { get; init; }

    public Actor Actor { get; init; }

    public Suppression Suppressed { get; init; }

    public IReadOnlyDictionary<string, int> Redactions { get; init; } = new Dictionary<string, int>();
}
