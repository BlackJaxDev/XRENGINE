# CPU Async Query Camera Motion TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [CPU Query Async Occlusion](../../../developer-guides/rendering/cpu-query-async-occlusion.md)  Design: none  
Validation: [Render Queries And Occlusion Validation](../../testing/rendering/render-queries-and-occlusion-validation.md)

## Current State

`CpuQueryAsync` uses `CpuOcclusionCameraSnapshot`, `CpuOcclusionProjectionFootprint`, and `CpuOcclusionTemporalPolicy` to keep hardware-query evidence safe during camera motion. Vulkan and OpenGL CPU-direct paths attach camera and bounds evidence to pending and resolved query state. Live Vulkan evidence covers stationary, slow translation, slow rotation, and return-to-stable cases. Edge, cut, stereo, hierarchy, and some deterministic tests remain open.

## Open Code Items

### Temporal Policy Tests

- [ ] Verify NDC footprint tests against the engine projection conventions. `CpuOcclusionProjectionFootprint`, `CpuOcclusionTemporalPolicy`, and `CpuOcclusionTemporalPolicyTests`. Done when reversed-Z Vulkan and OpenGL clip-space variants have deterministic coverage.
- [ ] Add a test for exact issuing snapshot transfer from pending to resolved state. `CpuRenderOcclusionCoordinator` and `CpuRenderOcclusionCoordinatorTests`. Done when a resolved negative query proves that its camera snapshot and AABB match the submitted epoch.
- [ ] Add a test for a negative query that is already older than the Vulkan latency floor when it resolves. `CpuRenderOcclusionCoordinator` and `CpuRenderOcclusionCoordinatorTests`. Done when the coordinator keeps the result safe and bounded without a premature stale decision.
- [ ] Add a test for pending recovery queries that draw fail-visible when old proof fails reprojection. `CpuRenderOcclusionCoordinator` and `CpuRenderOcclusionCoordinatorTests`. Done when unsafe old proof cannot skip the recovery draw.
- [ ] Add object-bounds motion coverage with a stationary camera. `CpuOcclusionTemporalPolicy` and `CpuRenderOcclusionCoordinatorTests`. Done when moved bounds invalidate unsafe negative evidence.
- [ ] Add zero recovery budget and zero max-query configuration coverage. `CpuRenderOcclusionCoordinatorTests`. Done when the coordinator fails visible and does not divide by zero or keep stale pending state.
- [ ] Add cleanup coverage for query reset, overdue replacement, command-set invalidation, and camera cuts. `CpuRenderOcclusionCoordinator` and tests. Done when temporal fields are cleared or replaced with no stale ticket reuse.

### Stereo And Hierarchy Evidence

- [ ] Store physical-eye query-pose evidence for shared stereo coverage. `CpuRenderOcclusionCoordinator`, `CpuQueryOcclusionContracts`, and stereo query state. Done when each coverage bit can prove the camera snapshot that produced it.
- [ ] Validate hierarchy query reuse under moving cameras and moving child bounds. `CpuRenderOcclusionCoordinator` hierarchy state and tests. Done when dynamic child bounds rebuild or invalidate the current hierarchy union before evidence is reused.

### OpenGL Scheduling

- [ ] Confirm and harden OpenGL GPU-dispatch query scheduling for command counts above the query cap. `GPURenderPassCollection.Occlusion.cs`. Done when the path makes forward progress through ranked or rotating selection without starving later commands.

## Decisions Needed

- [ ] Decide if CPU async hardware queries stay a recommended CPU-direct mode, or if CPU software occlusion becomes the preferred CPU culling path after validation. Owner: Rendering.
- [ ] Decide if the CPU direct path adds a lightweight depth prepass for selected occluders, or uses only normal opaque depth plus deferred proxy queries. Owner: Rendering.
- [ ] Decide how much same-frame recovery to build on OpenGL before GPU Hi-Z or CPU software occlusion replaces it. Owner: Rendering.
- [ ] Select the CPU spatial structure that owns hierarchical query grouping. Owner: Rendering.
- [ ] Decide if CPU hardware-query occlusion is off by default for single-pass stereo until a stereo-safe query primitive is validated. Owner: Rendering / XR.
- [ ] Decide if OpenXR uses one stereo-pair query scope, or keeps per-eye states and combines them with OR for shared command buffers. Owner: Rendering / XR.
- [ ] Decide if NDC reprojection constants should become engine settings after live evidence. Owner: Rendering.

## Out Of Scope

- Live editor screenshots, RenderDoc capture, and hardware smoke checks. They are in the validation doc.
- GPU Hi-Z and CPU software occlusion implementation work.
- Broad render-thread performance disposition.
