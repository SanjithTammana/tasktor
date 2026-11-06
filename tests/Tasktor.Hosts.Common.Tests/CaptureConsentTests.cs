using Tasktor.Hosts.Common.Deep;

namespace Tasktor.Hosts.Common.Tests;

/// <summary>C D-C22: capture's own consent screen.</summary>
public sealed class CaptureConsentTests
{
    [Fact]
    public void The_screen_states_the_never_recorded_list_storage_indicator_and_controls()
    {
        var text = string.Join(" ", CaptureConsent.Facts.Select(f => f.Heading + " " + f.Text));
        foreach (var must in new[] { "password", "secure desktop", "private windows", "other Windows users", "exclude", "7 days", "never uploaded", "red ring", "forget the last hour" })
        {
            Assert.Contains(must, text, StringComparison.Ordinal);
        }

        Assert.Equal(["Never recorded", "Stays on this PC", "Always visible", "Yours to stop"], CaptureConsent.Facts.Select(f => f.Heading));
    }

    [Fact]
    public void Nothing_is_sent_until_the_acknowledgement_is_ticked()
    {
        Assert.False(CaptureConsent.CanConfirm(false));
        Assert.Throws<InvalidOperationException>(() => CaptureConsent.Request(false));
        var r = CaptureConsent.Request(true);
        Assert.Equal(("module.enable", "capture", "capture", "true"), (r.Action, r.Target!["module"], r.Args!["screen"], r.Args["acknowledged"]));
    }
}
