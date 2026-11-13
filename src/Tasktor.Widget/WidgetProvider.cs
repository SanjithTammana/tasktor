using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;
using Tasktor.Hosts.Common.Pipe;

namespace Tasktor.Widget;

/// <summary>
/// Serves aggregate data for active widgets. Host callbacks never wait on an update: the Widgets contract lets a provider
/// update a widget at any time, so the one update that may already be in flight when <see cref="Deactivate"/> arrives is
/// allowed, and blocking the callback on it could deadlock against a host that holds its own lock across the call. No
/// later update reaches that instance (the generation check), and every payload is aggregate-only.
/// </summary>
internal sealed class WidgetProvider(ProviderConnections connections) : IWidgetProvider, ICustomQueryInterface
{
    private static readonly WidgetActivity Active = new();
    private static readonly SnapshotClient Snapshot = new("widget");
    private readonly ExternalConnection _external = new(connections);

    static WidgetProvider() => _ = Task.Run(UpdateLoopAsync);

    /// <summary>Hands COM's stub manager the connection counter that ends the server (<see cref="ServerLifetime"/>).</summary>
    public CustomQueryInterfaceResult GetInterface(ref Guid iid, out IntPtr ppv)
    {
        if (iid == ExternalConnection.Iid)
        {
            ppv = Marshal.GetComInterfaceForObject(_external, typeof(IExternalConnection));
            return CustomQueryInterfaceResult.Handled;
        }

        ppv = IntPtr.Zero;
        return CustomQueryInterfaceResult.NotHandled;
    }

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
                                // No lock over this host call (see the class remarks): an update already past
                                // the check may land just after Deactivate, which the Widgets contract allows.
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
