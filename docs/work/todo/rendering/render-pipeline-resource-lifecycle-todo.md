# Render Pipeline Resource Lifecycle TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Render Pipeline Resource Lifecycle](../../../architecture/rendering/render-pipeline-resource-lifecycle.md), [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md), [Frame Lifecycle And Dispatch Paths](../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md), [Vulkan Renderer](../../../architecture/rendering/vulkan-renderer.md)
Validation: [Default And Advanced Pipeline Validation](../../testing/rendering/default-and-advanced-pipeline-validation.md)

## Current State

`XRRenderPipeline.DescribeResources(...)`, `XRRenderPipelineInstance`, `RenderResourceGeneration`, and backend generation transactions own declared pipeline resources. Source-contract tests show the removed `VPRC_CacheOrCreate*` command family is not script-registered and command trees must not author resource lifecycle mutation. Remaining work is generation-key hardening, direct active-registry mutation audit, Vulkan physical-plan commit safety, diagnostics, bounded retirement, imported-target correctness, and coverage tests.

## Open Code Items

### Generation And Resize Semantics

- [ ] Complete the audit for direct active-registry mutation from resize, settings, resource factories, feature setup, readback, and capture paths. Pipeline subclasses and `XRRenderPipelineInstance`. Done when: no frame-execution path mutates the active registry for pipeline-owned resources.
- [ ] Ensure internal size, output size, stereo layers, HDR, AA/MSAA, capture policy, and imported-target identity participate in the correct generation key without unrelated churn. Resource profile and generation key code. Done when: toggles create replacement generations only when layout changes.
- [ ] Finish pending Vulkan image, buffer, view, and framebuffer allocation without invalidating the active physical plan. Vulkan resource generation transaction code. Done when: a failed pending physical plan leaves the active plan usable.
- [ ] Validate logical generation and Vulkan physical-plan commit as one failure-safe transaction. `XRRenderPipelineInstance`, Vulkan backend transaction service. Done when: logical commit cannot publish resources whose backend plan failed.
- [ ] Add diagnostics for missing declared resources, stale descriptors, attachment-generation mismatch, imported-resource mismatch, and old generation lifetime after commit. Done when: each failure reports the resource name, generation, and owner.
- [ ] Remove routine `DeviceWaitIdle` from resize or recreation after fence-based retirement covers all old resources. Backend lifecycle code. Done when: resize uses fence-driven retirement during normal operation.
- [ ] Bound retired-generation and pending-generation buildup during rapid resize, feature toggling, capture, and device-loss recovery. Done when: queues have enforced limits and diagnostics.
- [ ] Ensure failed or superseded pending generations destroy all partial logical and backend resources without touching the active generation. Done when: disposal paths are covered by tests.
- [ ] Verify planner and readback scopes always select the generation and imported target used by the rendered frame. Done when: readback cannot sample a stale generation.

### Static Enforcement And Tests

- [ ] Add layout-coverage tests for every retained pipeline and feature profile with owned resources. `XREngine.UnitTests/Rendering/`. Done when: missing declarations fail tests.
- [ ] Add tests for atmosphere, fog, exact transparency, debug views, experimental GI, forward MSAA, bloom, AO, SMAA, FXAA, TSR, temporal history, motion blur, depth of field, and final presentation dependencies. Done when: each feature profile has declared resources.
- [ ] Add tests proving feature toggles change generation keys only when their resource layout changes. Done when: no unrelated churn appears in assertions.
- [ ] Add tests for camera and viewport overrides that affect resource profiles. Done when: overrides produce the expected generation key.
- [ ] Add tests for imported window, scene-capture, and XR targets and ownership boundaries. Done when: imported resources do not look pipeline-owned.
- [ ] Add tests for rapid resize coalescing, stale pending generations, failed materialization, failed backend allocation, atomic commit, and retirement. Done when: failure leaves the active generation renderable.
- [ ] Remove migration-era descriptor or layout parity diagnostics after compatibility authoring is gone. Done when: only current contracts remain.
- [ ] Add static tests that enumerate command trees and assert no resource allocation commands or active-registry mutations are reachable. Done when: allocation commands cannot return silently.
- [ ] Add an allocation audit for steady-state command generation, command execution, and resource-plan lookup. Done when: the audit reports no new hot-path allocations.

## Decisions Needed

- [ ] Decide whether any remaining dynamic branch-local resources require an imported-resource contract instead of pipeline ownership. Owner: rendering lead.

## Out Of Scope

- Rewriting mesh submission, pass ordering, or synchronization into a new frame graph beyond resource declaration.
- Moving ordinary render, dispatch, copy, resolve, readback, or presentation work out of command lists.
- Hiding failed GPU resource creation behind CPU fallbacks.
- Preserving obsolete pre-v1 pipeline APIs or serialized cache-command authoring.
