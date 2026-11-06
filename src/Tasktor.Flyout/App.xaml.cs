using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Tasktor.Hosts.Common.Pipe;

namespace Tasktor.Flyout;

/// <summary>
/// The flyout process hosts the shelf and the deep window (U4 amended D-G11, D-G13). One process per logon session: a
/// second launch signals the first and exits. It exits on its own 30 s after nothing is showing (U1 D-H10: 0 MB at
/// rest; the edge and the tray icon live in the agent).
/// </summary>
public partial class App : Application
{
    public static readonly TimeSpan ExitAfterHidden = TimeSpan.FromSeconds(30);

    private Mutex? _instance;
    private SnapshotClient? _snapshot;
    private ShelfWindow? _shelf;
    private MainWindow? _deep;
    private DispatcherQueueTimer? _exit;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var wanted = ViewerSignal.ForArgs(Environment.GetCommandLineArgs());
        _instance = new Mutex(true, ViewerSignal.Instance, out var first);
        if (!first)
        {
            using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, wanted))
            {
                signal.Set();
            }

            Exit();
            return;
        }

        var ui = DispatcherQueue.GetForCurrentThread();
        _snapshot = new SnapshotClient("flyout");
        _snapshot.Start();
        _shelf = new ShelfWindow(_snapshot);
        _shelf.Hidden += StartExitCountdown;
        _shelf.DeepRequested += OpenDeep;

        _exit = ui.CreateTimer();
        _exit.Interval = ExitAfterHidden;
        _exit.IsRepeating = false;
        _exit.Tick += (_, _) =>
        {
            if (_shelf?.IsShown != true && _deep is null)
            {
                Exit();
            }
        };

        Listen(ViewerSignal.ShowShelf, ui, () => _shelf.ShowShelf());
        Listen(ViewerSignal.OpenDeep, ui, OpenDeep);
        if (wanted == ViewerSignal.OpenDeep)
        {
            OpenDeep();
        }
        else
        {
            _shelf.ShowShelf();
        }
    }

    private void OpenDeep()
    {
        _exit?.Stop();
        if (_deep is null)
        {
            _deep = new MainWindow();
            _deep.Closed += (_, _) =>
            {
                _deep = null;
                StartExitCountdown();
            };
        }

        _deep.Activate();
    }

    private void StartExitCountdown()
    {
        _exit?.Stop();
        _exit?.Start();
    }

    /// <summary>A background wait on a named event; each set runs <paramref name="show"/> on the UI thread.</summary>
    private static void Listen(string name, DispatcherQueue ui, Action show)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        var thread = new Thread(() =>
        {
            while (signal.WaitOne())
            {
                ui.TryEnqueue(() => show());
            }
        })
        {
            IsBackground = true,
            Name = "tasktor-flyout-signal",
        };
        thread.Start();
    }
}
