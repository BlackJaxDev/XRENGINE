# XR Smoke Lifecycle Fault Seams

`XRE_OPENXR_SMOKE_LIFECYCLE_FAULT_STAGE` is an opt-in smoke-only control. It is rejected unless a smoke frame budget is configured and rejects unknown values.

- `RetiredGenerationCapacity` holds only real retired Vulkan OpenXR generations until the existing four-generation admission boundary. The fifth retirement is deferred before active-generation detachment; terminal draining releases the hold.
- `PostDetachReplacementFailure` fires once after real swapchain cleanup has detached the active generation and before replacement creation. It uses the existing `SessionStopping` recovery path.
- `LossPending` fires once only after accepted live OpenXR work still has an active or retiring Vulkan submission with native queue acceptance. Its receipt records the accepted-pending count and tracker source; reservations alone cannot trigger it. It invokes the existing loss state machine with a simulated-state-machine source and does not forge an OpenXR event.

`OpenXrSmokeSummary.LifecycleFault` records arming, consumption, trigger frame, lifecycle epoch, retirement observation, pending-work observation, terminal-hold release, and diagnostic text. Read-only diagnostics never trigger a scenario.

Use `request_openxr_session_exit`, wait for completed child retirement in `get_openxr_runtime_diagnostics`, then use `request_openxr_session_start` to exercise repeated sessions on the configured window. Startup is asynchronous and preserves explicit renderer-recreation-required failures. `restart_renderer(restart_openxr_session=true)` replaces the renderer and restarts presentation only when OpenXR was active at the replacement boundary.
