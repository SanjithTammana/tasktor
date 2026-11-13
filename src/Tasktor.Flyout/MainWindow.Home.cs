using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tasktor.Contracts.Cards;
using Tasktor.Hosts.Common.Deep;
using Tasktor.Hosts.Common.Render;
using Tasktor.Hosts.Common.Shelf;

namespace Tasktor.Flyout;

public sealed partial class MainWindow
{
    // Home uses the same real cards and actions as the other surfaces, arranged around the approved two-column board.
    private void DrawHome(IReadOnlyList<Card> cards, BindingResolver resolver)
    {
        if (!_snapshot.Connected)
        {
            PageContent.Children.Add(Text("Tasktor is not running.", "TasktorInk2Brush"));
            return;
        }

        var summary = cards.Count == 0 ? "Waiting for Tasktor's readings…" : ShelfComposer.Compose(cards, resolver).Text;
        PageContent.Children.Add(new TextBlock
        {
            Text = summary,
            Style = StyleOf("TasktorSubtitleTextStyle"),
            Foreground = ThemeBrush("TasktorInkBrush"),
            TextWrapping = TextWrapping.Wrap,
        });

        var left = new StackPanel { Spacing = 10 };
        var right = new StackPanel { Spacing = 10 };
        left.Children.Add(SystemHeading("Needs you"));
        var alerts = _snapshot.Mirror.Root?["state"]?["alerts"] as JsonArray;
        if (alerts is { Count: > 0 })
        {
            var alertRows = new StackPanel { Spacing = 2 };
            foreach (var alert in alerts.OfType<JsonObject>().Reverse())
            {
                var severity = alert["severity"]?.ToString() == "critical" ? "Critical" : "Caution";
                var detail = alert["text"]?.ToString() ?? alert["rule"]?.ToString() ?? string.Empty;
                alertRows.Children.Add(SystemSurface(Line(severity, detail, "TasktorInkBrush")));
            }

            left.Children.Add(alertRows);
        }
        else
        {
            left.Children.Add(Text("No active alerts.", "TasktorInk2Brush"));
        }

        var pinned = DeepPages.Pinned(_snapshot.Mirror.Root);
        var shown = DeepPages.CardsFor(DeepPages.Home, cards, pinned);
        var renderer = new RowRenderer(DateTimeOffset.Now);
        if (shown.Count == 0)
        {
            left.Children.Add(SystemHeading("Pinned"));
            left.Children.Add(Text("No pinned cards are available.", "TasktorInk2Brush"));
        }

        string? previousGroup = null;
        foreach (var card in shown)
        {
            // Retain the user's pin order, including plugin cards, even when the jobs card is interleaved.
            var group = card.Id == "core.jobs" ? "Running" : "Pinned";
            if (group != previousGroup)
            {
                left.Children.Add(SystemHeading(group));
                previousGroup = group;
            }

            var rows = renderer.Render(CardFitter.Fit(card, FitTarget.Flyout, resolver), resolver);
            left.Children.Add(Section(rows with { Actions = [.. rows.Actions, DeepPages.PinAction(card.Id, pinned)] }));
        }

        right.Children.Add(SystemHeading("Last hour"));
        var history = new StackPanel { Spacing = 2 };
        foreach (var (id, label) in TimelineStrip.Series)
        {
            history.Children.Add(Chart(new CardRow(RowKind.Chart, label, Series: id)));
        }

        right.Children.Add(SystemSurface(history));
        right.Children.Add(SystemHeading("Recent actions"));
        if (HomePage.Undo(_snapshot.Mirror.Root?["state"]?["recent_actions"], DateTimeOffset.Now) is { } undo)
        {
            var actions = new StackPanel { Spacing = 2 };
            for (var i = 0; i < Math.Min(undo.Rows.Count, undo.Actions.Count); i++)
            {
                var row = undo.Rows[i];
                var action = undo.Actions[i];
                var body = new Grid { ColumnSpacing = 12 };
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var text = new StackPanel();
                text.Children.Add(Text(row.Label, "TasktorInkBrush"));
                if (row.Cells is { Count: > 1 }) text.Children.Add(Caption(row.Cells[1], "TasktorInk2Brush"));
                body.Children.Add(text);
                var button = new HyperlinkButton { Content = "Undo", Foreground = ThemeBrush("TasktorAccentBrush") };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, action.Label);
                button.Click += async (_, _) => await RunAsync(action);
                Grid.SetColumn(button, 1);
                body.Children.Add(button);
                actions.Children.Add(SystemSurface(body));
            }

            right.Children.Add(actions);
        }
        else
        {
            right.Children.Add(Text("No recent actions to undo.", "TasktorInk2Brush"));
        }

        var columns = new Grid { ColumnSpacing = 16, RowSpacing = 24 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(right, 1);
        columns.Children.Add(left);
        columns.Children.Add(right);
        columns.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 680;
            columns.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(right, narrow ? 0 : 1);
            Grid.SetRow(right, narrow ? 1 : 0);
        };
        PageContent.Children.Add(columns);
    }
}
