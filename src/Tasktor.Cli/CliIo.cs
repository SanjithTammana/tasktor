using Tasktor.Contracts.Platform;
using Tasktor.Core.Time;

namespace Tasktor.Cli;

/// <summary>Where the CLI reads and writes, and how it finds the agent and the store (tests inject their own).</summary>
public sealed record CliIo
{
    public required TextWriter Out { get; init; }

    public required TextWriter Err { get; init; }

    public TextReader In { get; init; } = TextReader.Null;

    public string? PipeName { get; init; }

    public string? DataDir { get; init; }

    public IClock Clock { get; init; } = SystemClock.Instance;

    public static CliIo Console() => new() { Out = System.Console.Out, Err = System.Console.Error, In = System.Console.In };
}

/// <summary>Exit codes (U1 D-H4).</summary>
public static class ExitCode
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int Refused = 2;
    public const int AgentDown = 3;
    public const int NeedsConfirm = 4;
}
