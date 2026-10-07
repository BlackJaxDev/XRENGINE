# Vulkan OpenXR And Advanced Rendering TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Advanced Render Pipeline](../../../architecture/rendering/advanced-render-pipeline.md), [Planar Mirror Capture](../../../architecture/rendering/planar-mirror-capture.md), [OpenXR VR Rendering](../../../architecture/rendering/openxr-vr-rendering.md)
Validation: [Vulkan Core Validation](../../testing/rendering/vulkan-core-validation.md#phase-6-openxr-checks), [Default and Advanced pipeline validation](../../testing/rendering/default-and-advanced-pipeline-validation.md)

## Current State

OpenXR submission ownership (`OpenXrVulkanSubmissionTracker`, `VulkanCommandRuntime.OpenXrSubmission`), swapchain lifecycle, and RVC eye output are implemented and pass bounded Monado cohorts. All Advanced implementation rows are done: canonical records, visibility and reconstruction, GPU classification, native shading, AO, GI, decals, late passes, temporal and post, stereo, independent output banks, mirror, probe, and standalone capture owners, and editor and MCP diagnostics. Lifecycle fault recovery and the mirror native compute-slot plan are not done. No `DefaultRenderPipeline2` or `XRE_USE_PIPELINE_V2` code remains. History and evidence are in the [Phase 6/7 investigation](../../investigations/rendering/vulkan-phase67-implementation.md) and the [mirror closeout](../../investigations/rendering/arp-mirror-placement-2026-09-14.md).

## Open Code Items

<a id="phase-6"></a>

### OpenXR Submission And Lifecycle

Entry points: `OpenXrVulkanSubmissionTracker`, `VulkanCommandRuntime.OpenXrSubmission`, `VulkanXrGraphicsBinding`, `OpenXRAPI.SwapchainLifecycle`, `Resolution`, `RuntimeStateMachine`.

- [ ] XR-I14: Keep a usable swapchain generation and resume pacing on pre-detachment deferral; separate post-detachment failure, keep parents until child retirement completes, and report per-session teardown epochs and XR retirement blockers. Done when: the lifecycle code paths exist and XR-V10 and XR-V11 can run.
- [ ] XR-I16: Serialize normal submission settlement against device-loss abandonment, keep the exact tracker during reentrant abandonment, and define authoritative abandonment for swapchain, input, and session parents. Done when: abandonment never reports normal completion, resets pending arenas, or claims a normal teardown.
- [ ] XR-I22: Reconcile mirror logical submission slots with native compute-slot requirements and exact keyed sealed-graph planning. Done when: mirror submissions with shape 5 have accepted native compute slot-3 resources.
- [ ] XR-I23: Carry the current submission revision through the desktop and XR queue ownership snapshot boundary. Done when: no stale queue ownership snapshot is used after a revision change.

<a id="phase-7"></a><a id="visibility-and-reconstruction-acceptance-carried-from-architecture-documents-0305"></a>

### Advanced Pipeline Resources

Visibility and reconstruction acceptance is in [Vulkan Core Validation](../../testing/rendering/vulkan-core-validation.md#visibility-and-reconstruction).

- [ ] Remove the classic GBuffer, deferred light accumulation, ordinary opaque Forward+, and `DeferredLightCombine` from the `AdvancedRenderPipeline` opaque path. Keep diagnostic reconstruction targets only behind an explicit capture or debug mode. `AdvancedRenderPipeline.FBOs.cs`, `AdvancedRenderPipeline.cs`, `AdvancedRenderPipeline.Resources.cs`. Done when: the production Advanced resource layout declares no GBuffer or light-combine resources, and a resource-layout unit test enforces it.
- [ ] Deduplicate the resource declaration catalogs behind shared helpers after `AdvancedRenderPipeline` feature parity is frozen. `DefaultRenderPipeline.Resources.cs`, `AdvancedRenderPipeline.Resources.cs`. Keep the separate command-chain structure of each pipeline. Done when: shared declarations exist once and both `DescribeResources(...)` paths use them.
- [ ] Replace mutable light-probe resource publication with an explicit scene-resource or import binding generation that both pipelines share. `DefaultRenderPipeline` probe resources, `AdvancedRenderPipeline.ProbeResources.cs`, `VPRC_SyncLightProbeResources`. Done when: light-combine passes bind probe buffers and arrays from one generation object, and neither pipeline keeps private probe publication state.

## Decisions Needed

None.
