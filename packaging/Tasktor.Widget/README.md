# Optional Windows Widgets package (draft)

This optional project is outside the solution and does not change the portable release. `dotnet msbuild Tasktor.Widget.Package.proj -t:Build` builds the provider. The separate `PackWidget` target publishes the x64 provider and invokes the Windows SDK's `makeappx` to create an unsigned development MSIX; it neither signs nor installs the package. This path has not yet been validated with a real package or Windows Widgets host. The manifest uses a development-only identity and publisher, not a release identity.

Before `PackWidget`, supply `StoreLogo.png`, `Square44x44Logo.png`, `Square150x150Logo.png`, `TasktorIcon.png`, and accurate `SystemScreenshot.png`, `JobsScreenshot.png`, and `WorkflowScreenshot.png` in `ProviderAssets`, or set `WidgetAssets` to their directory. Supply correctly sized genuine assets; none are fabricated here. Set `MakeAppx` if the Windows SDK is installed elsewhere. Replace the development identity and publisher for a real release and arrange signing separately. `AppxManifest.xml` is the draft used by the project. Jobs and Workflow show aggregate sentences only; customization, deep links and actions are not implemented. Host testing requires a properly signed installed package; none has been installed or registered here.

Known lifecycle blockers: the COM server does not exit when released, and deactivation can race with an in-flight pull or host update. Do not treat this draft as production-ready; COM lifetime and deactivation synchronization require implementation and real-host verification.

Subscription follow-up: membership changes cancel stale pulls and wake the loop; a final check-to-host-update race remains. Eight synthetic tests cover activity and formatting, not real-host lifecycle.
