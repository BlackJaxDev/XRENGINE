# Masked Software Occlusion Culling TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [CPU Software Occlusion](../../../architecture/rendering/cpu-software-occlusion.md)  Design: [Masked Software Occlusion Culling Design](../../design/rendering/masked-software-occlusion-culling-design.md)  
Validation: [Render Queries And Occlusion Validation](../../testing/rendering/render-queries-and-occlusion-validation.md)

## Current State

`CpuSoftwareOcclusionCuller`, `MaskedOcclusionBuffer`, `MaskedOcclusionRasterizer`, and `MaskedOcclusionAabbTester` implement the scalar CPU SOC path. The path is opt-in through `GpuOcclusionCullingMode=CpuSoftwareOcclusion`, the legacy settings toggle, or `XRE_CPU_SOC_OCCLUSION=1`. It supports opaque command selection, tile-mask AABB tests, stereo OR visibility, meshlet command visibility, telemetry, and targeted tests. Viewport overlay presentation, remaining selector tests, and measured SIMD specialization remain open.

## Open Code Items

### Selector Coverage

- [ ] Add command-selector tests for mesh rejection filters. `CpuSoftwareOcclusionCuller` and `MaskedSoftwareOcclusionCullingTests`. Done when non-triangle, empty, skinned, and blendshape meshes cannot become occluders.
- [ ] Add command-selector tests for instance rejection filters. `CpuSoftwareOcclusionCuller` and `MaskedSoftwareOcclusionCullingTests`. Done when multi-instance commands and unsafe snapshots remain conservative-visible.

### Debug Visualization

- [ ] Implement a viewport inset or overlay for `CpuSocDebugVisualization`. `CpuSoftwareOcclusionCuller`, `CpuSoftwareOcclusionDebugReadback`, and editor viewport UI. Done when the masked reciprocal-depth buffer and tile coverage can be inspected in the editor.
- [ ] Show SOC diagnostics next to the overlay. Editor occlusion UI and telemetry. Done when the overlay or adjacent panel shows active SOC resolution, selected occluder count, closed tiles, and force-visible state.

### SIMD And Hot Path

- [ ] Replace `CpuSocUseAvx2` with the shared `Auto | Scalar | Vector128 | Vector256` SIMD policy. Runtime settings and SOC implementation. Done when public settings no longer expose an ISA-specific toggle.
- [ ] Implement and measure a portable 128-bit tile-row rasterizer. `MaskedOcclusionRasterizer` and tests. Done when it matches scalar output and improves the owning stage or remains disabled.
- [ ] Retain a 256-bit raster path only if measurements justify it. `MaskedOcclusionRasterizer` and SIMD tests. Done when p50, p95, p99, bytes, vector iterations, tails, and hot-path allocations prove a frame benefit.
- [ ] Implement batched AABB testing only if measurements beat the current scalar and `System.Numerics` path. `MaskedOcclusionAabbTester` and tests. Done when scalar remains the oracle and retained vector widths have parity coverage.

### Disposition Follow-Up

- [ ] Apply the selected SOC disposition to settings docs, editor labels, troubleshooting notes, and release notes. Runtime settings, editor UI, and docs. Done when the retained mode is clearly described as production, opt-in, diagnostic-only, or retired.

## Decisions Needed

- [ ] Decide if SOC remains opt-in, becomes the preferred CPU-direct occlusion mode, stays diagnostic-only, or is retired. Owner: Rendering.
- [ ] Decide if SIMD specialization is worth retaining after live-path correctness and frame measurements. Owner: Rendering.
- [ ] Decide if non-meshlet GPU indirect needs a future compute-cull SSBO visibility mask before SOC can affect zero-readback strategies. Owner: Rendering.

## Out Of Scope

- Live Sponza captures, OpenVR captures, benchmark runs, and profiler evidence. They are in the validation doc.
- CPU async hardware query policy.
- GPU Hi-Z implementation.
