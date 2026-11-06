using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Hosts.Common.Deep;
using Tasktor.Hosts.Common.Pipe;
using Tasktor.Hosts.Common.Render;

namespace Tasktor.Flyout;

/// <summary>
/// U4 amended D-G13: the deep window. A normal resizable window on Mica with the nine pages (<see cref="DeepPages"/>;
/// a Module's page only while it is on) and each page's cards drawn as rows by <see cref="RowRenderer"/>. Card actions
/// go through <c>act</c> with origin <c>flyout</c>; a destructive one asks its two-step confirm in a dialog whose
/// default button keeps things as they are (D-H4).
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly SnapshotClient _snapshot;
    private readonly ActionInvoker _invoker;
    private string _page = DeepPages.Home;
    private string _pagesShown = string.Empty;
    private readonly Dictionary<string, (System.Text.Json.Nodes.JsonNode? Node, DateTimeOffset At)> _series = [];
    private readonly HashSet<string> _pulling = [];
    private static readonly TimeSpan SeriesFresh = TimeSpan.FromSeconds(30);

    public MainWindow(SnapshotClient snapshot, string page)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        InitializeComponent();
        _snapshot = snapshot;
        _invoker = new ActionInvoker(ActAsync, Origin.Flyout);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));
        snapshot.Changed += _ => DispatcherQueue.TryEnqueue(Refresh);
        OpenPage(page);
    }

    /// <summary>Show <paramref name="pageId"/> (unknown ids land on Home) and bring the window forward.</summary>
    public void OpenPage(string? pageId)
    {
        _page = DeepPages.Normalize(pageId);
        Refresh();
        Activate();
    }

    private Task<ActionResult> ActAsync(ActionRequest request, CancellationToken ct) =>
        _snapshot.Client is { } client ? client.ActAsync(request, ct) : Task.FromException<ActionResult>(new IOException("not connected"));

    private void Refresh()
    {
        var cards = _snapshot.Mirror.Cards;
        var visible = DeepPages.Visible(cards);
        if (!visible.Any(p => p.Id == _page))
        {
            _page = DeepPages.Home; // its Module was turned off
        }

        var key = string.Join(",", visible.Select(p => p.Id));
        if (key != _pagesShown)
        {
            _pagesShown = key;
            Nav.MenuItems.Clear();
            foreach (var p in visible)
            {
                Nav.MenuItems.Add(new NavigationViewItem { Content = p.Name, Tag = p.Id });
            }
        }

        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == _page);
        Draw(cards);
    }

    private void OnNavigate(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string id } && id != _page)
        {
            _page = id;
            Draw(_snapshot.Mirror.Cards);
        }
    }

    private void Draw(IReadOnlyList<Card> cards)
    {
        var page = DeepPages.All.First(p => p.Id == _page);
        PageTitle.Text = page.Name;
        PageContent.Children.Clear();
        var resolver = new BindingResolver(_snapshot.Mirror.Root);
        if (_page == DeepPages.Home)
        {
            DrawAlerts();
        }

        var shown = DeepPages.CardsFor(_page, cards);
        if (shown.Count == 0)
        {
            PageContent.Children.Add(Text(_snapshot.Connected ? "Nothing to show on this page yet." : "Tasktor is not running.", "TasktorInk2Brush"));
            return;
        }

        var renderer = new RowRenderer(DateTimeOffset.Now);
        foreach (var card in shown)
        {
            PageContent.Children.Add(Section(renderer.Render(CardFitter.Fit(card, FitTarget.Flyout, resolver), resolver)));
        }
    }

    private void DrawAlerts()
    {
        if (_snapshot.Mirror.Root?["state"]?["alerts"] is not System.Text.Json.Nodes.JsonArray { Count: > 0 } alerts)
        {
            return;
        }

        var list = new StackPanel { Spacing = 0 };
        foreach (var a in alerts.OfType<System.Text.Json.Nodes.JsonObject>().Reverse())
        {
            var severity = a["severity"]?.ToString() == "critical" ? "Critical" : "Caution";
            var text = a["text"]?.ToString() ?? a["rule"]?.ToString() ?? string.Empty;
            list.Children.Add(Line(severity, text, "TasktorInkBrush"));
        }

        PageContent.Children.Add(list);
    }

    private StackPanel Section(CardRows card)
    {
        var section = new StackPanel { Spacing = 0 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(new TextBlock { Text = card.Title, Style = StyleOf("TasktorSubtitleTextStyle"), Foreground = ThemeBrush("TasktorInkBrush") });
        if (card.Severity != Severity.Normal)
        {
            header.Children.Add(Caption(card.Severity == Severity.Critical ? "Critical" : "Caution", "TasktorInkBrush"));
        }

        foreach (var note in new[] { card.Source is null ? null : "from " + card.Source, card.AsOf })
        {
            if (note is not null)
            {
                header.Children.Add(Caption(note, "TasktorInk3Brush"));
            }
        }

        section.Children.Add(header);
        foreach (var row in card.Rows)
        {
            section.Children.Add(Row(row));
        }

        var actions = card.Actions.Where(ShelfComposer.ShelfRunnable).ToList();
        if (actions.Count > 0)
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var action in actions)
            {
                var button = new HyperlinkButton { Content = action.Label, Foreground = ThemeBrush("TasktorAccentBrush") };
                button.Click += async (_, _) => await RunAsync(action);
                bar.Children.Add(button);
            }

            section.Children.Add(bar);
        }

        return section;
    }

    private UIElement Row(CardRow row)
    {
        var mark = row.Severity switch { Severity.Critical => "Critical · ", Severity.Warn => "Caution · ", _ => string.Empty };
        switch (row.Kind)
        {
            case RowKind.Heading:
                return new TextBlock { Text = row.Label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4), Foreground = ThemeBrush("TasktorInk2Brush") };
            case RowKind.TableHeader or RowKind.TableRow:
                return Cells(row.Cells ?? [], header: row.Kind == RowKind.TableHeader);
            case RowKind.Text or RowKind.Alert:
                return Line(row.Kind == RowKind.Alert ? mark.TrimEnd(' ', '·') : null, row.Label, "TasktorInkBrush");
            case RowKind.Chart:
                return Chart(row);
            case RowKind.Meter:
                var meter = Line(row.Label, mark + row.Value, "TasktorInkBrush");
                if (row.Fraction is { } f)
                {
                    meter.Children.Insert(meter.Children.Count - 1, new ProgressBar { Value = f * 100, Maximum = 100, Margin = new Thickness(0, 0, 0, 10) });
                }

                return meter;
            default:
                return Line(row.Label, mark + (row.Value ?? string.Empty), "TasktorInkBrush");
        }
    }

    /// <summary>A sparkline row: label, the line, and the latest value; a series is pulled from the agent and cached.</summary>
    private StackPanel Chart(CardRow row)
    {
        System.Text.Json.Nodes.JsonNode? pulled = null;
        if (row.Points is not { Count: > 0 } && row.Series is { } id)
        {
            if (_series.TryGetValue(id, out var cached))
            {
                pulled = cached.Node;
            }

            if ((!_series.ContainsKey(id) || DateTimeOffset.Now - cached.At > SeriesFresh) && _pulling.Add(id))
            {
                _ = PullSeriesAsync(id);
            }
        }

        var spark = Sparks.From(row.Points, pulled, row.Value);
        var line = Line(row.Label, spark?.Last ?? BindingResolver.Missing, "TasktorInkBrush");
        if (spark is { Values.Count: > 1 } s)
        {
            const double w = 240, h = 28;
            var poly = new Polyline { Stroke = ThemeBrush("TasktorInkBrush"), StrokeThickness = 1, Width = w, Height = h, Margin = new Thickness(0, 6, 0, 6) };
            for (var i = 0; i < s.Values.Count; i++)
            {
                poly.Points.Add(new Windows.Foundation.Point(i * w / (s.Values.Count - 1), h - 1 - (s.Values[i] * (h - 2))));
            }

            AutomationProperties.SetName(poly, row.Label + ", " + s.Peak);
            line.Children.Insert(line.Children.Count - 1, poly);
        }

        return line;
    }

    private async Task PullSeriesAsync(string id)
    {
        try
        {
            var node = await _snapshot.PullAsync("series", new Dictionary<string, string> { ["id"] = id });
            _series[id] = (node, DateTimeOffset.Now);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            _series[id] = (null, DateTimeOffset.Now); // try again after SeriesFresh
        }
        finally
        {
            _pulling.Remove(id);
        }

        Draw(_snapshot.Mirror.Cards);
    }

    /// <summary>A label-left, value-right row with a hairline under it.</summary>
    private static StackPanel Line(string? label, string value, string valueBrush)
    {
        var grid = new Grid { MinHeight = 40 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (label is not null)
        {
            grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Style = StyleOf("TasktorBodyTextStyle"), Foreground = ThemeBrush("TasktorInk2Brush") });
        }

        var right = new TextBlock { Text = value, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Style = StyleOf("TasktorBodyTextStyle"), Foreground = ThemeBrush(valueBrush) };
        Grid.SetColumn(right, label is null ? 0 : 1);
        grid.Children.Add(right);
        AutomationProperties.SetName(grid, label is null ? value : label + ": " + value);
        var line = new StackPanel();
        line.Children.Add(grid);
        line.Children.Add(new Border { Height = 1, Background = ThemeBrush("TasktorRuleBrush") });
        return line;
    }

    private static StackPanel Cells(IReadOnlyList<string> cells, bool header)
    {
        var grid = new Grid { MinHeight = header ? 32 : 36 };
        for (var i = 0; i < cells.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(i == 0 ? 2 : 1, GridUnitType.Star) });
            var t = new TextBlock
            {
                Text = cells[i],
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
                Style = StyleOf(header ? "TasktorCaptionTextStyle" : "TasktorBodyTextStyle"),
                Foreground = ThemeBrush(header ? "TasktorInk3Brush" : "TasktorInkBrush"),
                TextAlignment = i == 0 ? TextAlignment.Left : TextAlignment.Right,
            };
            Grid.SetColumn(t, i);
            grid.Children.Add(t);
        }

        var line = new StackPanel();
        line.Children.Add(grid);
        line.Children.Add(new Border { Height = 1, Background = ThemeBrush("TasktorRuleBrush") });
        return line;
    }

    private async Task RunAsync(CardAction action)
    {
        ActionOutcome outcome;
        try
        {
            outcome = await _invoker.InvokeAsync(action, null, summary => OnUi(() => ConfirmAsync(summary)));
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            return; // the agent went away; the page shows "Tasktor is not running." on the next refresh
        }

        if (!outcome.Ok && !outcome.Declined && outcome.Result is { } r)
        {
            await OnUi(() => InformAsync(r.Message ?? "Couldn't do that (" + (r.Reason ?? "failed") + ")."));
        }
    }

    private async Task<bool> ConfirmAsync(string summary)
    {
        var dialog = new ContentDialog
        {
            Title = "Are you sure?",
            Content = summary,
            PrimaryButtonText = "Yes, do it",
            CloseButtonText = "Keep it",
            DefaultButton = ContentDialogButton.Close, // the safe choice is the default
            XamlRoot = Content.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<bool> InformAsync(string message)
    {
        var dialog = new ContentDialog { Title = "Tasktor", Content = message, CloseButtonText = "OK", XamlRoot = Content.XamlRoot };
        await dialog.ShowAsync();
        return true;
    }

    /// <summary>Run <paramref name="work"/> on the UI thread (the invoker's confirm can arrive from a pipe thread).</summary>
    private Task<T> OnUi<T>(Func<Task<T>> work)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            return work();
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                tcs.TrySetResult(await work());
            }
            catch (Exception e)
            {
                tcs.TrySetException(e);
            }
        });
        return tcs.Task;
    }

    private static TextBlock Text(string text, string brush) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Style = StyleOf("TasktorBodyTextStyle"), Foreground = ThemeBrush(brush) };

    private static TextBlock Caption(string text, string brush) =>
        new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Style = StyleOf("TasktorCaptionTextStyle"), Foreground = ThemeBrush(brush) };

    private static Style? StyleOf(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;

    private static Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;
}
