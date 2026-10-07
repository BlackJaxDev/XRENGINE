# Vulkan Core Frame Loop, Resident Rendering, And High-Refresh TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Vulkan Renderer](../../../architecture/rendering/vulkan-renderer.md), [Vulkan Command Recording](../../../architecture/rendering/vulkan-command-recording.md), [Frame Loop Design](../../../architecture/rendering/frame-loop-design.md)  Design: [Vulkan Render Loop Target Architecture](../../design/rendering/vulkan-render-loop-target-architecture.md#high-refresh-program-contracts)
Validation: [Vulkan Core Validation](../../testing/rendering/vulkan-core-validation.md)

## Current State

The desktop frame loop runs through `VulkanFrameLoop` with sealed submissions (`SealedSubmissionContract`), the `VulkanFrameTelemetry` schema, and a generation gate (`CollectVisibleGenerationGate` in `EngineTimer`) that lets visibility collection overlap rendering. `EditorUiRateHz` exists in `EditorPreferences` and `AbstractRenderer.ShouldSkipImGuiFrame`. `OpenXrVulkanSubmissionTracker` owns OpenXR eye completion. The legacy `VulkanPreparedMeshOperationCohort` and `VulkanPreparedMeshIngress` paths, the original `DefaultRenderPipeline`, and the `EVulkanCpuStage` taxonomy still exist. No `DefaultRenderPipeline2` or `XRE_USE_PIPELINE_V2` code remains. Completed implementation history is in the [Vulkan core completion record](../../progress/rendering/vulkan-phases-0-5-completed.md). Promotion evidence is not recorded.

<a id="recovered-acceptance-contracts-and-specialized-child-ownership"></a>

Child trackers own their own code items:

| Tracker | Scope |
|---|---|
| [Core hardening](vulkan-core-hardening-and-device-loss-todo.md) | Tail work, observability, occlusion modes, source audits, facade reduction |
| [OpenXR and Advanced rendering](vulkan-xr-and-advanced-rendering-todo.md) | XR submission and lifecycle defects, Advanced resource and probe work |
| Modern backends in this file | Dynamic rendering, local read, descriptor heap, shader objects, foveation, transient memory, DGC, ray tracing |
| [Stall separate findings](vulkan-stall-separate-findings-todo.md) | Stall side findings and remaining issue backlog |
| [Wrapper parity](vulkan-wrapper-parity-todo.md), [Vulkan memory allocation](../../../architecture/rendering/vulkan-memory-allocation.md) | Backend parity and allocator work |
| [Fossilize integration design](../../design/rendering/vulkan-fossilize-integration-design.md) | Pipeline capture and replay tooling |
| [Pipeline resource lifecycle](render-pipeline-resource-lifecycle-todo.md), [CPU direct](optimization/cpu-direct-fast-path-todo.md), [material ladder](optimization/material-table-and-texture-binding-ladder-todo.md), [compact submission](optimization/compact-zero-readback-rendering-todo.md), [GPU roadmap](gpu/production-rendering-pipeline-roadmap.md) | Backend-neutral resource and submission work |
| [Editor observer cost](optimization/editor-profiler-ui-render-cost-todo.md), [VR performance](optimization/vr-rendering-performance-contract-todo.md) | Observer overhead and XR runtime contracts |

## Open Code Items

<a id="foundation-carryovers"></a><a id="f0-01"></a><a id="f0-02"></a><a id="f0-04"></a><a id="f0-05"></a>

### Frame Transactions And Present-Now

- [ ] F0-01: Define presentation freshness from accepted frame-data and resource generations plus a compatible new or reused command artifact generation. `VulkanFrameLoop.PresentNowReadiness`, `VulkanAcceptedFramePlan`. Done when: `PresentNow` does not force native scene re-encoding when the artifact is legal and its slot is protected, and `PresentedNew` requires a new submit serial.
- [ ] F0-02: Give every authored frame operation one attempt and accepted-plan transaction. Frame-operation authoring and `FramePlanBuilder`. Done when: retry, rejection, supersession, and terminal paths transfer, settle, or discard each operation exactly once.
- [ ] F0-04: Classify a required preparation ticket that becomes stale during work as `RetryFrame` or `Superseded`. Frame-loop readiness and terminal classification. Done when: a normal generation race cannot latch `RendererTerminal`.
- [ ] F0-05: Settle, clear, or defer one-shot query and callback requests when no submitted planner generation exists, including resize while rendering is paused. Done when: ordinary absence causes no exception loop.

<a id="f3-01"></a><a id="f3-02"></a><a id="f3-03"></a>

### Submission Strategies And Resident Bins

- [ ] F3-01: Promote CPU-built multi-draw indirect from diagnostic scaffolding to a production option for compatible opaque and masked bins. Start only after the isolation ladder in the validation doc proves the crossover. Done when: the strategy resolver can select it and it consumes the same resident template and bin backend as GPU indirect.
- [ ] F3-02: Make the GPU indirect and meshlet lanes fill the canonical bin and range streams without rebuilding the per-draw CPU frontend. Done when: those lanes read only canonical streams.
- [ ] F3-03: Select prepared direct, CPU-built MDI, GPU indirect-count, or mesh-task realization through a measured candidate, bin, material, and culling crossover policy. Done when: the policy is data-driven and reports its choice per bin.

### Telemetry And Profiler

- [ ] Show units on every profiler and HUD telemetry column and summary label (`Rate (Hz)`, `CPU (ms)`, `GPU (ms)`, bytes, Âµs). `XREngine.Editor` profiler panels, HUD overlays. Done when: no telemetry column header lacks a unit.
- [ ] Add a CPU profiler dump mode that selects the worst retained frame, and an option to wait for a minimum number of snapshots. `ProfilerDiagnosticDumps.DumpCpuFrameTimingHistory`, MCP `dump_cpu_frame_profile`. Done when: the MCP tool accepts the mode and writes the slowest retained frame.
- [ ] Make `XRE_VULKAN_MATERIAL_BINDING_DIAG` report the traditional CPU-direct deferred material path. `VkMeshRenderer.Descriptors.cs`, `VkMaterial`. Done when: the diagnostic logs `Texture0` descriptor resolution and `BaseColor` and opacity auto-uniform writes for a CPU-direct deferred draw.

### Rendering Correctness

- [ ] Audit bindless leakage into traditional CPU-direct Vulkan draws. `VkMeshRenderer`, `VkMaterial`, material-table program selection in `HybridRenderingManager`. Done when: CPU-direct draws use per-material descriptors and non-bindless shaders unless the draw opts into `BindlessMaterialTable`, and a source-contract test enforces this.
- [ ] Give auto exposure an explicit reduced-mip source. `ColorGradingSettings`, `AbstractRenderer.UpdateAutoExposureGpu`, render-graph mip generation. Done when: the render graph generates the mips that auto exposure reads, or auto exposure reports a visible fallback instead of sampling mip 0.
- [ ] Stop default-pipeline cache destruction and physical-resource rebuilds while the editor viewport and profile settle, and remove render-thread `vkDeviceWaitIdle` from `XRRenderPipelineInstance.DestroyCache` and resource-replacement paths. Done when: startup creates no repeated `DefaultRenderPipeline` instances for a profile transition, and retirement queues own destruction without a device wait.
- [ ] Prevent 1x1 or pre-layout render-resource generations from creating Vulkan images or FBOs for the default pipeline. Viewport resize publication and render-pipeline resource generation. Done when: no physical generation is allocated below the minimum viewport extent, and a unit test covers a 1x1 resize followed by the real size.

### Collect Visible

- [ ] Optimize collect-visible spatial tree traversal, command emission, per-camera filtering, and per-frame allocations. `EngineTimer.CollectVisibleThread`, `DispatchCollectVisible`. Start only after the clean collect-hot capture in the [render queries and occlusion validation doc](../../testing/rendering/render-queries-and-occlusion-validation.md#collect-visible-wait-decoupling) shows that collection, not `WaitForRender`, is the cost. Done when: the change targets the measured hot path and adds no per-frame allocations.

<a id="open-code-items-moved-from-dedicated-render-thread-window-ownership-todomd"></a>

### Window Ownership And Render Thread

Architecture: [Window Ownership And Render Thread](../../../architecture/rendering/window-creation-and-renderer-init.md#window-ownership-and-render-thread). Checks: [window and render-thread validation](../../testing/rendering/window-and-render-thread-validation.md#window-ownership-and-render-thread).

- [ ] Audit editor camera controls, selection, gizmos, drag-and-drop, clipboard, and common dialogs for window-thread and render-thread affinity under `XRE_WINDOW_PUMP_HOST=sdl-prototype`. `XREngine.Editor`, `XRWindow` wrappers. Done when: each path reads window state only through snapshots or mailbox calls, and `WindowOwnershipContractTests` guards the result.
- [ ] Add thread-affinity adapters for STA-sensitive editor services: file dialogs, clipboard, shell integration, drag-and-drop, and native UI bootstrap. Done when: each service runs through a documented adapter, and the process entry thread no longer needs STA for them.
- [ ] Make hidden utility windows either use the graphics-backed render host or stay pure OS utility windows. `XRWindow`, `RuntimeWindowPurpose`, `RuntimeWindowPumpHost`. Done when: no hidden window creates a graphics context or swapchain outside the render host.
- [ ] Audit helper and shared OpenGL contexts and primary-context restoration. `RuntimeEngine.Rendering.SecondaryContext` (`SecondaryGpuContext`). Done when: each shared context has one owner thread and explicit restoration, with a source-contract test.
- [ ] Add unit tests for `RuntimeRenderThreadHost` startup and shutdown in `CollapsedWindowRenderThread` and `SplitWindowPumpPrototype` modes (test code; needs owner clearance). Done when: tests cover `RenderThreadId` stamping, the startup attachment barrier, and shutdown mailbox flush.

### Interactive Resize

Architecture: [Interactive Resize](../../../architecture/rendering/window-creation-and-renderer-init.md#interactive-resize).

- [ ] Give `EInteractiveWindowResizeStrategy.EngineBorderlessResize` an engine-owned resize pump that does not enter the OS modal resize loop. `XRWindow`, `DesktopBorderlessResizeHook`, title-bar hit testing. Done when: dragging an engine-owned grip produces continuous native size updates and render updates through the normal resize path on OpenGL and Vulkan.

### Deferred And Probe Debug Dumps

Architecture: [Deferred And Probe Resource Rules](../../../architecture/rendering/vulkan-renderer.md#deferred-and-probe-resource-rules). Do these only if the [global illumination checks](../../testing/rendering/global-illumination-validation.md#probe-gi-on-vulkan-deferred) show that existing capture tools cannot isolate a failure.

- [ ] (Optional) Add a Vulkan debug dump for the GBuffer albedo and depth targets. `DefaultRenderPipeline`, `AdvancedRenderPipeline` debug views, or an MCP capture preset. Done when: one command writes both images for the active viewport.
- [ ] (Optional) Add a Vulkan debug dump for light-probe irradiance and prefilter textures. `LightProbeComponent.IBL.cs`, MCP capture tools. Done when: one command writes the contents for a selected probe.

### OpenGL Backfill

- [ ] Report OpenGL wrapper readiness apart from `IsGenerated`, with the same not-ready categories as Vulkan (buffer data, shader and program, material and texture bindings, render state, texture residency). `XREngine.Runtime.Rendering.OpenGL` wrappers. Done when: OpenGL readiness exposes those categories.
- [ ] Use the same pass-declared material layouts, layout hashes, dirty-row updates, and texture-binding rung diagnostics in the OpenGL material-table and bindless paths as in Vulkan. Done when: both backends report the same layout hash for one material.
- [ ] Report OpenGL shader and program warmup cache hits, misses, backend choice, and failure state in the same shape as the Vulkan pipeline miss summary. Done when: the profiler shows both with one schema.
- [ ] Enforce the no-readback strategy rule on OpenGL: `GpuIndirectZeroReadback` and `GpuMeshletZeroReadback` do not read count, visibility, or indirect buffers; only instrumented strategies do. Done when: a source-contract test fails on a readback call in the OpenGL zero-readback path.
- [ ] Expose OpenGL VR and multiview diagnostics like Vulkan: per-eye target identity, mirror status, readback bytes, dropped operations, material fallback, and program miss counters. Done when: the same fields exist for both backends.

### Vulkan Extension Backlog

Each item is capability-gated, uses the `XRE_VK_*` toggle convention, and fails visibly when explicitly requested and unsupported.

- [ ] Add memory residency through `VK_EXT_memory_budget` plus `VK_EXT_pageable_device_local_memory`. Device bootstrap and allocators. Done when: the extension is enabled when available and residency priority is set per allocation class.
- [ ] Use modern synchronization and frame-pacing extensions where available: synchronization2, timeline semaphores, `VK_KHR_present_wait`, `VK_KHR_present_id`, `VK_EXT_swapchain_maintenance1`, and calibrated timestamps. Device bootstrap, frame pacing, submission, and telemetry. Done when: each feature is queried, enabled when available, and reported in startup diagnostics and frame telemetry.
- [ ] Add attachment modernization: `VK_EXT_multisampled_render_to_single_sampled` and attachment feedback-loop layouts. Render-target plans. Done when: an eligible MSAA target renders without a separate resolve image, and feedback-loop layouts are selected where a pass reads and writes the same attachment.
- [ ] Add fragment shading rate attachment support to dynamic-rendering plans and command scopes. `RenderingFragmentShadingRateAttachmentInfoKHR`, render-target planning, command compatibility, and secondary execution contracts. Done when: attachment identity participates in compatibility and secondary-command validation.
- [ ] Add fragment density map attachment support to dynamic-rendering plans where supported, and keep the required legacy render-pass path where dynamic density-map attachment is unavailable. `RenderingFragmentDensityMapAttachmentInfoEXT`, render-target planning, and fallback selection. Done when: supported devices use the dynamic path and unsupported dynamic attachment cases fail or route to the legacy path explicitly.
- [ ] Add `VK_KHR_pipeline_binary` caching beside `VulkanPipelineCache`. Done when: pipeline binaries persist and reload with device and driver keying.
- [ ] Add cooperative matrix or vector support for in-engine ML passes. Done when: the capability is queried and one denoiser or upscaler path can use it behind a toggle.

### Frame-Loop Tests

Test code; each item needs owner clearance after its live validation. Existing policy tests are in `XREngine.UnitTests/Rendering/VulkanDesktopFrameLoopPolicyTests.cs`.

- [ ] Update the factory-contract tests that still require `RvcRenderPipeline` for every OpenXR eye, rename `EAdvancedStereoMode.RvcTwoPass`, and replace `AdvancedProductionCutoverContract.ProductionOpenXrPipelineName`. `AdvancedProductionCutoverContractTests`, `AdvancedStereoAndEditorIntegrationContractTests`, and render-pipeline factory tests. Done when: tests express the current OpenXR eye policy without hard-coded legacy names.
- [ ] Add policy tests for the OpenXR startup gates (accepted-attempt count, observed tick and completion timestamp, desktop activity, the 250 ms dirty quiet period and its two-second bypass, the pending-timeline bypass) and a concurrency test for the retirement exclusion between OpenXR retirement and a new desktop attempt. Done when: the tests fail if a gate or the exclusion is removed.
- [ ] Extend `PreflightClassification_IsDeterministic` to stable size, live mismatch, active interactive resize, unsettled resize, zero surface, missing resource generation, compatible interactive display mismatch, and DLSS mode change. Done when: each case asserts its pre-acquire disposition.
- [ ] Add tests for `VulkanDesktopAcquireAvailabilityTracker`: reset after acquire, interactive-resize preservation, and the recreate threshold. Done when: they pass without a Vulkan device.
- [ ] Add recording-stage tests: no overlay, ImGui only, dynamic text only, both overlays, recording deferral, scene-record failure, each overlay failure, and dirty-after-record. Add the fresh-primary rule (stale dirty flag cleared; dirty cached primary or generation change aborts). Done when: each case asserts one typed recovery obligation.
- [ ] Add fault tests: after an upload command buffer is recorded but before scene recording returns; at each recovery operation (abort begin and end, bridge submit, skipped present, swapchain recreate); the abort-layout matrix (`Undefined` to `PresentSrcKhr` for never-presented images); after device loss clears the timeline arrays; at `RenderSubmitStart`, `RenderSubmitEnd`, staging trim, `PresentStart`, and `PresentEnd`; and acquire device loss, unexpected acquire, image-preparation, post-submit, unexpected present, and post-present failures. Done when: each fault leaves every acquired resource with exactly one terminal transition and maps to its typed outcome.
- [ ] Add submit and present tests for success and each Vulkan error through fault injection, including first-error device-loss preservation and healthy submit failure as acquired-but-unconsumed ownership. Done when: each result maps to its outcome.
- [ ] Add tests for monotonic signal generation and the global, slot, and image publication sets of normal draw, abort-present, and consume-only bridge submits. Done when: each submit kind asserts its publication set.
- [ ] Extend `PolicyResults_AreReferenceFreeAndClassifiersAllocateNothing` to every desktop frame-loop stage method. Done when: a closure, boxing, LINQ, or context copy in a phase method fails a test.
- [ ] Add OpenXR coexistence tests: activity and drain exclusion, pending-slot `continue`, completed-other-slot drain, distinct eye and desktop slot domains, and accepted-attempt and readiness semantics; and ordering of the exclusive runtime graphics transition and session start against desktop activity. Done when: the tests pass without a Vulkan device and fail if a desktop attempt can enter during the transition.
- [ ] Keep a regression test that a batched OpenXR render failure after confirmed device loss does not fall through to sequential-eye rendering. Done when: the test fails without the guard.
- [ ] Add deterministic primary-reuse coverage that cycles desktop swapchain images while camera and view data change. Done when: plan generation, render-frame ID, frame-data image index, and recorded order match, and stale thread-local scratch is rejected.
- [ ] Add completion-domain tests: frame-slot timeline for in-flight resources, desktop image timeline for descriptor and frame-data image slots, external completion for OpenXR, and the strongest retired-image requirement on swapchain recreation. Done when: each domain is asserted.
- [ ] Add speculative pre-seal primary reuse tests with stable and changed dynamic UI. Done when: exact secondary reuse accepts an unsealed current operation, and a changed operation falls back to full sealing without stale data or duplicate metrics.
- [ ] Add `PresentNow` contract tests: results cannot be `Deferred` or report `PresentedNew` without matching submit serials and generations. Done when: compatible artifact reuse cannot claim stale output.
- [ ] Add scheduling capacity tests at 1, 8, 32, and production values; the 221-request and 836-request visibility shapes; lane overflows; the 344-attempt and 8,193-operation retry shape; a required upload generation change during preparation; and one-shot readback with no submitted generation and across resize. Done when: operations settle exactly once, stale tickets retry or supersede, and no exception loop occurs.
- [ ] Add source and contract tests that the primary and inline mesh encoders use immutable prepared state, not live `VkMeshRenderer.RecordDraw`, and validate sealed recording manifests against the sampled full tracker (retirement races, image-access deltas, descriptor expansion, render-pass replacement, secondary execution). Done when: both are enforced.
- [ ] Add fault injection for slow pipeline compiles, chunked uploads, staging overflow, shader compile failures, descriptor exhaustion, frame arena overflow, host and device OOM, device loss, timeline stalls, and diagnostic ring wrap, full, late, and mismatch completion. Prove that uploads larger than the staging ring complete by chunking. Done when: each fault has a deterministic test.
- [ ] Add CI-safe Vulkan and OpenXR smoke coverage without a physical headset, including capture work queued beside eye rendering. Done when: the smoke runs in CI.
- [ ] Add coverage for all mesh strategies that zero-readback lanes make no CPU visibility or count reads or CPU fallback. Done when: a forbidden read fails a test.
- [ ] Add exact CPU, shader, and native layout tests (`Unsafe.SizeOf<T>()`, `Marshal.OffsetOf<T>()`, reflection) for hot and native records, and source coverage that they are blittable without GC references or native `bool`. Done when: an unversioned field, size, or alignment change fails a test.
- [ ] Add architecture coverage for facade-to-owner dependency direction, one mutable authority per domain, no hot-path thread-static or ambient facade lookup, and no new planner, scheduler, lifetime tracker, descriptor model, queue gateway, stage taxonomy, or stateful `VulkanRenderer` partial without a design revision. Done when: a violating change fails a test.
- [ ] Add coverage for the lifecycle stage schema fields and for inclusive and exclusive calculation, cross-thread links, worker overlap, critical-path selection, wait classification, ring overflow, dropped spans, and export ordering. Done when: each rule has a deterministic test.
- [ ] Add Advanced contract coverage on OpenGL and Vulkan: classification record layout, exact-once pixel assignment, subgroup and fallback equivalence, indirect dispatch construction, overflow, required-mode failure; reconstructed surface, material, light, shadow, AO, decal, GI, and background contracts; late-pass eligibility, feedback rules, OIT lanes, transparent motion, fog, temporal masks, HDR, and upscalers; view-set layout, per-eye addressing, imported XR resources, capture profiles, async selection; and a source contract that forbids classic GBuffer, light combine, ordinary opaque Forward+, and same-frame readback in the Advanced path. Done when: each contract has a test.
- [ ] Reconcile the legacy broad source-contract aggregates with the split-file layout and the one-way architecture baselines. Done when: the broad Vulkan filter has no moved-file or obsolete-marker failures.

### Production Cutover And Legacy Deletion



- [ ] Mark the desktop `AdvancedRenderPipeline` source production-ready, extend the default to applicable offscreen profiles, and promote the RVC-owned OpenXR eye path after its XR gates pass. Update settings, schemas, editor defaults, launch profiles, and unit-testing-world setup. Done when: the defaults change in code and regenerated settings.
- [ ] Remove development pipeline selectors and temporary pipeline environment variables. Done when: one final pipeline-kind setting remains.
- [ ] Delete `VulkanPreparedMeshOperationCohort` and `VulkanPreparedMeshIngress`. Done when: neither type exists.
- [ ] Delete duplicate `GPUScene` and `HybridRenderingManager` arrays and ID maps. Done when: `AdvancedSharedGpuSceneDatabase` is the only owner.
- [ ] Delete separate command-chain workers and dedicated OpenXR eye threads after their render-domain migration. Done when: render work runs only on `EngineWorkScheduler` lanes.
- [ ] Delete the live object-oriented Vulkan CPU-direct encoding path after prepared direct and CPU-indirect parity and ordered-exception coverage pass. Done when: CPU-direct uses prepared records only.
- [ ] Delete per-command global bind-state and lifetime discovery after sealed manifest parity, sampled validation, and retirement-race gates pass. Done when: recording uses command-local state only.
- [ ] Delete the classic `DefaultRenderPipeline` and the deferred and forward resources, shaders, commands, and settings that become unreachable. If a named consumer blocks deletion, rename it to `LegacyDefaultRenderPipeline`, keep it opt-in, and record the owner, blocker, and dated deletion gate. Done when: the type is gone or renamed with that record.
- [ ] Remove obsolete diagnostic aliases, duplicate telemetry, and transitional fallbacks. Done when: each alias has one name.

## Decisions Needed

- [ ] Decide whether the split window pump moves from the SDL prototype to a raw Win32 pump, and whether split mode becomes a default. The SDL prototype is Windows-only and Vulkan-only. Owner: Rendering.
- [ ] Decide whether to resume barrier specialization. The tested early-visibility specialization improved frame and GPU medians only 2.95% and 1.27% (below the 5% gate) and raised frame p99 14.03%, so it was removed. Re-entry needs driver counter access, a measured overlap opportunity, full consumer coverage, and stable controls. Evidence: [wait and barrier investigation](../../investigations/rendering/vulkan14-phase-d-waits-and-barriers-2026-09-09.md), [barrier research](../../investigations/rendering/vulkan14-barrier-performance-research-2026-09-09.md). Broad `AllCommandsBit` barrier narrowing waits on this decision. Owner: Rendering.
- [ ] Decide if the editor allows backend fallback by default, or requires an explicit `AutoPreferRequested` or `FallbackWithWarning` setting. Owner: Rendering / Editor.
- [ ] Decide if editor diagnostics write directly into `RenderDiagnosticsFlags`, or if one diagnostics service owns the environment seed, the preference seed, and live toggles. Owner: Rendering / Editor.
- [ ] Decide if the old flat render-settings asset properties get compatibility shims, or are removed before v1. Owner: Rendering.
- [ ] Decide if DX12 settings placeholders are added now as an interface shape, or delayed until DX12 work resumes. Owner: Rendering.

## Out Of Scope

- Manual runtime, visual, hardware, profiler, benchmark, and soak checks. The validation docs own those checks.
- Backend wrapper parity details. The wrapper parity todo owns those code items.
