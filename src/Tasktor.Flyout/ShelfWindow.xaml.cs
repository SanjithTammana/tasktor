using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
        if (_shelf.Confirming is { } c)
        {
            ConfirmButton.Content = c.Action.Label;
            KeepButton.Focus(FocusState.Programmatic); // the safe choice is the default
        }

        NoticeText.Text = _shelf.Notice ?? string.Empty;
        NoticeText.Visibility = _shelf.Notice is null ? Visibility.Collapsed : Visibility.Visible;
        DispatcherQueue.TryEnqueue(Layout);
    }

    private Hyperlink Noun(string text, string id)
    {
        var link = new Hyperlink { UnderlineStyle = UnderlineStyle.Single, FontWeight = FontWeights.SemiBold, Foreground = ThemeBrush("TasktorInkBrush") };
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
        SeverityMark.Fill = Brush(critical ? "TasktorCriticalBrush" : "TasktorCautionBrush");
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
    }

    private async Task LoadGrainAsync()
    {
        IReadOnlyList<double> load;
        try
        {
            load = EdgeGrain.LoadFromSeries(await _snapshot.PullAsync("series", new Dictionary<string, string> { ["id"] = "system.cpu_pct" }));
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            load = [];
        }

        // Severity history per minute is not served yet; the stretch is drawn plain until it is (PROGRESS: Next).
        var ticks = EdgeGrain.Compute(load, [.. load.Select(_ => false)], (int)_widthDip);
        var plain = new GeometryGroup();
        var hot = new GeometryGroup();
        foreach (var t in ticks)
        {
            var line = new LineGeometry { StartPoint = new Point(t.X + 1, 6), EndPoint = new Point(t.X + 1, 6 - (t.Height * 6)) };
            (t.Hot ? hot : plain).Children.Add(line);
        }

        GrainPath.Data = plain;
        HotPath.Data = hot;
    }

    private void Layout()
    {
        var area = DisplayArea.Primary;
        var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        var monitor = area.OuterBounds;
        var work = area.WorkArea;
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
