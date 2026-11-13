using Microsoft.Windows.Widgets.Providers;
using Tasktor.Hosts.Common.Pipe;

namespace Tasktor.Widget;

/// <summary>Serves aggregate data for active widgets.</summary>
internal sealed class WidgetProvider : IWidgetProvider
{
    private static readonly WidgetActivity Active = new();
    private static readonly SnapshotClient Snapshot = new("widget");

    static WidgetProvider() => _ = Task.Run(UpdateLoopAsync);

    public void CreateWidget(WidgetContext widgetContext) { }

    public void DeleteWidget(string widgetId, string customState) => Active.Deactivate(widgetId);

    public void Activate(WidgetContext widgetContext) => Active.Activate(widgetContext.Id, widgetContext.DefinitionId);

    public void Deactivate(string widgetId) => Active.Deactivate(widgetId);

    public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs) { }

    // The glance exposes no executable actions; an unexpected host callback cannot invoke one.
    public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs) { }

    private static async Task UpdateLoopAsync()
    {
        var subscribed = false;
        while (true)
        {
            try
            {
                var active = Active.Capture();
                if (active.Instances.Length == 0)
                {
                    if (subscribed)
                    {
                        await Snapshot.StopAsync().ConfigureAwait(false);
                        subscribed = false;
                    }
                }
                else if (Active.IsCurrent(active))
                {
                    if (!subscribed)
                    {
                        Snapshot.Start();
                        subscribed = true;
                    }

                    System.Text.Json.Nodes.JsonNode? series = null;
                    if (Snapshot.Connected && Active.IsCurrent(active))
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(active.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(1));
                        try
                        {
                            series = await Snapshot.PullAsync("series", new Dictionary<string, string> { ["id"] = "system.cpu_pct" }, ct: timeout.Token).ConfigureAwait(false);
                        }
                        catch (Exception e) when (e is AgentNotRunningException or OperationCanceledException or IOException or InvalidOperationException or ObjectDisposedException)
                        {
                            // The next update reflects the current connection instead of retaining old content.
                        }
                    }

                    if (Active.IsCurrent(active))
                    {
                        var connected = Snapshot.Connected;
                        var root = connected ? Snapshot.Mirror.Root : null;
                        foreach (var instance in active.Instances)
                        {
                            if (!Active.IsCurrent(active)) break;
                            var output = WidgetGlance.Render(connected, root, connected ? series : null, instance.Value);
                            if (!Active.IsCurrent(active)) break;
                            try
                            {
                                // Do not hold a managed lock over this host COM call: deactivation may
                                // race after the check, so one in-flight update can still reach the host.
                                WidgetManager.GetDefault().UpdateWidget(new WidgetUpdateRequestOptions(instance.Key)
                                {
                                    Template = output.TemplateJson,
                                    Data = output.DataJson,
                                });
                            }
                            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
                            {
                                // Host may have removed an instance concurrently; retry active instances next tick.
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Do not let a transient host or pipe exception silently kill future refreshes.
                // Payloads remain privacy-safe and the next pass rechecks connection state.
            }
            await Active.WaitForChangeAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
    }
}
