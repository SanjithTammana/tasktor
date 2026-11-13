using System.Text.Json.Nodes;
using Tasktor.Contracts.Cards;

namespace Tasktor.Hosts.Common.Deep;

/// <summary>
/// Which screen the shelf and the command line open on (<c>state.screen</c>, <c>settings.screen</c>). With one screen there
/// is nothing to choose and nothing is shown. With two or more, the shelf asks once; the answer sticks and Settings →
/// General can change it.
/// </summary>
public static class ScreenSetting
{
    public const string Working = "working";
    public const string Main = "main";

    /// <summary>The two choices, in the order shown, with their labels.</summary>
    public static IReadOnlyList<(string Id, string Label)> Choices { get; } =
    [
        (Working, "The screen you're working on"),
        (Main, "Your main screen"),
    ];

    /// <summary>The shelf's one-time question.</summary>
    public const string Question = "You have more than one screen. Open Tasktor on";

    /// <summary>The user's choice, or null when they have not chosen (which behaves as <see cref="Working"/>).</summary>
    public static string? Chosen(JsonNode? root) =>
        root?["state"]?["screen"] is JsonValue v && v.TryGetValue<string>(out var s) && s is Working or Main ? s : null;

    /// <summary>The choice in effect: the user's, else <see cref="Working"/>.</summary>
    public static string Effective(JsonNode? root) => Chosen(root) ?? Working;

    /// <summary>Settings shows the row only when there is a second screen to choose.</summary>
    public static bool Offered(int screens) => screens > 1;

    /// <summary>The shelf asks while there is more than one screen, the agent is connected and nothing was chosen.</summary>
    public static bool Ask(JsonNode? root, int screens, bool connected) => connected && Offered(screens) && Chosen(root) is null;

    /// <summary>The action that keeps the choice (people only; the agent refuses anything but the two ids).</summary>
    public static CardAction Set(string screen) => new()
    {
        Id = "screen",
        Label = "Choose a screen",
        Action = "settings.screen",
        Args = new Dictionary<string, string>(StringComparer.Ordinal) { ["screen"] = screen },
    };
}
