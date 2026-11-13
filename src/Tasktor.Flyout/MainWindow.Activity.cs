using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Tasktor.Contracts.Cards;
using Tasktor.Hosts.Common.Deep;
using Tasktor.Hosts.Common.Render;

namespace Tasktor.Flyout;

public sealed partial class MainWindow
{
    private JsonNode? _activitySummary;
    private DateTimeOffset _activitySummaryAt = DateTimeOffset.MinValue;
    private bool _pullingActivitySummary;

    // The summary pull uses a local calendar day for "1d". Card actions remain the real card's actions.
    private void DrawActivity(IReadOnlyList<Card> cards, BindingResolver resolver)
    {
        if (!_snapshot.Connected)
        {
            PageContent.Children.Add(Text("Tasktor is not running.", "TasktorInk2Brush"));
            return;
        }

        if (!_pullingActivitySummary && DateTimeOffset.Now - _activitySummaryAt > TimeSpan.FromSeconds(30))
        {
            _pullingActivitySummary = true;
            _ = PullActivitySummaryAsync();
        }

        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(SystemHeading("Today so far"));
        var tracked = BindingResolver.AsDouble(_activitySummary?["active_min"]);
        var amount = Text(tracked is { } trackedMinutes ? LearnedPage.Duration(trackedMinutes * 60) + " tracked" : "—", "TasktorInk2Brush");
        Grid.SetColumn(amount, 1);
        heading.Children.Add(amount);
        PageContent.Children.Add(SystemSurface(heading));

        var left = new StackPanel { Spacing = 10 };
        var right = new StackPanel { Spacing = 10 };
        left.Children.Add(SystemHeading("By workflow"));
        var workflowRows = new StackPanel { Spacing = 2 };
        if (_activitySummary?["by_workflow"] is JsonObject byWorkflow)
        {
            foreach (var pair in byWorkflow)
            {
                if (BindingResolver.AsDouble(pair.Value) is not { } minutes) continue;
                workflowRows.Children.Add(SystemSurface(SystemRow(pair.Key, LearnedPage.Duration(minutes * 60), null, null)));
            }
        }

        if (workflowRows.Children.Count == 0)
        {
            workflowRows.Children.Add(Text(_activitySummary is null
                ? _activitySummaryAt == DateTimeOffset.MinValue ? "Reading today's activity…" : "Today's activity is unavailable."
                : "No workflow time recorded today.", "TasktorInk2Brush"));
        }

        left.Children.Add(workflowRows);
        var shown = DeepPages.CardsFor("activity", cards);
        var pinned = DeepPages.Pinned(_snapshot.Mirror.Root);
        var renderer = new RowRenderer(DateTimeOffset.Now);
        foreach (var card in shown.Where(c => c.Id == "core.workflow"))
        {
            var rows = renderer.Render(CardFitter.Fit(card, FitTarget.Flyout, resolver), resolver);
            left.Children.Add(Section(rows with { Actions = [.. rows.Actions, DeepPages.PinAction(card.Id, pinned)] }));
        }

        right.Children.Add(SystemHeading("Today"));
        var switches = BindingResolver.AsDouble(_activitySummary?["switches_today"]);
        right.Children.Add(SystemSurface(SystemRow("Switches", switches is { } count
            ? count.ToString("0", CultureInfo.InvariantCulture) : "—", null, null)));

        right.Children.Add(SystemHeading("Search my activity"));
        right.Children.Add(Text("Find what you saw or typed through the command search when capture is available.", "TasktorInk2Brush"));
        var search = new HyperlinkButton { Content = "Open command search", Foreground = ThemeBrush("TasktorAccentBrush") };
        AutomationProperties.SetName(search, "Open command search for activity");
        search.Click += (_, _) => FindRequested?.Invoke();
        right.Children.Add(search);

        var other = shown.Where(c => c.Id != "core.workflow").ToList();
        if (other.Count > 0)
        {
            right.Children.Add(SystemHeading("Projects and events"));
            foreach (var card in other)
            {
                var rows = renderer.Render(CardFitter.Fit(card, FitTarget.Flyout, resolver), resolver);
                right.Children.Add(Section(rows with { Actions = [.. rows.Actions, DeepPages.PinAction(card.Id, pinned)] }));
            }
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

    private async Task PullActivitySummaryAsync()
    {
        try
        {
            _activitySummary = await _snapshot.PullAsync("summary", new Dictionary<string, string> { ["since"] = "1d" });
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            _activitySummary = null; // Show unavailable; do not silently present a stale report.
        }
        finally
        {
            _activitySummaryAt = DateTimeOffset.Now;
            _pullingActivitySummary = false;
        }

        RequestRedraw("activity");
    }
}
