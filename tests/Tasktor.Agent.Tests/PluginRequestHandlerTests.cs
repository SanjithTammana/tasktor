using Tasktor.Agent.Host;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Plugins;
using Tasktor.Core.Actions;
using Tasktor.Core.Loop;
using Tasktor.Tests.Shared;

namespace Tasktor.Agent.Tests;

/// <summary>Plugin contract 2, S4: a plugin's request goes through the router under the rule row of the origin policy.</summary>
public sealed class PluginRequestHandlerTests
{
    private readonly LogicLoop _loop = new(new FakeClock());
    private readonly ActionRouter _router;
    private readonly List<ActionRequest> _seen = [];

    public PluginRequestHandlerTests()
    {
        _router = new ActionRouter(_loop, new OriginPolicy(), GuardChain.Default(), _ => null, offLoop: work => Task.FromResult(work()));
        foreach (var action in new[] { "job.pause", "job.resume" })
        {
            _router.Register(action, req =>
            {
                _seen.Add(req);
                return new PreparedAction
                {
                    Summary = action,
                    Immediate = new ActionResult { Action = action, Status = ActionStatus.Ok, Message = "Paused train.py" },
                };
            });
        }
    }

    private PluginRequestResult Handle(PluginRequest r) => LoopPump.Run(_loop, new PluginRequestHandler(_router).Handle("focus", r));

    [Fact]
    public void A_pause_is_routed_as_the_plugin_with_the_rule_policy()
    {
        var result = Handle(new JobPauseRequest { Id = "p1", Job = "j1", Mode = "freeze" });

        var req = Assert.Single(_seen);
        Assert.Equal("job.pause", req.Action);
        Assert.Equal(Origin.Rule, req.Origin);
        Assert.Equal("plugin:focus", req.Rule);
        Assert.Equal("j1", req.Target!["job"]);
        Assert.Equal("freeze", req.Args!["mode"]);
        Assert.Equal(PluginRequestStatus.Ok, result.Status);
        Assert.Equal("p1", result.Id);
    }

    [Fact]
    public void A_resume_is_routed_the_same_way()
    {
        Handle(new JobResumeRequest { Id = "r1", Job = "j1" });
        Assert.Equal("job.resume", Assert.Single(_seen).Action);
    }

    [Fact]
    public void Arrange_and_browser_requests_are_refused_for_now_without_routing()
    {
        Assert.Equal(PluginRequestStatus.Refused, Handle(new ArrangeRequest { Id = "a", Preset = "Deep work" }).Status);
        Assert.Equal("Update the Tasktor extension", Handle(new SiteUnblockRequest { Id = "b" }).Message);
        Assert.Empty(_seen);
    }

    [Theory]
    [InlineData(ActionStatus.Ok, PluginRequestStatus.Ok)]
    [InlineData(ActionStatus.Partial, PluginRequestStatus.Ok)]
    [InlineData(ActionStatus.Refused, PluginRequestStatus.Refused)]
    [InlineData(ActionStatus.NeedsConfirm, PluginRequestStatus.Refused)]
    [InlineData(ActionStatus.Failed, PluginRequestStatus.Failed)]
    public void Router_answers_map_to_plugin_answers(ActionStatus from, PluginRequestStatus to)
    {
        Assert.Equal(to, PluginRequestHandler.Map("x", new ActionResult { Action = "job.pause", Status = from, Reason = "guard" }).Status);
    }

    [Fact]
    public void The_rule_row_refuses_what_a_plugin_must_never_do()
    {
        // The same origin as plugin requests: a destructive or user-only action is refused by the policy itself.
        var policy = new OriginPolicy();
        var end = ActionCatalog.Find("proc.end") ?? ActionCatalog.All.First(d => d.Class == ActionClass.Destructive);
        var userOnly = ActionCatalog.Find("plugin.enable")!;
        var asPlugin = new ActionRequest { Action = "x", Origin = Origin.Rule, Rule = "plugin:focus" };

        Assert.Equal(PolicyVerdict.Refuse, policy.Check(end, asPlugin with { Action = end.Id }, false, false).Verdict);
        Assert.Equal(PolicyVerdict.Refuse, policy.Check(userOnly, asPlugin with { Action = userOnly.Id }, false, false).Verdict);
    }
}
