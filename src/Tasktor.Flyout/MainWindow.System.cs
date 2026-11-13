using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Tasktor.Contracts.Cards;
using Tasktor.Hosts.Common.Deep;
using Tasktor.Hosts.Common.Render;

namespace Tasktor.Flyout;

public sealed partial class MainWindow
{
    private string _systemTab = "overview";
    private readonly HashSet<string> _resourceExpanded = new(StringComparer.Ordinal);
    private bool _systemMounted;

    // The approved deep-window board puts resources beside apps. Cards remain the data and action contract.
    private void DrawSystem(IReadOnlyList<Card> cards, BindingResolver resolver, StackPanel target)
    {
        var shown = DeepPages.CardsFor("system", cards);
        var tabs = new List<(string Id, string Name)> { ("overview", "Overview"), ("apps", "Apps") };
        if (shown.Any(c => c.Id == "core.disk"))
        {
            tabs.Add(("disk", "Disk"));
        }

        if (shown.Any(c => c.Id == "module.ports"))
        {
            tabs.Add(("ports", "Ports"));
        }

        if (!tabs.Any(t => t.Id == _systemTab))
        {
            _systemTab = "overview";
        }

        var tabBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
        foreach (var tab in tabs)
        {
            var selected = tab.Id == _systemTab;
            var label = new TextBlock
            {
                Text = tab.Name,
                Style = StyleOf(selected ? "TasktorBodyStrongTextStyle" : "TasktorBodyTextStyle"),
                Foreground = ThemeBrush(selected ? "TasktorInkBrush" : "TasktorInk2Brush"),
            };
            var indicator = new Border
            {
                Width = 16,
                Height = 3,
                CornerRadius = new CornerRadius(2),
                Background = selected ? ThemeBrush("TasktorAccentBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
            };
            var tabContent = new StackPanel();
            tabContent.Children.Add(label);
            tabContent.Children.Add(indicator);
            var button = new Button
            {
                Tag = "tab:" + tab.Id,
                Content = tabContent,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 6, 0, 0),
            };
            AutomationProperties.SetName(button, tab.Name + (selected ? ", selected" : string.Empty));
            button.Click += (_, _) =>
            {
                _systemTab = tab.Id;
                Draw(_snapshot.Mirror.Cards);
            };
            tabBar.Children.Add(button);
        }

        target.Children.Add(tabBar);
        if (!_snapshot.Connected)
        {
            target.Children.Add(Text("Tasktor is not running.", "TasktorInk2Brush"));
            return;
        }

        if (_systemTab == "apps")
        {
            target.Children.Add(SystemApps(shown.Any(c => c.Id == "core.apps")));
            return;
        }

        var renderer = new RowRenderer(DateTimeOffset.Now);
        if (_systemTab is "disk" or "ports")
        {
            var id = _systemTab == "disk" ? "core.disk" : "module.ports";
            foreach (var card in shown.Where(c => c.Id == id))
            {
                target.Children.Add(Section(renderer.Render(CardFitter.Fit(card, FitTarget.Flyout, resolver), resolver)));
            }

            return;
        }

        var resources = new StackPanel { Spacing = 8 };
        resources.Children.Add(SystemHeading("Resources"));
        if (shown.FirstOrDefault(c => c.Id == "core.system") is { } system)
        {
            var rows = renderer.Render(CardFitter.Fit(system, FitTarget.Flyout, resolver), resolver);
            var group = new StackPanel { Spacing = 2 };
            foreach (var row in rows.Rows.Where(r => r.Kind is RowKind.Value or RowKind.Meter))
            {
                var label = row.Label switch { "RAM" => "Memory", "Down" => "Network", _ => row.Label };
                var description = row.Label == "Down" ? "Download rate" : null;
                var header = SystemRow(label, row.Value ?? BindingResolver.Missing, description, ResourceGlyph(row.Label));
                header.Tag = "resource:" + row.Label;
                var history = rows.Rows.FirstOrDefault(r => r.Kind == RowKind.Chart && r.Label == row.Label);
                if (history is null)
                {
                    group.Children.Add(SystemSurface(header));
                    continue;
                }

                // Match the 64-DIP resource rows while keeping history on demand.
                header.MinHeight = 64;
                var resourceId = row.Label;
                var expanded = _resourceExpanded.Contains(resourceId);
                var expander = new Expander
                {
                    Header = header,
                    IsExpanded = expanded,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = ThemeBrush("TasktorSurfaceBrush"),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0),
                    Content = expanded ? Chart(history) : null,
                };
                expander.Expanding += (_, _) =>
                {
                    _resourceExpanded.Add(resourceId);
                    var current = CurrentSystemHistory(resourceId);
                    if (current is not null)
                    {
                        expander.Content ??= Chart(current);
                    }
                };
                expander.Collapsed += (_, _) => _resourceExpanded.Remove(resourceId);
                AutomationProperties.SetName(expander, label + ", " + row.Value + ", show history");
                group.Children.Add(expander);
            }

            resources.Children.Add(group);
            if (rows.Severity != Severity.Normal)
            {
                resources.Children.Add(Caption(rows.Severity == Severity.Critical ? "Critical" : "Caution", "TasktorInkBrush"));
            }

            if (rows.AsOf is { } asOf)
            {
                resources.Children.Add(Caption(asOf, "TasktorInk3Brush"));
            }

            var pin = DeepPages.PinAction(system.Id, DeepPages.Pinned(_snapshot.Mirror.Root));
            var pinButton = new HyperlinkButton { Content = pin.Label };
            pinButton.Tag = ActionKey(pin, null);
            pinButton.Click += async (_, _) => await RunAsync(pin);
            resources.Children.Add(pinButton);
        }
        else
        {
            resources.Children.Add(Text("Waiting for resource readings…", "TasktorInk2Brush"));
        }

        var columns = new Grid { ColumnSpacing = 16, RowSpacing = 24 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var apps = SystemApps(shown.Any(c => c.Id == "core.apps"));
        Grid.SetColumn(apps, 1);
        columns.Children.Add(resources);
        columns.Children.Add(apps);
        columns.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 680;
            columns.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1.1, GridUnitType.Star);
            Grid.SetColumn(apps, narrow ? 0 : 1);
            Grid.SetRow(apps, narrow ? 1 : 0);
        };
        target.Children.Add(columns);

        // Keep guards, battery and optional sources reachable; the overview only replaces the duplicated system/apps cards.
        foreach (var card in shown.Where(c => c.Id is not ("core.system" or "core.apps")))
        {
            var rows = renderer.Render(CardFitter.Fit(card, FitTarget.Flyout, resolver), resolver);
            target.Children.Add(Section(rows with { Actions = [.. rows.Actions, DeepPages.PinAction(card.Id, DeepPages.Pinned(_snapshot.Mirror.Root))] }));
        }
    }

    private StackPanel SystemApps(bool canPin)
    {
        if (DateTimeOffset.Now - _procsAt > TimeSpan.FromSeconds(5) && !_pullingProcs)
        {
            _pullingProcs = true;
            _ = PullProcessesAsync();
        }

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(SystemHeading("Apps by memory"));
        var group = new StackPanel { Spacing = 2 };
        var apps = AppsTree.From(_procs, byMemory: true);
        foreach (var app in apps)
        {
            var detail = (app.Count == 1 ? "1 process" : app.Count + " processes") + ", " + app.Cpu + " CPU";
            StackPanel MakeBody()
            {
                var body = new StackPanel { Spacing = 2 };
                var current = AppsTree.From(_procs, byMemory: true).FirstOrDefault(a => a.AppId == app.AppId);
                if (current is null)
                {
                    return body;
                }

                foreach (var process in current.Processes)
                {
                    var row = SystemSurface(SystemRow(process.Exe, process.Memory,
                        process.Locked ?? process.Cpu + " CPU", null));
                    row.Tag = "process:" + process.Pid + ":" + (process.Actions.FirstOrDefault()?.Target?.GetValueOrDefault("created") ?? string.Empty);
                    if (process.Actions.Count > 0)
                    {
                        row.ContextFlyout = Menu(process.Actions);
                    }

                    body.Children.Add(row);
                }

                if (current.Actions.Count > 0)
                {
                    var actions = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 8, 0, 0) };
                    foreach (var action in current.Actions)
                    {
                        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        var button = new Button
                        {
                            Tag = ActionKey(action, null),
                            Content = new TextBlock
                            {
                                Text = action.Id switch { "lower" => "Lower priority", "eco" => "Efficiency mode", "end" => "End…", _ => action.Label },
                                TextWrapping = TextWrapping.Wrap,
                                FontSize = 12,
                            },
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            Padding = new Thickness(8, 6, 8, 6),
                        };
                        Grid.SetColumn(button, actions.ColumnDefinitions.Count - 1);
                        AutomationProperties.SetName(button, action.Label);
                        button.Click += async (_, _) => await RunAsync(action);
                        actions.Children.Add(button);
                    }

                    body.Children.Add(actions);
                }

                return body;
            }

            var expander = new Expander
            {
                Tag = "app:" + app.AppId,
                Header = SystemRow(app.Name, app.Memory, detail, null),
                Content = _expanded.Contains(app.AppId) ? MakeBody() : null,
                IsExpanded = _expanded.Contains(app.AppId),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = ThemeBrush("TasktorSurfaceBrush"),
                BorderThickness = new Thickness(0),
            };
            expander.Expanding += (_, _) =>
            {
                _expanded.Add(app.AppId);
                expander.Content ??= MakeBody();
            };
            expander.Collapsed += (_, _) => _expanded.Remove(app.AppId);
            AutomationProperties.SetName(expander, app.Name + ", " + app.Memory + ", " + detail);
            group.Children.Add(expander);
        }

        if (apps.Count == 0)
        {
            group.Children.Add(Text(_procs is null ? "Reading the process table…" : "No apps in this snapshot.", "TasktorInk2Brush"));
        }

        panel.Children.Add(group);
        if (canPin)
        {
            var pin = DeepPages.PinAction("core.apps", DeepPages.Pinned(_snapshot.Mirror.Root));
            var button = new HyperlinkButton { Content = pin.Label, Foreground = ThemeBrush("TasktorAccentBrush") };
            button.Tag = ActionKey(pin, null);
            button.Click += async (_, _) => await RunAsync(pin);
            panel.Children.Add(button);
        }

        return panel;
    }

    private CardRow? CurrentSystemHistory(string resourceId)
    {
        var card = DeepPages.CardsFor("system", _snapshot.Mirror.Cards).FirstOrDefault(c => c.Id == "core.system");
        if (card is null)
        {
            return null;
        }

        var resolver = new BindingResolver(_snapshot.Mirror.Root);
        return new RowRenderer(DateTimeOffset.Now).Render(CardFitter.Fit(card, FitTarget.Flyout, resolver), resolver)
            .Rows.FirstOrDefault(r => r.Kind == RowKind.Chart && r.Label == resourceId);
    }

    // Build a proposed view off-tree, then update matching controls in place. This preserves keyboard focus and
    // open flyouts; a structural change waits until interaction ends, while compatible branches still refresh.
    private void RefreshSystem(IReadOnlyList<Card> cards)
    {
        if (!_systemMounted || _page != "system")
        {
            return;
        }

        var proposed = new StackPanel { Spacing = PageContent.Spacing };
        DrawSystem(cards, new BindingResolver(_snapshot.Mirror.Root), proposed);

        if (SameSystemStructure(PageContent, proposed))
        {
            SyncSystemTree(PageContent, proposed);
            return;
        }

        if (SystemInteractionActive())
        {
            SyncCompatibleSystemTree(PageContent, proposed);
            return;
        }

        PageContent.Children.Clear();
        while (proposed.Children.Count > 0)
        {
            var child = proposed.Children[0];
            proposed.Children.RemoveAt(0);
            PageContent.Children.Add(child);
        }
    }

    private bool SystemInteractionActive()
    {
        if (Content.XamlRoot is not { } root)
        {
            return false;
        }

        if (VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Count > 0)
        {
            return true;
        }

        for (var focused = FocusManager.GetFocusedElement(root) as DependencyObject; focused is not null; focused = VisualTreeHelper.GetParent(focused))
        {
            if (ReferenceEquals(focused, PageContent))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameSystemStructure(UIElement oldElement, UIElement newElement)
    {
        if (oldElement.GetType() != newElement.GetType())
        {
            return false;
        }

        if (oldElement is FrameworkElement oldFramework && newElement is FrameworkElement newFramework &&
            (!Equals(oldFramework.Tag, newFramework.Tag) || oldElement is not TextBlock && !SameSystemMenu(oldFramework.ContextFlyout, newFramework.ContextFlyout)))
        {
            return false;
        }

        if (oldElement is FontIcon oldIcon && newElement is FontIcon newIcon && oldIcon.Glyph != newIcon.Glyph)
        {
            return false;
        }

        var oldChildren = SystemChildren(oldElement);
        var newChildren = SystemChildren(newElement);
        return oldChildren.Count == newChildren.Count && oldChildren.Zip(newChildren).All(pair => SameSystemStructure(pair.First, pair.Second));
    }

    private static bool SameSystemMenu(FlyoutBase? oldFlyout, FlyoutBase? newFlyout)
    {
        if (oldFlyout is null || newFlyout is null)
        {
            return oldFlyout is null && newFlyout is null;
        }

        if (oldFlyout is not MenuFlyout oldMenu || newFlyout is not MenuFlyout newMenu || oldMenu.Items.Count != newMenu.Items.Count)
        {
            return false;
        }

        return oldMenu.Items.Zip(newMenu.Items).All(pair => Equals(pair.First.Tag, pair.Second.Tag));
    }

    private static List<UIElement> SystemChildren(UIElement element)
    {
        var result = new List<UIElement>();
        switch (element)
        {
            case Panel panel:
                result.AddRange(panel.Children);
                break;
            case Border { Child: { } child }:
                result.Add(child);
                break;
            case Expander expander:
                if (expander.Header is UIElement header) result.Add(header);
                if (expander.Content is UIElement body) result.Add(body);
                break;
            case ContentControl { Content: UIElement content }:
                result.Add(content);
                break;
        }

        return result;
    }

    private static void SyncCompatibleSystemTree(UIElement oldElement, UIElement newElement)
    {

        if (SameSystemStructure(oldElement, newElement))
        {
            SyncSystemTree(oldElement, newElement);
            return;
        }

        if (oldElement.GetType() != newElement.GetType() || oldElement is FrameworkElement oldFramework &&
            newElement is FrameworkElement newFramework && !Equals(oldFramework.Tag, newFramework.Tag))
        {
            return;
        }

        // Stable tagged controls retain their identity even if a child changes structurally. Keep their
        // accessible name current as compatible descendants receive updated values.
        if (oldElement is FrameworkElement { Tag: string } && newElement is FrameworkElement)
        {
            AutomationProperties.SetName(oldElement, AutomationProperties.GetName(newElement));
        }

        var oldChildren = SystemChildren(oldElement);
        var newChildren = SystemChildren(newElement);
        for (var i = 0; i < Math.Min(oldChildren.Count, newChildren.Count); i++)
        {
            SyncCompatibleSystemTree(oldChildren[i], newChildren[i]);
        }
    }

    private static void SyncSystemTree(UIElement oldElement, UIElement newElement)
    {
        if (oldElement is TextBlock oldText && newElement is TextBlock newText)
        {
            oldText.Text = newText.Text;
            oldText.Foreground = newText.Foreground;
            oldText.Style = newText.Style;
        }
        else if (oldElement is ProgressBar oldProgress && newElement is ProgressBar newProgress)
        {
            oldProgress.Value = newProgress.Value;
        }
        else if (oldElement is Microsoft.UI.Xaml.Shapes.Polyline oldLine && newElement is Microsoft.UI.Xaml.Shapes.Polyline newLine)
        {
            oldLine.Points.Clear();
            foreach (var point in newLine.Points) oldLine.Points.Add(point);
        }
        else if (oldElement is ContentControl oldContent && newElement is ContentControl newContent && newContent.Content is string value)
        {
            oldContent.Content = value;
        }

        AutomationProperties.SetName(oldElement, AutomationProperties.GetName(newElement));
        var oldChildren = SystemChildren(oldElement);
        var newChildren = SystemChildren(newElement);
        for (var i = 0; i < oldChildren.Count; i++) SyncSystemTree(oldChildren[i], newChildren[i]);
    }

    private static string ActionKey(CardAction action, string? rowKey)
    {
        static string Map(IReadOnlyDictionary<string, string>? values) => values is null ? "-" : string.Join("&", values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        return string.Join("|", action.Id, action.Action ?? "", action.Destructive == true ? "1" : "0", Uri.EscapeDataString(rowKey ?? ""), Map(action.Target), Map(action.Args));
    }

    // Old controls can remain mounted while a structural update is deferred. Never invoke an action from such a
    // control unless its complete target and arguments are still present in the latest data, including creation time.
    private bool CurrentSystemAction(CardAction action, string? rowKey)
    {
        if (!_snapshot.Connected) return false;
        var key = ActionKey(action, rowKey);
        var resolver = new BindingResolver(_snapshot.Mirror.Root);
        var pinned = DeepPages.Pinned(_snapshot.Mirror.Root);
        foreach (var card in DeepPages.CardsFor("system", _snapshot.Mirror.Cards))
        {
            if (ActionKey(DeepPages.PinAction(card.Id, pinned), null) == key) return true;
            var rows = new RowRenderer(DateTimeOffset.Now).Render(CardFitter.Fit(card, FitTarget.Flyout, resolver), resolver);
            if (rowKey is null && rows.Actions.Any(a => ActionKey(a, null) == key)) return true;
            if (rowKey is not null && rows.Rows.Any(r => r.RowKey == rowKey) && rows.RowActions.Any(a => ActionKey(a, rowKey) == key)) return true;
        }

        return rowKey is null && AppsTree.From(_procs).Any(app =>
            app.Actions.Any(a => ActionKey(a, null) == key) ||
            app.Processes.Any(process => process.Actions.Any(a => ActionKey(a, null) == key)));
    }

    private static TextBlock SystemHeading(string label) => new()
    {
        Text = label,
        Style = StyleOf("TasktorBodyTextStyle"),
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = ThemeBrush("TasktorInkBrush"),
    };

    private static Border SystemSurface(UIElement content) => new()
    {
        Background = ThemeBrush("TasktorSurfaceBrush"),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(16, 8, 16, 8),
        Child = content,
    };

    private static Grid SystemRow(string label, string value, string? description, string? glyph)
    {
        var row = new Grid { MinHeight = 48, ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (glyph is not null)
        {
            row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 20, FontFamily = new FontFamily("Segoe Fluent Icons"), Foreground = ThemeBrush("TasktorInk2Brush") });
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Text(label, "TasktorInkBrush"));
        if (description is not null)
        {
            text.Children.Add(Caption(description, "TasktorInk2Brush"));
        }

        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var amount = Text(value, "TasktorInk2Brush");
        amount.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(amount, 2);
        row.Children.Add(amount);
        return row;
    }

    private static string ResourceGlyph(string label) => label switch
    {
        "CPU" => "\uE950",
        "RAM" => "\uE964",
        "GPU" => "\uE7F4",
        "Down" => "\uE701",
        _ => "\uEDA2",
    };

    private static FontIcon NavigationIcon(string page) => new()
    {
        FontFamily = new FontFamily("Segoe Fluent Icons"),
        FontSize = 20,
        Glyph = page switch
        {
            "home" => "\uE80F",
            "system" => "\uE770",
            "jobs" => "\uE8EF",
            "activity" => "\uE9D9",
            "learned" => "\uE8F1",
            "focus" => "\uE916",
            "windows" => "\uE8A9",
            "data" => "\uE8B7",
            _ => "\uE713",
        },
    };
}
