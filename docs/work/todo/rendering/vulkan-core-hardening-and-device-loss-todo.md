# Vulkan Core Hardening TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Vulkan Renderer: Device Loss And Fault Containment](../../../architecture/rendering/vulkan-renderer.md#device-loss-and-fault-containment), [Vulkan Resource Lifetime And Retirement](../../../architecture/rendering/vulkan-resource-lifetime-and-retirement.md), [Rendering Code Map](../../../architecture/rendering/code-map.md)  Design: [Vulkan Render Loop Target Architecture](../../design/rendering/vulkan-render-loop-target-architecture.md)
Validation: [Vulkan Core Validation](../../testing/rendering/vulkan-core-validation.md)

## Current State

Output scheduling, modal-resize stale presentation, persistent worker recording, and the Forward+ normal and depth reuse are implemented (see the [Forward+ investigation](../../investigations/rendering/vulkan-forward-plus-phase6-2026-08-25.md)). Device-fault reports (`VulkanDeviceContext.DeviceFaults`), memory-budget sampling (`VulkanFrameLoop.DeviceDiagnostics`), crash and submission breadcrumbs (`VulkanCrashBreadcrumb`, `VulkanFrameTelemetry`), and `VulkanFrameTelemetry` exist. The flat `EVulkanCpuStage` taxonomy still exists beside it. No TDR-risk signal and no interactive-resize watchdog exist. The Advanced pipeline classification, native shading, late-pass, stereo, and capture implementation is complete; its remaining checks are in the validation doc, and its remaining code items are in the [OpenXR and Advanced rendering todo](vulkan-xr-and-advanced-rendering-todo.md). Cutover and legacy deletion are in the [frame-loop master](vulkan-core-frame-loop-and-resident-rendering-master-todo.md#production-cutover-and-legacy-deletion).

## Open Code Items

### 7. Bound Shadow, Streaming, And Render-Thread Tail Work

- [ ] Define directional cascade invalidation from camera, light, caster, receiver, atlas, and quality state; stabilize projections and reuse unaffected cascade recording and data. Done when: an unchanged cascade is not re-recorded, and the invalidation reason is reported.
- [ ] Add a bounded per-frame directional-cascade update budget and an explicit temporal policy. Done when: the budget is a setting and over-budget cascades defer with a counter.
- [ ] Move texture decode, transcode, mip preparation, and upload planning off the render thread; batch transfer recording, sparse transitions, finalization, and descriptor publication. Done when: the render thread does none of this work.
- [ ] Publish immutable texture generations with narrow descriptor and command invalidation and bounded per-frame upload work. Done when: a texture replacement invalidates only its dependents.
- [ ] RC-A02: Inventory generic jobs, BVH work, physics preparation, capture preparation, and render-thread-affine increments; move pure work to its owning worker and split render-thread-affine work into budgeted increments with admission control. Done when: each work kind has an owning worker or admission budget, and each remaining unbounded render-thread task has its own item.

### 8. Observability And Runtime Diagnostics

- [ ] Publish explicit counters and state for device loss, frame and output status, reuse decisions and misses, queue and fence wait, worker wait, allocations, jobs, cascade invalidation, uploads, descriptor publication, GPU work, and deferred work through `VulkanFrameTelemetry`. Done when: each counter exists in the schema.
- [ ] Add a TDR-risk signal from submission size and GPU duration. Done when: telemetry reports it per frame.
- [ ] RC-A03: Audit the on-demand submit and descriptor dumps and context provenance. Record per-context planner ownership, display and internal extents, registry and resource generations, physical allocation, and every attempted cross-context substitution; promote incompatible context or extent reuse from a throttled warning to a structured frame-rejection reason. Done when: the dumps include these fields without steady-state formatting cost, and the rejection reason exists.
- [ ] Extend final-presentation diagnostics with the immutable source tuple, bound descriptor payload, selected primary and secondary artifacts, layout transitions, swapchain image, and submit generation. Done when: a stale view cannot pass as a valid `SourceTexture` binding in the ledger.
- [ ] Add an interactive-resize liveness watchdog with breadcrumbs for modal callback entry and exit, visibility publication, package selection, plan replacement, retirement backlog, waits, submission, and present. Done when: renderer hangs are reported apart from validation errors, device loss, managed exceptions, and native crashes.
- [ ] Replace the disconnected desktop lifecycle counters, the flat `EVulkanCpuStage` interpretation, and targeted CPU spans with the `VulkanFrameTelemetry` schema; keep adapters only until every dashboard, profiler, MCP tool, and benchmark uses it. Done when: `EVulkanCpuStage` is deleted.
- [ ] Define one stable coarse stage taxonomy (pacing and snapshot handoff, acquire, plan, resource preparation, scheduling, recording, submit, output completion, settlement) with detailed operation IDs nested below. Done when: the taxonomy is one enum used by all consumers.
- [ ] Correlate every aggregate or retained span with frame IDs, output and view-set IDs, frame slot, generation, stage and detail ID, span, parent, and cross-thread link IDs, thread or worker ID, timestamps, allocation, operation count and bytes, outcome, and wait, reuse, or invalidation reason. Done when: the span record carries these fields.
- [ ] Classify time as engine work, wait, native-driver call, external-runtime work, or intrusive diagnostics; keep queue-lock, fence and timeline, acquire, present, worker, collect, and retirement waits individually visible. Done when: no blocking call is unlabeled.
- [ ] Compute inclusive and exclusive time, aggregate worker CPU, wall span, overlap, imbalance, render-thread wait, critical path, and attributed and unattributed root time after capture without double counting. Done when: the post-capture analyzer produces these values.
- [ ] Keep aggregate mode allocation-free and low-contention with fixed per-thread and per-frame storage; keep targeted traces in prewarmed bounded rings and freeze before and after windows for slow frames. Done when: no measured thread serializes or aggregates.
- [ ] Emit an explicit `Unattributed` failure record for every detailed-capture gap of 50 µs or more. Done when: the record exists in the export.
- [ ] Publish the same IDs and results to the editor frame tree and timeline, runtime counters, MCP and component-profiler results, and JSON, CSV, and trace exports; defer string formatting to consumption. Done when: all outputs read one schema.

### 9. Make Occlusion Modes Bounded And Effective

- [ ] Separate occlusion candidates, occluders, tested bounds, rasterized triangles, queries, Hi-Z invocations, indirect commands, and actual culls in runtime telemetry. Done when: each count is a separate counter.
- [ ] Add representative open, moderate, occluder-heavy, masked, static, and deterministic moving-camera occlusion scenarios. Done when: each scenario is a loadable fixture.
- [ ] Bound CPU-software candidate selection, sorting, and rasterization; bypass cheaply when candidates, occluders, or prior benefit do not justify the work. Done when: the bypass condition is a measured threshold.
- [ ] Define CPU-query latency, refresh, stale-result, and camera-motion policy without CPU waits or current-frame result dependencies. Done when: the policy is code with no current-frame wait.
- [ ] Use persistent minimal-format GPU Hi-Z resources; bound pyramid, barriers, refinement, and count-copy work, consume visibility on the GPU, and bypass ineffective cases. Done when: Hi-Z resources persist across frames and the bypass exists.
- [ ] Define selection thresholds and hysteresis, keep a forced diagnostic mode, and mark each CPU-software, CPU-query, and GPU Hi-Z mode as production, opt-in, diagnostic-only, or retired. Done when: each mode has a disposition in code and settings.

### Source Structure And Unsafe Boundary

- [ ] Reduce `VulkanRenderer` to a small facade and composition root. The guardrail baseline allows 72 stateful `VulkanRenderer` partials and 548 legacy fields. Move that state into the subsystem owners (device, resource lifetime, render graph, command, descriptor, pipeline, desktop frame, OpenXR, ImGui). Done when: the baseline reaches the target facade budget and the guardrail test enforces the lower count.
- [ ] RC-A04: Produce the reproducible facade, lifecycle-spine, Vulkan source and line, directory-depth, file and method size, dependency-direction, and single-authority inventory against the target architecture budgets. Done when: each budget has a result and each failure has its own item.
- [ ] RC-A05: Audit retained unsafe native and mapped-memory owners for lifetime, bounds, alignment, and concurrency, and record why a safe span path is not enough. Done when: each retained unsafe path has a named owner and an evidence requirement.

## Decisions Needed

None.

## Recovered Items To Triage

The 2026-10-06 todo cleanup removed these items, and no match was found in other docs. Classify each item as code, check, decision, or done. Then move it to the correct doc or delete it.

### From `todo/rendering/vulkan-core-hardening-and-device-loss-todo.md`

- [ ] Classify water, hair, particles, trails, beams, portals, mirrors, and
  custom effects as native visibility, transparent, refractive, volumetric, or
  unsupported.
- [ ] Specialize the immutable section-2 `ViewSetPlan` with view count, layer
  mapping, current/previous matrices, jitter, render region, foveation region,
  and output target.
- [ ] Remove `DefaultRenderPipeline2` completely.
