using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Tasktor.Contracts.Actions;
using Tasktor.Hosts.Common.Command;
using Tasktor.Hosts.Common.Pipe;
using Tasktor.Hosts.Common.Render;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;

namespace Tasktor.Flyout;

/// <summary>
/// U4 amended D-G12: the command line window. Borderless, topmost, out of Alt+Tab, 640 effective pixels wide near the
/// top of the primary monitor's work area. All behaviour is in <see cref="CommandController"/>; this class draws it and
/// forwards keys, clicks and focus loss.
/// </summary>
public sealed partial class CommandWindow : Window
{
    private const double WidthDip = 640;
    private readonly SnapshotClient _snapshot;
    private readonly CommandController _command;
    private bool _confirmationVisible;
    private bool _bringSelectionIntoView;
    private string? _renderedQuery;
    private int _renderVersion;

    public CommandWindow(SnapshotClient snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        InitializeComponent();
        _snapshot = snapshot;
        var ui = DispatcherQueue;
        _command = new CommandController(new ActionInvoker(ActAsync, Origin.Command), a =>
        {
            if (ui.HasThreadAccess)
            {
                a();
            }
            else
            {
                ui.TryEnqueue(() => a());
            }
        })
        {
            SearchActivity = SearchActivityAsync, // C21: the agent relays to the capture host's encrypted index
        };
        _command.Changed += Render;
        _command.Done += HideCommand;
        _command.OpenPage += page =>
        {
            HideCommand();
            DeepRequested?.Invoke(page);
        };
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
        Input.TextChanged += (_, _) => _command.Type(Input.Text);
        Input.KeyDown += OnKeyDown;
        Root.KeyDown += (_, e) =>
        {
            // Esc with the focus on the confirm buttons (the input handles its own keys first).
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                if (_command.Escape())
                {
                    HideCommand();
                }
            }
        };
        Refresh();
    }

    public bool IsShown { get; private set; }

    private readonly Tasktor.Hosts.Common.Command.DimLayer _dim = new();

    public event Action? Hidden;

    /// <summary>A page result was chosen; the app opens the deep window there.</summary>
    public event Action<string>? DeepRequested;

    public void ShowCommand()
    {
        Refresh();
        Input.Text = string.Empty;
        Layout();

        // G2 (D-G12): the dimmed desktop under the input, on the same monitor; none in high-contrast mode.
        var monitor = DisplayArea.Primary.OuterBounds;
        if (Tasktor.Hosts.Common.Command.DimSpec.For(monitor.X, monitor.Y, monitor.Width, monitor.Height, Tasktor.Hosts.Common.Command.DimLayer.HighContrast()) is { } dim)
        {
            _dim.Show(dim.X, dim.Y, dim.W, dim.H, dim.Alpha, WinRT.Interop.WindowNative.GetWindowHandle(this));
        }

        AppWindow.Show(true);
        Activate();
        IsShown = true;
        Input.Focus(FocusState.Programmatic);
    }

    public void HideCommand()
    {
        if (!IsShown)
        {
            return;
        }

        IsShown = false;
        _dim.Hide();
        AppWindow.Hide();
        _command.Closed();
        Hidden?.Invoke();
    }

    private Task<ActionResult> ActAsync(ActionRequest request, CancellationToken ct) =>
        _snapshot.Client is { } client ? client.ActAsync(request, ct) : Task.FromException<ActionResult>(new IOException("not connected"));

    /// <summary>"Search my activity": the agent's <c>activity</c> pull; null when capture is off or has no index.</summary>
    private async Task<IReadOnlyList<Contracts.Capture.ActivityHit>?> SearchActivityAsync(string query, CancellationToken ct)
    {
        var node = await _snapshot.PullAsync("activity", new Dictionary<string, string> { ["q"] = query }, ct: ct);
        return node is null ? null : System.Text.Json.JsonSerializer.Deserialize(node, Contracts.ContractsJsonContext.Default.IReadOnlyListActivityHit);
    }

    private void Refresh() => _command.Update(_snapshot.Mirror.Cards, new BindingResolver(_snapshot.Mirror.Root));

    private void Render()
    {
        Verdict.Text = _command.View.Verdict;
        var previousOffset = string.Equals(_renderedQuery, _command.Text, StringComparison.Ordinal) ? ResultsScroller.VerticalOffset : 0;
        _renderedQuery = _command.Text;
        var version = ++_renderVersion;
        Results.Children.Clear();
        var index = 0;
        foreach (var section in _command.View.Sections)
        {
            Results.Children.Add(new TextBlock
            {
                Text = section.Heading,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Margin = new Thickness(16, 14, 0, 2),
                Foreground = ThemeBrush("TasktorGlanceInk3Brush"),
            });
            foreach (var item in section.Items)
            {
                var selected = index == _command.Selected;
                var row = Row(item, selected, index);
                if (selected && _bringSelectionIntoView)
                {
                    row.Loaded += (_, _) => row.StartBringIntoView();
                }

                Results.Children.Add(row);
                index++;
            }
        }

        var confirming = _command.Confirming is not null;
        ConfirmRow.Visibility = confirming ? Visibility.Visible : Visibility.Collapsed;
        ResultsScroller.Visibility = confirming ? Visibility.Collapsed : Visibility.Visible;
        if (_command.Confirming is { } summary)
        {
            ConfirmText.Text = summary;
            if (!_confirmationVisible)
            {
                KeepButton.Focus(FocusState.Programmatic); // the safe choice is the default
            }
        }
        else if (_confirmationVisible && IsShown)
        {
            Input.Focus(FocusState.Programmatic);
        }

        _confirmationVisible = confirming;

        NoticeText.Text = _command.Notice ?? string.Empty;
        NoticeText.Visibility = _command.Notice is null ? Visibility.Collapsed : Visibility.Visible;
        if (!_bringSelectionIntoView && !confirming)
        {
            // Keep a user's scroll position during live requery; a new query starts at the top.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (version == _renderVersion) ResultsScroller.ChangeView(null, previousOffset, null, true);
            });
        }

        DispatcherQueue.TryEnqueue(Layout);
    }

    private Grid Row(CommandItem item, bool selected, int index)
    {
        var row = new Grid { MinHeight = 40, Padding = new Thickness(16, 0, 0, 0) };
        row.Children.Add(new Border
        {
            Width = 3,
            Height = 18,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(-16, 0, 0, 0),
            Background = selected ? ThemeBrush("TasktorAccentBrush") : null,
        });
        row.Children.Add(new TextBlock
        {
            Text = item.Label,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrush(selected ? "TasktorGlanceInkBrush" : "TasktorGlanceInk2Brush"),
        });
        AutomationProperties.SetName(row, item.Label);
        row.Tapped += async (_, _) =>
        {
            _command.Move(index - _command.Selected);
            await _command.EnterAsync();
        };
        return row;
    }

    private void Layout()
    {
        var area = DisplayArea.Primary;
        var work = area.WorkArea;
        var scale = Tasktor.Hosts.Common.Command.DisplayScale.At(work.X + (work.Width / 2), work.Y + (work.Height / 2));
        var widthDip = Math.Min(WidthDip, Math.Max(1, (work.Width / scale) - 32));
        var yOffset = Math.Max(0, work.Height / 6);
        var heightDip = Math.Min(560, Math.Max(1, (work.Height - yOffset) / scale));

        // Keep the input and confirmation visible and give the results the remaining height.
        ResultsScroller.MaxHeight = double.PositiveInfinity;
        Body.Measure(new Size(widthDip, double.PositiveInfinity));
        var fixedHeight = Math.Max(0, Body.DesiredSize.Height - ResultsScroller.DesiredSize.Height);
        ResultsScroller.MaxHeight = Math.Max(0, heightDip - fixedHeight);
        Body.Measure(new Size(widthDip, double.PositiveInfinity));

        var width = Math.Max(1, Math.Min(work.Width, (int)Math.Ceiling(widthDip * scale)));
        var height = Math.Max(1, Math.Min(work.Height - yOffset, (int)Math.Ceiling(Math.Min(Body.DesiredSize.Height, heightDip) * scale)));
        var x = work.X + ((work.Width - width) / 2);
        var y = work.Y + yOffset;
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            HideCommand();
        }
    }

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                e.Handled = true;
                MoveSelection(1);
                break;
            case VirtualKey.Up:
                e.Handled = true;
                MoveSelection(-1);
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                if (_command.Confirming is null)
                {
                    await _command.EnterAsync();
                }
                else
                {
                    // Only the explicit confirm button can answer yes; Enter in the input stays safe.
                    KeepButton.Focus(FocusState.Programmatic);
                }

                break;
            case VirtualKey.Escape:
                e.Handled = true;
                if (_command.Escape())
                {
                    HideCommand();
                }

                break;
            default:
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        _bringSelectionIntoView = true;
        try
        {
            _command.Move(delta);
        }
        finally
        {
            _bringSelectionIntoView = false;
        }
    }

    private void OnKeep(object sender, RoutedEventArgs e) => _command.Answer(false);

    private void OnConfirm(object sender, RoutedEventArgs e) => _command.Answer(true);

    private static Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;
}
