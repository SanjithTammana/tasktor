using Tasktor.Contracts.Snapshot;

namespace Tasktor.Contracts.Plugins;

/// <summary>
/// U4 D-G3 Settings → Plugins (U1 D-H7 consent): each plugin folder with its state and what its consent dialog must show:
/// name, author, what it reads, what it contacts, its actions and the exact command line.
/// </summary>
public sealed record PluginsReport
{
    public required IReadOnlyList<PluginInfo> Plugins { get; init; }

    /// <summary>Packs found (D-H7: a pack gets one consent dialog for all its plugins).</summary>
    public IReadOnlyList<PackInfo> Packs { get; init; } = [];
}

public sealed record PackInfo
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Author { get; init; }
}

public sealed record PluginInfo
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Author { get; init; }

    /// <summary><c>found</c> (new or changed: off until the user consents), <c>ok</c>, <c>off</c> or <c>error</c>.</summary>
    public required PluginState State { get; init; }

    public string? Error { get; init; }

    public IReadOnlyList<string> Reads { get; init; } = [];

    public IReadOnlyList<string> Network { get; init; } = [];

    public IReadOnlyList<string> Actions { get; init; } = [];

    /// <summary>The exact command line it runs; null when its plugin.json could not be read.</summary>
    public string? CommandLine { get; init; }

    public string? Pack { get; init; }

    /// <summary>Contract 2: what the plugin asks to do, one consent line each (the dialog's checkboxes); null for contract 1.</summary>
    public IReadOnlyList<PluginCapabilityInfo>? Capabilities { get; init; }

    /// <summary>Contract 2: the capability names the user granted (empty until turned on).</summary>
    public IReadOnlyList<string>? Granted { get; init; }
}

/// <summary>One contract-2 capability as the consent dialog shows it.</summary>
public sealed record PluginCapabilityInfo
{
    /// <summary>The name in <c>plugin.json</c>, sent back in <c>plugin.enable</c>'s <c>granted</c> argument.</summary>
    public required string Name { get; init; }

    public required string Line { get; init; }
}
