# DDGI TODO

Last Updated: 2026-10-06
Status: Active (experimental feature)
Architecture: [DDGI guide](../../../../developer-guides/gi/ddgi.md), [Global Illumination Ownership](../../../../architecture/rendering/global-illumination-ownership.md)  Design: [DDGI Integration Plan](../../../design/global-illumination/ddgi-integration-plan.md)
Validation: [Global Illumination Validation](../../../testing/rendering/global-illumination-validation.md#ddgi)

## Current State

DDGI runs in `DefaultRenderPipeline` and `AdvancedRenderPipeline` through the `ScreenSpaceDiffuseOutput` host capability. `DDGIVolumeComponent`, `DDGIVolumeRuntimeState`, `GpuDdgiGeometryService`, and the `VPRC_DDGI*` passes implement raygen, GPU BVH trace, material-aware hit shading, relocation, atlas updates, border copy, screen sampling, composition, cascades, baked asset version 2, and interruption receipts with `DDGIInterruptionDiagnostics`. Both OpenGL pipelines pass most runtime checks. Vulkan/Advanced passes stereo and ownership; the Vulkan final-source authority and stereo composition defects are closed. The Vulkan/Advanced BRDF lookup stays zero. Evidence history: [DDGI working-copy verification](../../../investigations/rendering/2026-09-20-ddgi-working-copy-verification.md).

## Open Code Items

### Vulkan

- [ ] Fix the zero BRDF lookup on Vulkan/Advanced. The producer runs, the packed reflection array is nonzero, but every BRDF texel is zero. BRDF lookup producer pass in the Advanced pipeline and its Vulkan resource closure. Done when: the Vulkan BRDF producer writes the same lookup values as OpenGL, with no change to the OpenGL path.

### Diagnostics

- [ ] Add probe ray and hit-vector debug drawing. `VPRC_DDGIDebugVisualization.cs`, `DDGIProbeDebug.comp`. Done when: a debug mode draws the traced rays and hit points of selected probes from the live ray and hit buffers.

### Related Renderer Defects

- [ ] Stop the InfiniteGrid shaders from inverting already-reversed depth. `InfiniteGrid.fs`, `InfiniteGridStereo.fs`, `InfiniteGridMotionVectors.fs`, `InfiniteGridMotionVectorsStereo.fs`, `InfiniteGridReactiveMask.fs`, `InfiniteGridReactiveMaskStereo.fs`, and the embedded fallback shaders. Done when: each writes `gl_FragDepth = depth` with no `DepthMode` inversion.
- [ ] Review first-image storage readiness for mutable 2D and 3D OpenGL textures used by Landscape and MeshSDF. Done when: those textures allocate typed storage before the first image binding, as DDGI textures do.

### Tests (Owner Clearance Required)

- [ ] Refresh the eight obsolete DDGI contracts: old geometry buffer names, fixed four-cascade allocation, shader expressions without diffuse weighting, probe work above the configured cap, and incomplete baked payloads. `XREngine.UnitTests/Rendering/DdgiComputeIntegrationTests.cs` and related DDGI tests. Done when: the targeted DDGI suite passes without weaker runtime guards.
- [ ] Add focused tests for atlas indexing, cascade-local versus global probe indices, baked version 2 rejection of version 1, and the update-transaction history rule. Done when: each behavior has a deterministic test.

## Decisions Needed

- [ ] Set the DDGI support tier (experimental or supported) per backend and pipeline after the validation matrix passes. Owner: rendering lead.
- [ ] Decide on a per-cascade memory budget limit after the memory check in the validation doc. Owner: rendering lead.

## Out Of Scope

- Forward, transparent, and world-space DDGI consumers.
- ReSTIR secondary-bounce sampling of DDGI and shared glossy-RT ray dispatch. See [DDGI guide](../../../../developer-guides/gi/ddgi.md#hybrid-integrations--interoperability).
