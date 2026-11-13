using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tasktor.Hosts.Common.Deep;
using Tasktor.Hosts.Common.Render;

namespace Tasktor.Flyout;

public sealed partial class MainWindow
{
    private string _learnedTab = "workflows";
    private bool _learnedAttempted;
    private bool _workflowsAttempted;

    private void DrawLearnedPage()
    {
        PageTitle.Text = "Learned";
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
        foreach (var (id, name) in new[] { ("workflows", "Workflows"), ("jobs", "Jobs"), ("capture", "Deep capture") })
        {
            var selected = id == _learnedTab;
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = name,
                Style = StyleOf(selected ? "TasktorBodyStrongTextStyle" : "TasktorBodyTextStyle"),
                Foreground = ThemeBrush(selected ? "TasktorInkBrush" : "TasktorInk2Brush"),
            });
            content.Children.Add(new Border
            {
                Width = 16,
                Height = 3,
                CornerRadius = new CornerRadius(2),
                Background = selected ? ThemeBrush("TasktorAccentBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
            });
            var button = new Button
            {
                Content = content,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 6, 0, 0),
            };
            AutomationProperties.SetName(button, name + (selected ? ", selected" : string.Empty));
            button.Click += (_, _) =>
            {
                _learnedTab = id;
                Draw(_snapshot.Mirror.Cards);
            };
            tabs.Children.Add(button);
        }

        PageContent.Children.Add(tabs);
        if (!_snapshot.Connected)
        {
            PageContent.Children.Add(Text("Tasktor is not running.", "TasktorInk2Brush"));
            return;
        }

        // Each report can be unavailable independently; one must not hide the other.
        if (_learned is null && !_pullingLearned && !_learnedAttempted)
        {
            _pullingLearned = true;
            _ = PullLearnedAsync();
        }

        if (_workflows is null && !_pullingWorkflows && !_workflowsAttempted)
        {
            _pullingWorkflows = true;
            _ = PullWorkflowsAsync();
        }

        if (_learnedTab == "capture")
        {
            DrawLearnedCapture();
            return;
        }

        var left = new StackPanel { Spacing = 10 };
        var right = new StackPanel { Spacing = 10 };
        if (_learnedTab == "jobs")
        {
            left.Children.Add(SystemHeading("Job kinds"));
            right.Children.Add(SystemHeading("Learning and guardrails"));
            if (_learned is null)
            {
                left.Children.Add(Text(_learnedAttempted ? "Job learning is unavailable." : "Reading what Tasktor learned…", "TasktorInk2Brush"));
                if (_learnedAttempted) left.Children.Add(LearnedRetry("jobs"));
            }
            else
            {
                foreach (var section in LearnedPage.Sections(_learned))
                {
                    (section.Id is "learned.learning" or "learned.guardrails" ? right : left).Children.Add(Section(section));
                }
            }
        }
        else
        {
            left.Children.Add(SystemHeading("Your workflows"));
            DrawWorkflows(left);
            right.Children.Add(SystemHeading("How it learns"));
            var workflow = _snapshot.Mirror.Cards.FirstOrDefault(c => c.Id == "core.workflow");
            if (workflow is null)
            {
                right.Children.Add(Text("Workflow tracking is not enabled or is unavailable.", "TasktorInk2Brush"));
                right.Children.Add(LearnedSettingsLink());
            }
            else
            {
                var resolver = new BindingResolver(_snapshot.Mirror.Root);
                var rows = new RowRenderer(DateTimeOffset.Now).Render(CardFitter.Fit(workflow, FitTarget.Flyout, resolver), resolver);
                right.Children.Add(Section(rows with { Actions = [.. rows.Actions, DeepPages.PinAction(workflow.Id, DeepPages.Pinned(_snapshot.Mirror.Root))] }));
            }
        }

        var columns = new Grid { ColumnSpacing = 16, RowSpacing = 24 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.8, GridUnitType.Star) });
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(right, 1);
        columns.Children.Add(left);
        columns.Children.Add(right);
        columns.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 680;
            columns.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(0.8, GridUnitType.Star);
            Grid.SetColumn(right, narrow ? 0 : 1);
            Grid.SetRow(right, narrow ? 1 : 0);
        };
        PageContent.Children.Add(columns);
    }

    private void DrawLearnedCapture()
    {
        PageContent.Children.Add(SystemHeading("Deep capture"));
        var card = _snapshot.Mirror.Cards.FirstOrDefault(c => c.Id == "core.capture");
        if (card is null)
        {
            PageContent.Children.Add(Text("Capture is not enabled or is unavailable.", "TasktorInk2Brush"));
        }
        else
        {
            var resolver = new BindingResolver(_snapshot.Mirror.Root);
            var rows = new RowRenderer(DateTimeOffset.Now).Render(CardFitter.Fit(card, FitTarget.Flyout, resolver), resolver);
            PageContent.Children.Add(Section(rows with { Actions = [.. rows.Actions, DeepPages.PinAction(card.Id, DeepPages.Pinned(_snapshot.Mirror.Root))] }));
        }

        PageContent.Children.Add(LearnedSettingsLink());
    }

    private Button LearnedRetry(string source)
    {
        var retry = new Button { Content = "Try again", HorizontalAlignment = HorizontalAlignment.Left };
        retry.Click += (_, _) =>
        {
            if (source == "jobs") _learnedAttempted = false;
            else _workflowsAttempted = false;
            Draw(_snapshot.Mirror.Cards);
        };
        return retry;
    }

    private Button LearnedSettingsLink()
    {
        var button = new Button { Content = "Open Settings", HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => OpenPage("settings");
        return button;
    }
}
