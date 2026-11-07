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
        });
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

    public event Action? Hidden;

    /// <summary>A page result was chosen; the app opens the deep window there.</summary>
    public event Action<string>? DeepRequested;

    public void ShowCommand()
    {
        Refresh();
        Input.Text = string.Empty;
        Layout();
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
        AppWindow.Hide();
        _command.Closed();
        Hidden?.Invoke();
    }

    private Task<ActionResult> ActAsync(ActionRequest request, CancellationToken ct) =>
        _snapshot.Client is { } client ? client.ActAsync(request, ct) : Task.FromException<ActionResult>(new IOException("not connected"));

    private void Refresh() => _command.Update(_snapshot.Mirror.Cards, new BindingResolver(_snapshot.Mirror.Root));

    private void Render()
    {
        Verdict.Text = _command.View.Verdict;
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
                Foreground = ThemeBrush("TasktorInk3Brush"),
            });
            foreach (var item in section.Items)
            {
                Results.Children.Add(Row(item, index == _command.Selected, index));
                index++;
            }
        }

        var confirming = _command.Confirming is not null;
        ConfirmRow.Visibility = confirming ? Visibility.Visible : Visibility.Collapsed;
        Results.Visibility = confirming ? Visibility.Collapsed : Visibility.Visible;
        if (_command.Confirming is { } summary)
        {
            ConfirmText.Text = summary;
            KeepButton.Focus(FocusState.Programmatic); // the safe choice is the default
        }

        NoticeText.Text = _command.Notice ?? string.Empty;
        NoticeText.Visibility = _command.Notice is null ? Visibility.Collapsed : Visibility.Visible;
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
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrush(selected ? "TasktorInkBrush" : "TasktorInk2Brush"),
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
        var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        var work = area.WorkArea;
        Body.Measure(new Size(WidthDip, double.PositiveInfinity));
        var width = (int)Math.Ceiling(WidthDip * scale);
        var height = (int)Math.Ceiling(Math.Min(Body.DesiredSize.Height, 560) * scale);
        var x = work.X + ((work.Width - width) / 2);
        var y = work.Y + (work.Height / 6);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, Math.Max(height, 1)));
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
                _command.Move(1);
                break;
            case VirtualKey.Up:
                e.Handled = true;
                _command.Move(-1);
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                await _command.EnterAsync();
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

    private void OnKeep(object sender, RoutedEventArgs e) => _command.Answer(false);

    private void OnConfirm(object sender, RoutedEventArgs e) => _command.Answer(true);

    private static Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;
}
