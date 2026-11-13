using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Hosts.Common.Pipe;
using Tasktor.Hosts.Common.Render;
using Tasktor.Hosts.Common.Shelf;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;

namespace Tasktor.Flyout;

/// <summary>
/// U4 amended D-G11: the shelf window. Borderless, topmost, out of Alt+Tab, full width above the taskbar of the primary
/// monitor (<see cref="ShelfLayout"/>), as tall as its content. All behaviour is in <see cref="ShelfController"/>;
/// this class only draws it and forwards taps, Esc and focus loss.
/// </summary>
public sealed partial class ShelfWindow : Window
{
    private readonly SnapshotClient _snapshot;
    private readonly ShelfController _shelf;
    private readonly Dictionary<Hyperlink, string> _nouns = [];
    private double _widthDip = 1280;
    private bool _compactHeadline;
    private bool _confirmationVisible;

    public ShelfWindow(SnapshotClient snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        InitializeComponent();
        _snapshot = snapshot;
        var ui = DispatcherQueue;
        _shelf = new ShelfController(new ActionInvoker(ActAsync, Origin.Shelf), a =>
        {
            if (ui.HasThreadAccess)
            {
                a();
            }
            else
            {
                ui.TryEnqueue(() => a());
            }
        });
        _shelf.Changed += Render;
        snapshot.Changed += _ => ui.TryEnqueue(Refresh);

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        Activated += OnActivated;
        Root.KeyDown += OnKeyDown;
        Root.Loaded += (_, _) => Layout();
        Refresh();
    }

    public bool IsShown { get; private set; }

    /// <summary>The shelf hid; the app starts its exit countdown.</summary>
    public event Action? Hidden;

    /// <summary>"Everything Tasktor knows" was chosen.</summary>
    public event Action? DeepRequested;

    public void ShowShelf()
    {
        Refresh();
        Layout();
        AppWindow.Show(true);
        Activate();
        IsShown = true;
        _ = LoadGrainAsync();
        _ = LoadStripAsync();
    }

    public void HideShelf()
    {
        if (!IsShown)
        {
            return;
        }

        IsShown = false;
        AppWindow.Hide();
        _shelf.Closed();
        Hidden?.Invoke();
    }

    private Task<ActionResult> ActAsync(ActionRequest request, CancellationToken ct) =>
        _snapshot.Client is { } client ? client.ActAsync(request, ct) : Task.FromException<ActionResult>(new IOException("not connected"));

    private void Refresh() => _shelf.Update(_snapshot.Mirror.Cards, new BindingResolver(_snapshot.Mirror.Root));

    private void Render()
    {
        Sentence.Blocks.Clear();
        _nouns.Clear();
        var paragraph = new Paragraph();
        if (_shelf.Confirming is { } confirm)
        {
            paragraph.Inlines.Add(new Run { Text = confirm.Summary });
        }
        else
        {
            foreach (var segment in _shelf.Sentence.Segments)
            {
                paragraph.Inlines.Add(segment.Noun is { } id ? Noun(segment.Text, id) : new Run { Text = segment.Text });
            }

            if (_shelf.Sentence.More > 0)
            {
                paragraph.Inlines.Add(new Run { Text = " " + _shelf.Sentence.More + " more need a look." });
            }
        }

        Sentence.Blocks.Add(paragraph);
        RenderMark(_shelf.Sentence.Severity);
        RenderActions();

        ConfirmRow.Visibility = _shelf.Confirming is null ? Visibility.Collapsed : Visibility.Visible;
        ConfirmScroll.Visibility = ConfirmRow.Visibility;
        if (_shelf.Confirming is { } c)
        {
            ConfirmButton.Content = c.Action.Label;
            if (!_confirmationVisible)
            {
                KeepButton.Focus(FocusState.Programmatic); // the safe choice is the default
            }
        }

        _confirmationVisible = _shelf.Confirming is not null;

        NoticeText.Text = _shelf.Notice ?? string.Empty;
        NoticeText.Visibility = _shelf.Notice is null ? Visibility.Collapsed : Visibility.Visible;
        DispatcherQueue.TryEnqueue(Layout);
    }

    private Hyperlink Noun(string text, string id)
    {
        var link = new Hyperlink { UnderlineStyle = UnderlineStyle.Single, FontWeight = FontWeights.SemiBold, Foreground = ThemeBrush("TasktorGlanceInkBrush") };
        link.Inlines.Add(new Run { Text = text });
        link.Click += (sender, _) =>
        {
            if (_nouns.TryGetValue(sender, out var noun))
            {
                _shelf.Toggle(noun);
            }
        };
        _nouns[link] = id;
        return link;
    }

    private void RenderMark(Severity severity)
    {
        // Caution: a small triangle; critical: a small dot. Calm: nothing.
        SeverityMark.Visibility = severity == Severity.Normal ? Visibility.Collapsed : Visibility.Visible;
        if (severity == Severity.Normal)
        {
            return;
        }

        var critical = severity == Severity.Critical;
        SeverityMark.Fill = ThemeBrush(critical ? "TasktorCriticalBrush" : "TasktorCautionBrush");
        SeverityMark.Data = critical
            ? new EllipseGeometry { Center = new Point(5, 5), RadiusX = 5, RadiusY = 5 }
            : Triangle();
        AutomationProperties.SetName(SeverityMark, critical ? "Critical" : "Caution");
    }

    private static PathGeometry Triangle()
    {
        var figure = new PathFigure { StartPoint = new Point(5, 0), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment { Point = new Point(10, 10) });
        figure.Segments.Add(new LineSegment { Point = new Point(0, 10) });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private void RenderActions()
    {
        while (ActionsRow.Children.Count > 1)
        {
            ActionsRow.Children.RemoveAt(ActionsRow.Children.Count - 1);
        }

        if (_shelf.Unfolded is not { } noun || _shelf.Confirming is not null)
        {
            ActionsRow.Visibility = Visibility.Collapsed;
            ActionsScroll.Visibility = Visibility.Collapsed;
            return;
        }

        ActionsLabel.Text = noun.Text;
        foreach (var action in noun.Actions)
        {
            var button = new HyperlinkButton { Content = action.Label, Foreground = ThemeBrush("TasktorAccentBrush"), IsEnabled = !_shelf.Busy };
            button.Click += async (_, _) => await _shelf.RunAsync(action);
            ActionsRow.Children.Add(button);
        }

        ActionsRow.Visibility = Visibility.Visible;
        ActionsScroll.Visibility = Visibility.Visible;
    }

    private async Task LoadGrainAsync()
    {
        IReadOnlyList<double> load;
        IReadOnlyList<bool> hot;
        try
        {
            var pulled = await _snapshot.PullAsync("series", new Dictionary<string, string> { ["id"] = "system.cpu_pct" });
            load = EdgeGrain.LoadFromSeries(pulled);
            hot = EdgeGrain.HotFromSeries(pulled, load.Count);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            (load, hot) = ([], []);
        }

        var ticks = EdgeGrain.Compute(load, hot, (int)_widthDip);
        var plain = new GeometryGroup();
        var bright = new GeometryGroup();
        foreach (var t in ticks)
        {
            var line = new LineGeometry { StartPoint = new Point(t.X + 1, 6), EndPoint = new Point(t.X + 1, 6 - (t.Height * 6)) };
            (t.Hot ? bright : plain).Children.Add(line);
        }

        GrainPath.Data = plain;
        HotPath.Data = bright;
    }

    private async Task LoadStripAsync()
    {
        var pulled = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>();
        try
        {
            foreach (var (id, _) in TimelineStrip.Series)
            {
                pulled[id] = await _snapshot.PullAsync("series", new Dictionary<string, string> { ["id"] = id });
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // Draw what arrived; a missing series is simply not a row.
        }

        var alerts = _snapshot.Mirror.Root?["state"]?["alerts"] as System.Text.Json.Nodes.JsonArray;
        var recent = _snapshot.Mirror.Root?["state"]?["recent_actions"] as System.Text.Json.Nodes.JsonArray;
        DrawStrip(TimelineStrip.Compose(pulled, alerts, DateTimeOffset.Now, TimeZoneInfo.Local, recent));
        DispatcherQueue.TryEnqueue(Layout);
    }

    private void DrawStrip(TimelineModel m)
    {
        const double labelW = 64, top = 22, rowH = 12, maxT = rowH - 3;
        Strip.Children.Clear();
        if (m.Rows.Count == 0)
        {
            Strip.Height = 0;
            return;
        }

        var width = Math.Max(labelW + 1, _widthDip - 96);
        var pastW = (width - labelW) * (TimelineStrip.Past / (TimelineStrip.Past + TimelineStrip.Future));
        var nowX = labelW + pastW;
        var bottom = top + (m.Rows.Count * rowH);
        double X(double at) => labelW + (at * pastW);

        foreach (var e in m.Events)
        {
            Strip.Children.Add(new Line { X1 = X(e.At), Y1 = top - 4, X2 = X(e.At), Y2 = bottom, Stroke = ThemeBrush("TasktorRuleBrush"), StrokeThickness = 1 });
            Strip.Children.Add(Label(e.Label, X(e.At) + 4, 0, "TasktorGlanceInk2Brush"));
        }

        for (var r = 0; r < m.Rows.Count; r++)
        {
            var row = m.Rows[r];
            var c = top + (r * rowH) + (rowH / 2);
            var band = new Polygon { Fill = ThemeBrush(row.Lead ? "TasktorGlanceInkBrush" : "TasktorGlanceInk3Brush") };
            for (var i = 0; i < row.Values.Count; i++)
            {
                band.Points.Add(new Point(X(TimelineStrip.At(i, row.Values.Count)), c - (row.Values[i] * maxT / 2)));
            }

            for (var i = row.Values.Count - 1; i >= 0; i--)
            {
                band.Points.Add(new Point(X(TimelineStrip.At(i, row.Values.Count)), c + (row.Values[i] * maxT / 2) + 0.5));
            }

            Strip.Children.Add(band);
            var name = Label(row.Label, 0, c - 8, row.Lead ? "TasktorGlanceInkBrush" : "TasktorGlanceInk3Brush");
            name.FontWeight = row.Lead ? FontWeights.SemiBold : FontWeights.Normal;
            Strip.Children.Add(name);

            if (row.Lead && m.Forecast is { } f)
            {
                var step = (width - nowX) / f.Values.Count;
                var upper = new Polyline { Stroke = ThemeBrush("TasktorGlanceInkBrush"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 4 } };
                var lower = new Polyline { Stroke = ThemeBrush("TasktorGlanceInkBrush"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 4 } };
                upper.Points.Add(new Point(nowX, c - (row.Values[^1] * maxT / 2)));
                lower.Points.Add(new Point(nowX, c + (row.Values[^1] * maxT / 2)));
                for (var k = 0; k < f.Values.Count; k++)
                {
                    upper.Points.Add(new Point(nowX + ((k + 1) * step), c - (f.Values[k] * maxT / 2)));
                    lower.Points.Add(new Point(nowX + ((k + 1) * step), c + (f.Values[k] * maxT / 2)));
                }

                Strip.Children.Add(upper);
                Strip.Children.Add(lower);
                if (f.Label is { } text)
                {
                    Strip.Children.Add(Label(text, nowX + 8, c - 22, "TasktorGlanceInkBrush"));
                }
            }
        }

        Strip.Children.Add(new Line { X1 = nowX, Y1 = top - 4, X2 = nowX, Y2 = bottom, Stroke = ThemeBrush("TasktorGlanceInk3Brush"), StrokeThickness = 1 });
        Strip.Children.Add(Label(m.Start, labelW, bottom + 4, "TasktorGlanceInk3Brush"));
        var now = Label("now", nowX - 20, bottom + 4, "TasktorGlanceInkBrush");
        now.Width = 40;
        now.TextAlignment = TextAlignment.Center;
        Strip.Children.Add(now);
        var end = Label(m.End, width - 60, bottom + 4, "TasktorGlanceInk3Brush");
        end.Width = 60;
        end.TextAlignment = TextAlignment.Right;
        Strip.Children.Add(end);
        Strip.Width = width;
        Strip.Height = bottom + 22;
    }

    private static TextBlock Label(string text, double x, double y, string brush)
    {
        var t = new TextBlock { Text = text, FontSize = 12, Foreground = ThemeBrush(brush) };
        Canvas.SetLeft(t, x);
        Canvas.SetTop(t, y);
        return t;
    }

    private void OnHeadlineSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 560;
        if (compact == _compactHeadline)
        {
            return;
        }

        _compactHeadline = compact;
        Grid.SetRow(DeepLink, compact ? 1 : 0);
        Grid.SetColumn(DeepLink, compact ? 1 : 2);
        DeepLink.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        DeepLink.Margin = compact ? new Thickness(0, 4, 0, 0) : new Thickness(0, 6, 0, 0);
        DispatcherQueue.TryEnqueue(Layout);
    }

    private void Layout()
    {
        var area = DisplayArea.Primary;
        var monitor = area.OuterBounds;
        var work = area.WorkArea;
        var scale = Tasktor.Hosts.Common.Command.DisplayScale.At(work.X + (work.Width / 2), work.Y + (work.Height / 2));
        _widthDip = monitor.Width / scale;
        Body.Measure(new Size(_widthDip, double.PositiveInfinity));
        var placement = ShelfLayout.Place(
            new PxRect(monitor.X, monitor.Y, monitor.Width, monitor.Height),
            new PxRect(work.X, work.Y, work.Width, work.Height),
            scale,
            Body.DesiredSize.Height + 6);
        var shelf = placement.Shelf;
        AppWindow.MoveAndResize(new RectInt32(shelf.X, shelf.Y, shelf.Width, shelf.Height));
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            HideShelf();
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        e.Handled = true;
        if (_shelf.Escape())
        {
            HideShelf();
        }
    }

    private void OnKeep(object sender, RoutedEventArgs e) => _shelf.Answer(false);

    private void OnConfirm(object sender, RoutedEventArgs e) => _shelf.Answer(true);

    private void OnDeepLink(object sender, RoutedEventArgs e)
    {
        HideShelf();
        DeepRequested?.Invoke();
    }

    private static Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;
}
