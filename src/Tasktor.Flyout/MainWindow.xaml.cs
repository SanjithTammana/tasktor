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
    private Contracts.Export.DataReport? _data;
    private DateTimeOffset _dataAt = DateTimeOffset.MinValue;
    private bool _pullingData;

    public MainWindow(SnapshotClient snapshot, string page)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        InitializeComponent();
        _snapshot = snapshot;
        _invoker = new ActionInvoker(ActAsync, Origin.Flyout);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));
        snapshot.Changed += _ => DispatcherQueue.TryEnqueue(() => Refresh(fromSnapshot: true));
        OpenPage(page);
    }

    /// <summary>Show <paramref name="pageId"/> (unknown ids land on Home) and bring the window forward.</summary>
    public void OpenPage(string? pageId)
    {
        _page = DeepPages.Normalize(pageId);
        Refresh(fromSnapshot: false);
        Activate();
    }

    private Task<ActionResult> ActAsync(ActionRequest request, CancellationToken ct) =>
        _snapshot.Client is { } client ? client.ActAsync(request, ct) : Task.FromException<ActionResult>(new IOException("not connected"));

    /// <summary>
    /// Pages drawn from their own pull (Settings, Your data) are not rebuilt on every snapshot change: a rebuild a second
    /// would take keyboard focus off a switch while the user is on it.
    /// </summary>
    private void Refresh(bool fromSnapshot)
    {
        var cards = _snapshot.Mirror.Cards;
        var visible = DeepPages.Visible(cards, DeepPages.HiddenPages(_snapshot.Mirror.Root));
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
        if (fromSnapshot && _page is "settings" or "data" or "learned")
        {
            return;
        }

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
            if (HomePage.Undo(_snapshot.Mirror.Root?["state"]?["recent_actions"], DateTimeOffset.Now) is { } undo)
            {
                PageContent.Children.Add(Section(undo));
            }
        }

        if (_page == "data")
        {
            DrawData();
        }

        if (_page == "settings")
        {
            DrawSettings();
            return;
        }

        if (_page == "learned")
        {
            DrawLearned();
            return;
        }

        if (_page == "focus")
        {
            DrawFocus();
            return;
        }

        if (_page == "windows")
        {
            DrawWindows();
            return;
        }

        var pinned = DeepPages.Pinned(_snapshot.Mirror.Root); // null = the defaults
        var shown = DeepPages.CardsFor(_page, cards, pinned);
        if (shown.Count == 0 && _page != "data")
        {
            PageContent.Children.Add(Text(_snapshot.Connected ? "Nothing to show on this page yet." : "Tasktor is not running.", "TasktorInk2Brush"));
            return;
        }

        var renderer = new RowRenderer(DateTimeOffset.Now);
        foreach (var card in shown)
        {
            var rows = renderer.Render(CardFitter.Fit(card, FitTarget.Flyout, resolver), resolver);
            // Pin / Unpin is the window's own control, added after fitting so it never displaces the card's actions.
            PageContent.Children.Add(Section(rows with { Actions = [.. rows.Actions, DeepPages.PinAction(card.Id, pinned)] }));
        }
    }

    /// <summary>"Your data": the agent's scan as sections with a two-step Forget each; re-pulled every 15 s while shown.</summary>
    private bool _exportRaw;
    private bool _exportCapture;
    private bool _exporting;

    private void DrawData()
    {
        var fresh = _data is null ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(15);
        if (DateTimeOffset.Now - _dataAt > fresh && !_pullingData)
        {
            _pullingData = true;
            _ = PullDataAsync();
        }

        if (_data is null)
        {
            PageContent.Children.Add(Text("Counting what Tasktor keeps…", "TasktorInk2Brush"));
            return;
        }

        foreach (var section in DataPage.Sections(_data, TimeZoneInfo.Local))
        {
            PageContent.Children.Add(Section(section));
        }

        DrawExport();
    }

    /// <summary>D-S7 Export: CSV + JSONL per table to a new folder; titles and capture records only when ticked (the agent asks).</summary>
    private void DrawExport()
    {
        PageContent.Children.Add(new TextBlock { Text = "Export", Style = StyleOf("TasktorSubtitleTextStyle"), Foreground = ThemeBrush("TasktorInkBrush") });
        PageContent.Children.Add(Text("A copy of what Tasktor keeps, as CSV and JSONL files with a manifest, in a new folder under Documents. The files are plain text.", "TasktorInk2Brush"));
        // The page is redrawn after every 15 s re-count: the ticks and the running export live in fields, not in the controls.
        var raw = new CheckBox { Content = "Include window titles (the last 30 days of raw events)", IsChecked = _exportRaw };
        raw.Click += (_, _) => _exportRaw = raw.IsChecked == true;
        var capture = new CheckBox { Content = "Include capture records, decrypted (the last 7 days)", IsChecked = _exportCapture };
        capture.Click += (_, _) => _exportCapture = capture.IsChecked == true;
        var go = new Button { Content = _exporting ? "Exporting…" : "Export my data", IsEnabled = !_exporting, Style = StyleOf("AccentButtonStyle"), Margin = new Thickness(0, 8, 0, 0) };
        go.Click += async (_, _) =>
        {
            _exporting = true;
            go.IsEnabled = false;
            try
            {
                if (await RunAsync(DataPage.Export(_exportRaw, _exportCapture)) is { Ok: true, Result.Message: { } done })
                {
                    await OnUi(() => InformAsync(done)); // where the files went
                }
            }
            finally
            {
                _exporting = false;
                if (_page == "data")
                {
                    Draw(_snapshot.Mirror.Cards);
                }
            }
        };
        PageContent.Children.Add(raw);
        PageContent.Children.Add(capture);
        PageContent.Children.Add(go);
    }

    private System.Text.Json.Nodes.JsonNode? _learned;
    private bool _pullingLearned;

    /// <summary>"What Tasktor learned": the agent's learned pull as sections; pulled again after any action here.</summary>
    private void DrawLearned()
    {
        if (_learned is null && !_pullingLearned)
        {
            _pullingLearned = true;
            _ = PullLearnedAsync();
        }

        if (_learned is null)
        {
            PageContent.Children.Add(Text(_snapshot.Connected ? "Reading what Tasktor learned…" : "Tasktor is not running.", "TasktorInk2Brush"));
            return;
        }

        foreach (var section in LearnedPage.Sections(_learned))
        {
            PageContent.Children.Add(Section(section));
        }

        DrawWorkflows();
    }

    private Contracts.Workflow.WorkflowsReport? _workflows;
    private bool _pullingWorkflows;

    /// <summary>The Learned page's Workflows tab (C7, C9, C10): learned workflows with Rename and Merge into.</summary>
    private void DrawWorkflows()
    {
        if (_workflows is null && !_pullingWorkflows)
        {
            _pullingWorkflows = true;
            _ = PullWorkflowsAsync();
        }

        PageContent.Children.Add(new TextBlock { Text = "Workflows", Style = StyleOf("TasktorSubtitleTextStyle"), Foreground = ThemeBrush("TasktorInkBrush"), Margin = new Thickness(0, 16, 0, 0) });
        var (summary, rows) = LearnedPage.Workflows(_workflows);
        PageContent.Children.Add(Text(_workflows is null ? "Reading learned workflows…" : summary, "TasktorInk2Brush"));
        var list = new StackPanel();
        foreach (var row in rows)
        {
            var grid = new Grid { MinHeight = 56, ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = row.Title, TextWrapping = TextWrapping.Wrap, Style = StyleOf("TasktorBodyTextStyle"), Foreground = ThemeBrush("TasktorInkBrush") });
            text.Children.Add(new TextBlock { Text = row.Note, TextWrapping = TextWrapping.Wrap, Style = StyleOf("TasktorCaptionTextStyle"), Foreground = ThemeBrush("TasktorInk3Brush") });
            grid.Children.Add(text);
            if (row.CanRename)
            {
                var rename = new Button { Content = "Rename…", VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(rename, "Rename " + row.Title);
                rename.Click += async (_, _) =>
                {
                    var box = new TextBox { Text = row.Title, MaxLength = 80 };
                    var dialog = new ContentDialog { Title = "Rename workflow", Content = box, PrimaryButtonText = "Rename", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot };
                    if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    {
                        await RunAsync(LearnedPage.Rename(row, box.Text));
                        _workflows = null;
                        Draw(_snapshot.Mirror.Cards);
                    }
                };
                Grid.SetColumn(rename, 1);
                grid.Children.Add(rename);
            }

            if (row.CanMerge)
            {
                var merge = new Button { Content = "Merge into…", VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(merge, "Merge " + row.Title + " into another workflow");
                merge.Click += async (_, _) =>
                {
                    var pick = new ComboBox { ItemsSource = row.MergeTargets.Select(t => t.Name).ToList(), SelectedIndex = 0, MinWidth = 240 };
                    var body = new StackPanel { Spacing = 8 };
                    body.Children.Add(new TextBlock { Text = "From the next refit on, both are learned as one workflow. Until then " + row.Title + " shows as the one you pick.", TextWrapping = TextWrapping.Wrap });
                    body.Children.Add(pick);
                    var dialog = new ContentDialog { Title = "Merge " + row.Title, Content = body, PrimaryButtonText = "Merge", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot };
                    if (await dialog.ShowAsync() == ContentDialogResult.Primary && pick.SelectedIndex >= 0)
                    {
                        await RunAsync(LearnedPage.Merge(row, row.MergeTargets[pick.SelectedIndex].Id));
                        _workflows = null;
                        Draw(_snapshot.Mirror.Cards);
                    }
                };
                Grid.SetColumn(merge, 2);
                grid.Children.Add(merge);
            }

            list.Children.Add(grid);
            list.Children.Add(new Border { Height = 1, Background = ThemeBrush("TasktorRuleBrush") });
        }

        PageContent.Children.Add(list);
    }

    private async Task PullWorkflowsAsync()
    {
        try
        {
            var node = await _snapshot.PullAsync("workflows");
            _workflows = node is null ? new Contracts.Workflow.WorkflowsReport() : System.Text.Json.JsonSerializer.Deserialize(node, Contracts.ContractsJsonContext.Default.WorkflowsReport);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or System.Text.Json.JsonException)
        {
            // stays on "Reading…" until the next visit
        }
        finally
        {
            _pullingWorkflows = false;
        }

        if (_page == "learned" && _workflows is not null)
        {
            Draw(_snapshot.Mirror.Cards);
        }
    }

    private async Task PullLearnedAsync()
    {
        try
        {
            _learned = await _snapshot.PullAsync("learned");
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // stays on "Reading…" until the next visit
        }
        finally
        {
            _pullingLearned = false;
        }

        if (_page == "learned")
        {
            Draw(_snapshot.Mirror.Cards);
        }
    }

    private System.Text.Json.Nodes.JsonNode? _focusPull;
    private DateTimeOffset _focusAt = DateTimeOffset.MinValue;
    private bool _pullingFocus;

    /// <summary>Focus: the running block from the snapshot (redrawn on every change), presets and recent blocks from the focus pull (every 30 s, and after any action here).</summary>
    private void DrawFocus()
    {
        if (DateTimeOffset.Now - _focusAt > TimeSpan.FromSeconds(30) && !_pullingFocus)
        {
            _pullingFocus = true;
            _ = PullFocusAsync();
        }

        var focus = _snapshot.Mirror.Root?["state"]?["focus"];
        foreach (var section in FocusPage.Sections(focus, _focusPull, DateTimeOffset.Now))
        {
            PageContent.Children.Add(Section(section));
        }

        if (_focusPull is null)
        {
            PageContent.Children.Add(Text(_snapshot.Connected ? "Reading your presets…" : "Tasktor is not running.", "TasktorInk2Brush"));
        }
    }

    private async Task PullFocusAsync()
    {
        try
        {
            _focusPull = await _snapshot.PullAsync("focus");
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // keeps what it had; tried again in 30 s
        }
        finally
        {
            _pullingFocus = false;
            _focusAt = DateTimeOffset.Now;
        }

        if (_page == "focus")
        {
            Draw(_snapshot.Mirror.Cards);
        }
    }

    private System.Text.Json.Nodes.JsonNode? _windowsPull;
    private DateTimeOffset _windowsAt = DateTimeOffset.MinValue;
    private bool _pullingWindows;

    /// <summary>Windows: the presets pull as sections with Arrange, Apply and Undo; pulled again every 15 s and after any action here (an undo appears once an arrange has run).</summary>
    private void DrawWindows()
    {
        if (DateTimeOffset.Now - _windowsAt > TimeSpan.FromSeconds(15) && !_pullingWindows)
        {
            _pullingWindows = true;
            _ = PullWindowsAsync();
        }

        if (_windowsPull is null)
        {
            PageContent.Children.Add(Text(_snapshot.Connected ? "Reading your presets…" : "Tasktor is not running.", "TasktorInk2Brush"));
            return;
        }

        foreach (var section in WindowsPage.Sections(_windowsPull))
        {
            PageContent.Children.Add(Section(section));
        }
    }

    private async Task PullWindowsAsync()
    {
        try
        {
            _windowsPull = await _snapshot.PullAsync("presets");
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // keeps what it had; tried again in 15 s
        }
        finally
        {
            _pullingWindows = false;
            _windowsAt = DateTimeOffset.Now;
        }

        if (_page == "windows")
        {
            Draw(_snapshot.Mirror.Cards);
        }
    }

    private Contracts.Config.ModulesReport? _modules;
    private bool _pullingModules;

    /// <summary>Settings → Modules: a switch per Module; on asks first (what it reads, where it is kept, what it contacts).</summary>
    private void DrawSettings()
    {
        if (_modules is null && !_pullingModules)
        {
            _pullingModules = true;
            _ = PullModulesAsync();
        }

        if (SettingsPage.Hotkey(_snapshot.Mirror.Root) is { } hotkey)
        {
            PageContent.Children.Add(new TextBlock { Text = "General", Style = StyleOf("TasktorSubtitleTextStyle"), Foreground = ThemeBrush("TasktorInkBrush") });
            var row = new Grid { MinHeight = 56, ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = "Command line shortcut", Style = StyleOf("TasktorBodyTextStyle"), Foreground = ThemeBrush("TasktorInkBrush") });
            text.Children.Add(new TextBlock { Text = hotkey.Note ?? "For example Win+Alt+T or Ctrl+Shift+Space", TextWrapping = TextWrapping.Wrap, Style = StyleOf("TasktorCaptionTextStyle"), Foreground = ThemeBrush(hotkey.Note is null ? "TasktorInk3Brush" : "TasktorInkBrush") });
            row.Children.Add(text);
            var box = new TextBox { Text = hotkey.Keys, MinWidth = 180, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(box, "Command line shortcut");
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            var change = new Button { Content = "Change", VerticalAlignment = VerticalAlignment.Center };
            change.Click += async (_, _) =>
            {
                await RunAsync(SettingsPage.SetHotkey(box.Text)); // the result (or why it was refused) shows as usual
                Draw(_snapshot.Mirror.Cards);
            };
            Grid.SetColumn(change, 2);
            row.Children.Add(change);
            PageContent.Children.Add(row);
            PageContent.Children.Add(new Border { Height = 1, Background = ThemeBrush("TasktorRuleBrush") });
        }

        PageContent.Children.Add(new TextBlock { Text = "Modules", Style = StyleOf("TasktorSubtitleTextStyle"), Foreground = ThemeBrush("TasktorInkBrush") });
        var list = new StackPanel();
        foreach (var row in SettingsPage.Modules(_modules))
        {
            var grid = new Grid { MinHeight = 56 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = row.Info.Name, Style = StyleOf("TasktorBodyTextStyle"), Foreground = ThemeBrush("TasktorInkBrush") });
            text.Children.Add(new TextBlock { Text = row.Note ?? row.Info.Reads, TextWrapping = TextWrapping.Wrap, Style = StyleOf("TasktorCaptionTextStyle"), Foreground = ThemeBrush("TasktorInk3Brush") });
            grid.Children.Add(text);
            var toggle = new ToggleSwitch { IsOn = row.On, IsEnabled = _modules is not null, OnContent = "On", OffContent = "Off", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(toggle, row.Info.Name);
            toggle.Toggled += async (_, _) => await ToggleModuleAsync(row, toggle);
            Grid.SetColumn(toggle, 1);
            grid.Children.Add(toggle);
            list.Children.Add(grid);
            list.Children.Add(new Border { Height = 1, Background = ThemeBrush("TasktorRuleBrush") });
        }

        PageContent.Children.Add(list);

        DrawRules();
        DrawPlugins();
        DrawAppearance();
    }

    /// <summary>Settings → Appearance (U4): hide pages from the rail; Home, Your data and Settings always stay.</summary>
    private void DrawAppearance()
    {
        PageContent.Children.Add(new TextBlock { Text = "Appearance", Style = StyleOf("TasktorSubtitleTextStyle"), Foreground = ThemeBrush("TasktorInkBrush"), Margin = new Thickness(0, 16, 0, 0) });
        PageContent.Children.Add(Text("Pages shown in the list on the left. Home, Your data and Settings are always there.", "TasktorInk2Brush"));
        var hidden = DeepPages.HiddenPages(_snapshot.Mirror.Root);
        var list = new StackPanel();
        foreach (var page in DeepPages.Hideable)
        {
            var grid = new Grid { MinHeight = 48 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock { Text = page.Name, VerticalAlignment = VerticalAlignment.Center, Style = StyleOf("TasktorBodyTextStyle"), Foreground = ThemeBrush("TasktorInkBrush") });
            var on = !hidden.Contains(page.Id);
            var toggle = new ToggleSwitch { IsOn = on, OnContent = "Shown", OffContent = "Hidden", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(toggle, page.Name);
            toggle.Toggled += async (_, _) =>
            {
                if (toggle.IsOn != on)
                {
                    await RunAsync(SettingsPage.ShowPage(page.Id, toggle.IsOn)); // the rail follows the next snapshot
                }
            };
            Grid.SetColumn(toggle, 1);
            grid.Children.Add(toggle);
            list.Children.Add(grid);
            list.Children.Add(new Border { Height = 1, Background = ThemeBrush("TasktorRuleBrush") });
        }

        PageContent.Children.Add(list);
    }

    private Contracts.Plugins.PluginsReport? _plugins;
    private bool _pullingPlugins;

    /// <summary>Settings → Plugins (U1 D-H7): each folder; turning one on shows its consent first (what it reads, contacts, runs).</summary>
    private void DrawPlugins()
    {
        if (_plugins is null && !_pullingPlugins)
        {
            _pullingPlugins = true;
            _ = PullPluginsAsync();
        }

        var rows = SettingsPage.Plugins(_plugins);
        PageContent.Children.Add(new TextBlock { Text = "Plugins", Style = StyleOf("TasktorSubtitleTextStyle"), Foreground = ThemeBrush("TasktorInkBrush"), Margin = new Thickness(0, 16, 0, 0) });
        if (_plugins is not null && rows.Count == 0)
        {
            PageContent.Children.Add(Text("No plugins. A plugin is a folder with a plugin.json in Tasktor's plugins folder.", "TasktorInk2Brush"));
        }

        var list = new StackPanel();
        foreach (var row in rows)
        {
            var grid = new Grid { MinHeight = 56 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = row.Title, Style = StyleOf("TasktorBodyTextStyle"), Foreground = ThemeBrush("TasktorInkBrush") });
            text.Children.Add(new TextBlock { Text = row.Note, TextWrapping = TextWrapping.Wrap, Style = StyleOf("TasktorCaptionTextStyle"), Foreground = ThemeBrush("TasktorInk3Brush") });
            grid.Children.Add(text);
            var toggle = new ToggleSwitch { IsOn = row.On, OnContent = "On", OffContent = "Off", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(toggle, row.Title);
            toggle.Toggled += async (_, _) =>
            {
                if (toggle.IsOn == row.On)
                {
                    return;
                }

                // D-H7: a plugin from a pack is turned on with its whole pack, through the pack's one dialog.
                var pack = toggle.IsOn && row.Info.Pack is { } packId ? SettingsPage.PackConsent(_plugins, packId) : null;
                if (toggle.IsOn)
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Turn on " + (pack?.Pack.Name ?? row.Title) + "?",
                        Content = new ScrollViewer { Content = new TextBlock { Text = pack?.Text ?? SettingsPage.PluginConsent(row.Info), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
                        PrimaryButtonText = "Turn on",
                        CloseButtonText = "Not now",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = Content.XamlRoot,
                    };
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    {
                        toggle.IsOn = false;
                        return;
                    }
                }

                await RunAsync(pack is { } p ? SettingsPage.EnablePack(p.Pack.Id) : SettingsPage.SwitchPlugin(row, toggle.IsOn));
                _plugins = null; // re-read the states
                Draw(_snapshot.Mirror.Cards);
            };
            Grid.SetColumn(toggle, 1);
            grid.Children.Add(toggle);
            list.Children.Add(grid);
            list.Children.Add(new Border { Height = 1, Background = ThemeBrush("TasktorRuleBrush") });
        }

        PageContent.Children.Add(list);
    }

    private async Task PullPluginsAsync()
    {
        try
        {
            var node = await _snapshot.PullAsync("plugins");
            _plugins = node is null ? null : System.Text.Json.JsonSerializer.Deserialize(node, Contracts.ContractsJsonContext.Default.PluginsReport);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or System.Text.Json.JsonException)
        {
            // the list stays empty until the agent answers
        }
        finally
        {
            _pullingPlugins = false;
        }

        if (_page == "settings" && _plugins is not null)
        {
            Draw(_snapshot.Mirror.Cards);
        }
    }

    private Contracts.Rules.RulesReport? _rules;
    private bool _pullingRules;

    /// <summary>Settings → Rules: each rule with its switch, where it comes from and when it last fired; load errors on top.</summary>
    private void DrawRules()
    {
        if (_rules is null && !_pullingRules)
        {
            _pullingRules = true;
            _ = PullRulesAsync();
        }

        PageContent.Children.Add(new TextBlock { Text = "Rules", Style = StyleOf("TasktorSubtitleTextStyle"), Foreground = ThemeBrush("TasktorInkBrush"), Margin = new Thickness(0, 16, 0, 0) });
        var editRules = new Button { Content = SettingsPage.EditRules.Label, Margin = new Thickness(0, 4, 0, 8) };
        editRules.Click += async (_, _) =>
        {
            await RunAsync(SettingsPage.EditRules); // a save reloads the rules; re-read the list then
            _rules = null;
        };
        PageContent.Children.Add(editRules);
        foreach (var error in _rules?.Errors ?? [])
        {
            PageContent.Children.Add(Text("Not loaded: " + error, "TasktorInkBrush"));
        }

        var list = new StackPanel();
        foreach (var row in SettingsPage.Rules(_rules, TimeZoneInfo.Local))
        {
            var grid = new Grid { MinHeight = 56 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = row.Title, TextWrapping = TextWrapping.Wrap, Style = StyleOf("TasktorBodyTextStyle"), Foreground = ThemeBrush("TasktorInkBrush") });
            text.Children.Add(new TextBlock { Text = row.Note, Style = StyleOf("TasktorCaptionTextStyle"), Foreground = ThemeBrush("TasktorInk3Brush") });
            grid.Children.Add(text);
            var toggle = new ToggleSwitch { IsOn = row.On, OnContent = "On", OffContent = "Off", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(toggle, row.Title);
            toggle.Toggled += async (_, _) =>
            {
                if (toggle.IsOn == row.On)
                {
                    return;
                }

                await RunAsync(SettingsPage.SwitchRule(row, toggle.IsOn)); // a failure (e.g. rules.json unreadable) is shown
                _rules = null; // re-read: the agent reloaded its rules
                Draw(_snapshot.Mirror.Cards);
            };
            Grid.SetColumn(toggle, 1);
            grid.Children.Add(toggle);
            list.Children.Add(grid);
            list.Children.Add(new Border { Height = 1, Background = ThemeBrush("TasktorRuleBrush") });
        }

        PageContent.Children.Add(list);
        DrawPackRules();
        DrawArrangeRequests();
    }

    /// <summary>U3 OQ-W2: rules that would arrange windows wait here for their one-time consent.</summary>
    private void DrawArrangeRequests()
    {
        var rows = SettingsPage.ArrangeRequests(_rules);
        if (rows.Count == 0)
        {
            return;
        }

        PageContent.Children.Add(new TextBlock { Text = "Waiting for your consent", Style = StyleOf("TasktorBodyStrongTextStyle"), Foreground = ThemeBrush("TasktorInkBrush"), Margin = new Thickness(0, 12, 0, 0) });
        foreach (var row in rows)
        {
            var grid = new Grid { MinHeight = 56 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = row.Title, TextWrapping = TextWrapping.Wrap, Style = StyleOf("TasktorBodyTextStyle"), Foreground = ThemeBrush("TasktorInkBrush") });
            text.Children.Add(new TextBlock { Text = "Wants to arrange windows", Style = StyleOf("TasktorCaptionTextStyle"), Foreground = ThemeBrush("TasktorInk3Brush") });
            grid.Children.Add(text);
            var allow = new Button { Content = "Allow…", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(allow, "Allow " + row.Title + " to arrange windows");
            allow.Click += async (_, _) =>
            {
                var dialog = new ContentDialog
                {
                    Title = "Let this rule arrange windows?",
                    Content = new TextBlock { Text = row.Consent, TextWrapping = TextWrapping.Wrap },
                    PrimaryButtonText = "Allow",
                    CloseButtonText = "Not now",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot,
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    await RunAsync(SettingsPage.AllowArrange(row));
                    _rules = null;
                    Draw(_snapshot.Mirror.Cards);
                }
            };
            Grid.SetColumn(allow, 1);
            grid.Children.Add(allow);
            PageContent.Children.Add(grid);
        }
    }

    /// <summary>Settings → Rules, from plugin packs: every rule of a pack that is on, each off until ticked.</summary>
    private void DrawPackRules()
    {
        var rows = SettingsPage.PackRules(_rules);
        if (rows.Count == 0)
        {
            return;
        }

        PageContent.Children.Add(new TextBlock { Text = "From plugin packs", Style = StyleOf("TasktorBodyStrongTextStyle"), Foreground = ThemeBrush("TasktorInkBrush"), Margin = new Thickness(0, 12, 0, 0) });
        var list = new StackPanel();
        foreach (var row in rows)
        {
            var grid = new Grid { MinHeight = 56 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = row.Title, TextWrapping = TextWrapping.Wrap, Style = StyleOf("TasktorBodyTextStyle"), Foreground = ThemeBrush("TasktorInkBrush") });
            text.Children.Add(new TextBlock { Text = row.Note, Style = StyleOf("TasktorCaptionTextStyle"), Foreground = ThemeBrush("TasktorInk3Brush") });
            grid.Children.Add(text);
            var toggle = new ToggleSwitch { IsOn = row.On, OnContent = "On", OffContent = "Off", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(toggle, row.Title);
            toggle.Toggled += async (_, _) =>
            {
                if (toggle.IsOn == row.On)
                {
                    return;
                }

                await RunAsync(SettingsPage.TickPackRule(row, toggle.IsOn));
                _rules = null;
                Draw(_snapshot.Mirror.Cards);
            };
            Grid.SetColumn(toggle, 1);
            grid.Children.Add(toggle);
            list.Children.Add(grid);
            list.Children.Add(new Border { Height = 1, Background = ThemeBrush("TasktorRuleBrush") });
        }

        PageContent.Children.Add(list);
    }

    private async Task PullRulesAsync()
    {
        try
        {
            var node = await _snapshot.PullAsync("rules");
            _rules = node is null ? null : System.Text.Json.JsonSerializer.Deserialize(node, Contracts.ContractsJsonContext.Default.RulesReport);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or System.Text.Json.JsonException)
        {
            // the list stays empty until the agent answers
        }
        finally
        {
            _pullingRules = false;
        }

        if (_page == "settings" && _rules is not null)
        {
            Draw(_snapshot.Mirror.Cards);
        }
    }

    private async Task ToggleModuleAsync(ModuleRow row, ToggleSwitch toggle)
    {
        if (toggle.IsOn == row.On)
        {
            return;
        }

        if (toggle.IsOn)
        {
            if (!row.CanTurnOnHere)
            {
                // Capture: its own consent screen (D-C22), never the generic dialog.
                if (await CaptureConsentAsync() is not { } consent)
                {
                    toggle.IsOn = false;
                    return;
                }

                await RunAsync(consent);
                _modules = null;
                Draw(_snapshot.Mirror.Cards);
                return;
            }

            var dialog = new ContentDialog
            {
                Title = "Turn on " + row.Info.Name + "?",
                Content = new TextBlock { Text = SettingsPage.Consent(row.Info), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Turn on",
                CloseButtonText = "Not now",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                toggle.IsOn = false;
                return;
            }
        }

        var action = new CardAction
        {
            Id = "module",
            Label = row.Info.Name,
            Action = toggle.IsOn ? "module.enable" : "module.disable",
            Target = new Dictionary<string, string> { ["module"] = row.Info.Id },
        };
        await RunAsync(action);
        _modules = null; // re-read: the row now says whether a restart is needed
        Draw(_snapshot.Mirror.Cards);
    }

    /// <summary>The D-C22 screen: the four facts and an acknowledgement that must be ticked before "Turn on capture".</summary>
    private async Task<CardAction?> CaptureConsentAsync()
    {
        var body = new StackPanel { Spacing = 12, MaxWidth = 520 };
        body.Children.Add(new TextBlock { Text = CaptureConsent.Lead, TextWrapping = TextWrapping.Wrap });
        foreach (var (heading, text) in CaptureConsent.Facts)
        {
            var fact = new StackPanel { Spacing = 2 };
            fact.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeights.SemiBold });
            fact.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("TasktorInk2Brush") });
            body.Children.Add(fact);
        }

        var ack = new CheckBox { Content = new TextBlock { Text = CaptureConsent.Acknowledgement, TextWrapping = TextWrapping.Wrap } };
        body.Children.Add(ack);
        var dialog = new ContentDialog
        {
            Title = CaptureConsent.Title,
            Content = body,
            PrimaryButtonText = CaptureConsent.Confirm,
            CloseButtonText = CaptureConsent.Cancel,
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            XamlRoot = Content.XamlRoot,
        };
        ack.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = CaptureConsent.CanConfirm(true);
        ack.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = CaptureConsent.CanConfirm(false);
        var yes = await dialog.ShowAsync() == ContentDialogResult.Primary;
        return yes && ack.IsChecked == true ? CaptureConsent.Request(acknowledged: true) : null;
    }

    private async Task PullModulesAsync()
    {
        try
        {
            var node = await _snapshot.PullAsync("modules");
            _modules = node is null ? null : System.Text.Json.JsonSerializer.Deserialize(node, Contracts.ContractsJsonContext.Default.ModulesReport);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or System.Text.Json.JsonException)
        {
            // the switches stay disabled until the agent answers
        }
        finally
        {
            _pullingModules = false;
        }

        if (_page == "settings" && _modules is not null)
        {
            Draw(_snapshot.Mirror.Cards);
        }
    }

    private async Task PullDataAsync()
    {
        try
        {
            var node = await _snapshot.PullAsync("data");
            _data = node is null ? _data : System.Text.Json.JsonSerializer.Deserialize(node, Contracts.ContractsJsonContext.Default.DataReport);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or System.Text.Json.JsonException)
        {
            // keep what we had; try again on the next refresh
        }
        finally
        {
            _dataAt = DateTimeOffset.Now;
            _pullingData = false;
        }

        if (_page == "data")
        {
            Draw(_snapshot.Mirror.Cards);
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
            var element = Row(row);
            if (row is { Kind: RowKind.TableRow, RowKey: { } key } && card.RowActions.Count > 0 && element is FrameworkElement fe)
            {
                // Right-click (or Shift+F10) on a row offers the card's row actions for that row.
                var menu = new MenuFlyout();
                foreach (var action in card.RowActions)
                {
                    var item = new MenuFlyoutItem { Text = action.Label };
                    item.Click += async (_, _) => await RunAsync(action, key);
                    menu.Items.Add(item);
                }

                fe.ContextFlyout = menu;
            }

            section.Children.Add(element);
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
            case RowKind.Tree when row.Series == "processes":
                return ProcessTree();
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

    private System.Text.Json.Nodes.JsonNode? _procs;
    private DateTimeOffset _procsAt = DateTimeOffset.MinValue;
    private bool _pullingProcs;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    /// <summary>
    /// Apps → processes (A2/A3). The full process table is pulled only while this is on screen, at most every 5 s
    /// (U1: "pulled only while the table is open"); which apps are open survives the page's redraws.
    /// </summary>
    private StackPanel ProcessTree()
    {
        if (DateTimeOffset.Now - _procsAt > TimeSpan.FromSeconds(5) && !_pullingProcs)
        {
            _pullingProcs = true;
            _ = PullProcessesAsync();
        }

        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var app in AppsTree.From(_procs))
        {
            var header = new Grid();
            foreach (var w in new[] { 3.0, 1, 1, 1 })
            {
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
            }

            string[] cells = [app.Name, app.Cpu, app.Memory, app.Count == 1 ? "1 process" : app.Count + " processes"];
            for (var i = 0; i < cells.Length; i++)
            {
                var t = new TextBlock { Text = cells[i], Style = StyleOf("TasktorBodyTextStyle"), Foreground = ThemeBrush(i == 0 ? "TasktorInkBrush" : "TasktorInk2Brush"), TextAlignment = i == 0 ? TextAlignment.Left : TextAlignment.Right };
                Grid.SetColumn(t, i);
                header.Children.Add(t);
            }

            var body = new StackPanel();
            foreach (var p in app.Processes)
            {
                var line = Line(p.Exe + " · " + p.Pid, p.Locked is { } why ? why : p.Cpu + " · " + p.Memory, p.Locked is null ? "TasktorInkBrush" : "TasktorInk3Brush");
                if (p.Actions.Count > 0)
                {
                    line.ContextFlyout = Menu(p.Actions);
                }

                body.Children.Add(line);
            }

            var id = app.AppId;
            var expander = new Expander
            {
                Header = header,
                Content = body,
                IsExpanded = _expanded.Contains(id),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            expander.Expanding += (_, _) => _expanded.Add(id);
            expander.Collapsed += (_, _) => _expanded.Remove(id);
            if (app.Actions.Count > 0)
            {
                expander.ContextFlyout = Menu(app.Actions);
            }

            AutomationProperties.SetName(expander, app.Name + ", " + app.Cpu + " CPU, " + app.Memory);
            panel.Children.Add(expander);
        }

        if (_procs is null)
        {
            panel.Children.Add(Text("Reading the process table…", "TasktorInk2Brush"));
        }

        return panel;
    }

    private MenuFlyout Menu(IReadOnlyList<CardAction> actions)
    {
        var menu = new MenuFlyout();
        foreach (var action in actions)
        {
            var item = new MenuFlyoutItem { Text = action.Label };
            item.Click += async (_, _) => await RunAsync(action);
            menu.Items.Add(item);
        }

        return menu;
    }

    private async Task PullProcessesAsync()
    {
        try
        {
            _procs = await _snapshot.PullAsync("processes");
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // keep the last table; the next draw tries again
        }
        finally
        {
            _procsAt = DateTimeOffset.Now;
            _pullingProcs = false;
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

    private async Task<ActionOutcome?> RunAsync(CardAction action, string? rowKey = null)
    {
        ActionOutcome outcome;
        try
        {
            outcome = await _invoker.InvokeAsync(action, rowKey, summary => OnUi(() => ConfirmAsync(summary)));
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            return null; // the agent went away; the page shows "Tasktor is not running." on the next refresh
        }

        _dataAt = DateTimeOffset.MinValue; // a forget (or anything) may change what is kept: re-count on the next draw
        if (_page == "learned")
        {
            _learned = null; // relearn / forget / learning on-off: read it again
            Draw(_snapshot.Mirror.Cards);
        }

        if (_page is "focus" or "windows")
        {
            _focusAt = DateTimeOffset.MinValue; // a block started, a preset arranged: read both pulls again
            _windowsAt = DateTimeOffset.MinValue;
            Draw(_snapshot.Mirror.Cards);
        }

        if (!outcome.Ok && !outcome.Declined && outcome.Result is { } r)
        {
            await OnUi(() => InformAsync(r.Message ?? "Couldn't do that (" + (r.Reason ?? "failed") + ")."));
        }

        return outcome;
    }

    private async Task<bool> ConfirmAsync(string summary)
    {
        // A summary with lines (a settings diff, a list) keeps its lines: monospace, scrollable, selectable.
        object content = summary.Contains('\n', StringComparison.Ordinal)
            ? new ScrollViewer
            {
                MaxHeight = 360,
                Content = new TextBlock { Text = summary, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"), IsTextSelectionEnabled = true, TextWrapping = TextWrapping.NoWrap },
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            }
            : summary;
        var dialog = new ContentDialog
        {
            Title = "Are you sure?",
            Content = content,
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
