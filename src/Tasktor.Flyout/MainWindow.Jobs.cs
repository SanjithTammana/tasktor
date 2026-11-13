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
    private JsonNode? _jobsLearned;
    private DateTimeOffset _jobsLearnedAt = DateTimeOffset.MinValue;
    private bool _pullingJobsLearned;

    // The board groups the real job feed and its learned durations; it does not supply sample jobs or actions.
    private void DrawJobs(IReadOnlyList<Card> cards, BindingResolver resolver)
    {
        if (!_snapshot.Connected)
        {
            PageContent.Children.Add(Text("Tasktor is not running.", "TasktorInk2Brush"));
            return;
        }

        if (!_pullingJobsLearned && DateTimeOffset.Now - _jobsLearnedAt > TimeSpan.FromSeconds(30))
        {
            _pullingJobsLearned = true;
            _ = PullJobsLearnedAsync();
        }

        var shown = DeepPages.CardsFor("jobs", cards);
        var jobsCard = shown.FirstOrDefault(c => c.Id == "core.jobs");
        var left = new StackPanel { Spacing = 10 };
        var right = new StackPanel { Spacing = 10 };
        left.Children.Add(SystemHeading("Running"));

        if (jobsCard is null)
        {
            left.Children.Add(Text("Waiting for job readings…", "TasktorInk2Brush"));
        }
        else
        {
            var jobs = (_snapshot.Mirror.Root?["state"]?["jobs"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(j => JobString(j, "id") is not null && JobString(j, "label") is not null).ToList();
            var running = jobs.Where(j => !JobFinished(j)).ToList();
            var recent = jobs.Where(JobFinished).ToList();
            left.Children.Add(JobGroup(running, jobsCard, "No jobs are running."));
            left.Children.Add(SystemHeading("Recent"));
            left.Children.Add(JobGroup(recent, jobsCard, "No recent jobs in this snapshot."));
            var pin = DeepPages.PinAction(jobsCard.Id, DeepPages.Pinned(_snapshot.Mirror.Root));
            var pinButton = new HyperlinkButton { Content = pin.Label, Foreground = ThemeBrush("TasktorAccentBrush") };
            pinButton.Click += async (_, _) => await RunAsync(pin);
            left.Children.Add(pinButton);
        }

        right.Children.Add(SystemHeading("Watching"));
        if (jobsCard is not null)
        {
            right.Children.Add(SystemSurface(ChoiceRow("Watch a program as a job", "Pick a running process; Tasktor tracks it like any job", "Choose…", WatchProgramAsync)));
        }

        var otherCards = shown.Where(c => c.Id != "core.jobs").ToList();
        if (otherCards.Count == 0)
        {
            right.Children.Add(Text("No additional watcher cards are available.", "TasktorInk2Brush"));
        }
        else
        {
            var renderer = new RowRenderer(DateTimeOffset.Now);
            var pinned = DeepPages.Pinned(_snapshot.Mirror.Root);
            foreach (var card in otherCards)
            {
                var rows = renderer.Render(CardFitter.Fit(card, FitTarget.Flyout, resolver), resolver);
                right.Children.Add(Section(rows with { Actions = [.. rows.Actions, DeepPages.PinAction(card.Id, pinned)] }));
            }
        }

        if (_jobsLearned is JsonObject learned)
        {
            var learning = LearnedPage.Sections(learned).FirstOrDefault(s => s.Id == "learned.learning");
            if (learning is { Actions.Count: > 0, Rows.Count: > 0 })
            {
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = new StackPanel();
                label.Children.Add(Text("Learn how long my jobs take", "TasktorInkBrush"));
                label.Children.Add(Caption("Learning: " + learning.Rows[0].Value, "TasktorInk2Brush"));
                row.Children.Add(label);
                var action = learning.Actions[0];
                var toggle = new Button { Content = action.Label, VerticalAlignment = VerticalAlignment.Center };
                toggle.Click += async (_, _) =>
                {
                    if (await RunAsync(action) is { Ok: true })
                    {
                        _jobsLearnedAt = DateTimeOffset.MinValue;
                        if (_page == "jobs") Draw(_snapshot.Mirror.Cards);
                    }
                };
                Grid.SetColumn(toggle, 1);
                row.Children.Add(toggle);
                right.Children.Add(SystemSurface(row));
            }
        }

        right.Children.Add(SystemHeading("Usual durations"));
        var durations = new StackPanel { Spacing = 2 };
        foreach (var kind in (_jobsLearned?["kinds"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (BindingResolver.AsDouble(kind["usual_duration_s"]) is not { } seconds) continue;
            var name = JobString(kind, "name") ?? "Job kind";
            var runs = BindingResolver.AsDouble(kind["runs"]);
            var detail = runs is { } count ? ((long)count).ToString(CultureInfo.InvariantCulture) + (count == 1 ? " run" : " runs") : null;
            durations.Children.Add(SystemSurface(SystemRow(name, LearnedPage.Duration(seconds), detail, null)));
        }

        if (durations.Children.Count == 0)
        {
            durations.Children.Add(Text(_jobsLearned is null && _jobsLearnedAt == DateTimeOffset.MinValue
                ? "Reading learned durations…" : _jobsLearned is null ? "Learned durations are unavailable." : "No usual durations learned yet.", "TasktorInk2Brush"));
        }

        right.Children.Add(durations);
        var columns = new Grid { ColumnSpacing = 16, RowSpacing = 24 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.85, GridUnitType.Star) });
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(right, 1);
        columns.Children.Add(left);
        columns.Children.Add(right);
        columns.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 680;
            columns.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(0.85, GridUnitType.Star);
            Grid.SetColumn(right, narrow ? 0 : 1);
            Grid.SetRow(right, narrow ? 1 : 0);
        };
        PageContent.Children.Add(columns);
    }

    private StackPanel JobGroup(IReadOnlyList<JsonObject> jobs, Card card, string empty)
    {
        var group = new StackPanel { Spacing = 2 };
        foreach (var job in jobs)
        {
            var id = JobString(job, "id")!;
            var label = JobString(job, "label")!;
            var status = JobString(job, "status") ?? "unknown";
            var internalJob = job["internal"] is JsonValue iv && iv.TryGetValue<bool>(out var b) && b;
            var body = new StackPanel { Spacing = 6 };
            var header = new Grid { ColumnSpacing = 12 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock { Text = label, Style = StyleOf("TasktorBodyStrongTextStyle"), Foreground = ThemeBrush("TasktorInkBrush"), TextWrapping = TextWrapping.Wrap });
            if (BindingResolver.AsDouble(job["pct"]) is { } pct)
            {
                var amount = Text(BindingResolver.Format(JsonValue.Create(pct)) + "%", "TasktorInkBrush");
                Grid.SetColumn(amount, 1);
                header.Children.Add(amount);
                var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = Math.Clamp(pct, 0, 100), Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(bar, label + " progress " + BindingResolver.Format(JsonValue.Create(pct)) + " percent");
                body.Children.Add(header);
                body.Children.Add(bar);
            }
            else
            {
                body.Children.Add(header);
            }

            var details = new List<string> { status.Replace('_', ' ') };
            foreach (var field in new[] { "stage", "msg", "basis" })
            {
                if (JobString(job, field) is { } value) details.Add(value);
            }

            if (job["longer_than_usual"] is JsonValue uv && uv.TryGetValue<bool>(out var longer) && longer) details.Add("Longer than usual");
            if (BindingResolver.AsDouble(job["eta_s"]) is { } eta && eta >= 0 && !JobFinished(job)) details.Add("about " + LearnedPage.Duration(eta) + " left");
            if (BindingResolver.AsDouble(job["exit_code"]) is { } code && JobFinished(job)) details.Add("exit " + ((long)code).ToString(CultureInfo.InvariantCulture));
            body.Children.Add(new TextBlock { Text = string.Join(" · ", details), Style = StyleOf("TasktorCaptionTextStyle"), Foreground = ThemeBrush("TasktorInk2Brush"), TextWrapping = TextWrapping.Wrap });

            if (!internalJob)
            {
                var controllable = status is "starting" or "working" or "working_gpu" or "quiet" or "silent" or "stuck_busy" or "paused";
                var hasLog = job["has_log"] is JsonValue lv && lv.TryGetValue<bool>(out var h) && h;
                var actionIds = new List<string>();
                if (controllable) actionIds.Add(status == "paused" ? "resume" : "pause");
                if (hasLog) actionIds.Add("log");
                if (controllable) actionIds.Add("end");
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                foreach (var actionId in actionIds)
                {
                    var action = card.Actions?.FirstOrDefault(a => a.Id == actionId && RowRenderer.NeedsRow(a));
                    if (action is null) continue;
                    var button = new Button { Content = action.Label, Padding = new Thickness(10, 5, 10, 5) };
                    AutomationProperties.SetName(button, action.Label + " " + label);
                    button.Click += async (_, _) => await RunAsync(action, id);
                    buttons.Children.Add(button);
                }

                if (buttons.Children.Count > 0)
                {
                    body.Children.Add(new ScrollViewer { Content = buttons, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
                }
            }

            group.Children.Add(SystemSurface(body));
        }

        if (group.Children.Count == 0) group.Children.Add(Text(empty, "TasktorInk2Brush"));
        return group;
    }

    /// <summary>A label and caption on the left, one button on the right (the board's "Choose…" row).</summary>
    private static Grid ChoiceRow(string label, string caption, string button, Func<Task> click)
    {
        var row = new Grid { ColumnSpacing = 12, MinHeight = 48 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Text(label, "TasktorInkBrush"));
        text.Children.Add(new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap, Style = StyleOf("TasktorCaptionTextStyle"), Foreground = ThemeBrush("TasktorInk2Brush") });
        row.Children.Add(text);
        var choose = new Button { Content = button, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(choose, label);
        choose.Click += async (_, _) => await click();
        Grid.SetColumn(choose, 1);
        row.Children.Add(choose);
        return row;
    }

    /// <summary>
    /// B1 "Watch a program as a job": pick one running process (the same ones System lets you act on), then
    /// <c>job.pin</c> it. The agent checks the process again, so one that exited meanwhile is refused with a reason.
    /// </summary>
    private async Task WatchProgramAsync()
    {
        JsonNode? pulled = null;
        try
        {
            pulled = await _snapshot.PullAsync("processes");
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
        }

        var all = JobPicker.From(pulled);
        if (all.Count == 0)
        {
            await InformAsync(pulled is null ? "Couldn't read the running programs. Try again in a moment." : "No running program can be watched right now.");
            return;
        }

        var filter = new TextBox { PlaceholderText = "Type to filter", MinWidth = 380 };
        AutomationProperties.SetName(filter, "Filter programs");
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 360 };
        AutomationProperties.SetName(list, "Running programs");
        void Fill()
        {
            list.Items.Clear();
            foreach (var choice in JobPicker.Filter(all, filter.Text))
            {
                var item = new StackPanel { Padding = new Thickness(0, 4, 0, 4), Tag = choice };
                item.Children.Add(new TextBlock { Text = choice.Exe, Style = StyleOf("TasktorBodyTextStyle"), Foreground = ThemeBrush("TasktorInkBrush") });
                item.Children.Add(new TextBlock { Text = choice.Detail + " · PID " + choice.Pid.ToString(CultureInfo.InvariantCulture), Style = StyleOf("TasktorCaptionTextStyle"), Foreground = ThemeBrush("TasktorInk2Brush") });
                AutomationProperties.SetName(item, choice.Exe + ", " + choice.Detail);
                list.Items.Add(item);
            }
        }

        Fill();
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(filter);
        body.Children.Add(list);
        var dialog = new ContentDialog
        {
            Title = "Watch a program as a job",
            Content = body,
            PrimaryButtonText = "Watch",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = Content.XamlRoot,
        };
        filter.TextChanged += (_, _) => Fill();
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is not null;
        if (await ShowTasktorDialogAsync(dialog) != ContentDialogResult.Primary || (list.SelectedItem as FrameworkElement)?.Tag is not JobChoice picked)
        {
            return;
        }

        await RunAsync(picked.Watch);
    }

    private static bool JobFinished(JsonObject job) => JobString(job, "status") is "done" or "failed" or "ended";

    private static string? JobString(JsonObject row, string key) =>
        row[key] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private async Task PullJobsLearnedAsync()
    {
        var refreshed = false;
        try
        {
            var value = await _snapshot.PullAsync("learned");
            if (value is not null)
            {
                _jobsLearned = value;
                refreshed = true;
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // Keep the last successful report and try again later.
        }
        finally
        {
            _jobsLearnedAt = DateTimeOffset.Now;
            _pullingJobsLearned = false;
        }

        if (refreshed) RequestRedraw("jobs");
    }
}
