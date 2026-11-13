using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Plugins;
using Tasktor.Core.Actions;
using Tasktor.Core.Plugins;

namespace Tasktor.Agent.Host;

/// <summary>
/// Plugin contract 2, S4: carries out a plugin's request through the <see cref="ActionRouter"/>, so the same origin policy,
/// guards and undo apply as to a user action. The request is submitted as <see cref="Origin.Rule"/> with rule
/// <c>plugin:&lt;id&gt;</c>: the rule row of the origin policy is exactly the plugin row (reversible actions only, never
/// user-only ones, never lifting a block, never built-in-only ones). The host has already checked the capability.
/// Loop-only.
/// </summary>
public sealed class PluginRequestHandler : IPluginRequests
{
    private readonly ActionRouter _router;

    public PluginRequestHandler(ActionRouter router)
    {
        _router = router;
    }

    public Task<PluginRequestResult> Handle(string pluginId, PluginRequest request)
    {
        ArgumentNullException.ThrowIfNull(pluginId);
        ArgumentNullException.ThrowIfNull(request);
        return request switch
        {
            JobPauseRequest p => Route(pluginId, p.Id, "job.pause", p.Job, p.Mode is { } mode ? new Dictionary<string, string>(StringComparer.Ordinal) { ["mode"] = mode } : null),
            JobResumeRequest p => Route(pluginId, p.Id, "job.resume", p.Job, null),
            ArrangeRequest a => Done(a.Id, PluginRequestStatus.Refused, "Arranging windows from plugins is not available yet"),
            SiteBlockRequest or SiteUnblockRequest => Done(request.Id, PluginRequestStatus.Refused, "Update the Tasktor extension"),
            _ => Done(request.Id, PluginRequestStatus.Refused, "Unknown request"),
        };
    }

    private Task<PluginRequestResult> Route(string pluginId, string requestId, string action, string job, IReadOnlyDictionary<string, string>? args)
    {
        var req = new ActionRequest
        {
            Action = action,
            Target = new Dictionary<string, string>(StringComparer.Ordinal) { ["job"] = job },
            Args = args,
            Origin = Origin.Rule,
            Rule = "plugin:" + pluginId,
        };
        return _router.Submit(req).ContinueWith(
            t => t.IsCompletedSuccessfully ? Map(requestId, t.Result) : new PluginRequestResult { Id = requestId, Status = PluginRequestStatus.Failed, Message = "The request failed" },
            TaskScheduler.Default);
    }

    /// <summary>The router's answer in the plugin's terms.</summary>
    public static PluginRequestResult Map(string requestId, ActionResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return r.Status switch
        {
            ActionStatus.Ok or ActionStatus.Partial => new PluginRequestResult { Id = requestId, Status = PluginRequestStatus.Ok, Message = r.Message },
            ActionStatus.Failed => new PluginRequestResult { Id = requestId, Status = PluginRequestStatus.Failed, Message = r.Message ?? r.Reason },
            ActionStatus.NeedsConfirm => new PluginRequestResult { Id = requestId, Status = PluginRequestStatus.Refused, Message = "This needs you to confirm it" },
            _ => new PluginRequestResult { Id = requestId, Status = PluginRequestStatus.Refused, Message = r.Message ?? r.Reason },
        };
    }

    private static Task<PluginRequestResult> Done(string id, PluginRequestStatus status, string message) =>
        Task.FromResult(new PluginRequestResult { Id = id, Status = status, Message = message });
}
