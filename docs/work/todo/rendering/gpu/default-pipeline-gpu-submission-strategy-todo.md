# Default Pipeline GPU Submission Strategy TODO

Last Updated: 2026-05-08
Status: Proposed

## Goal

Make GPU-driven mesh rendering the production execution strategy inside `DefaultRenderPipeline`, without creating a separate full indirect render pipeline.

The desired architecture is:

- `DefaultRenderPipeline` remains the source of truth for pass ordering, render targets, post-processing, transparency, stereo, and UI.
- Mesh draw commands choose a clear submission strategy per pass.
- The production GPU strategy uses zero-readback indirect submission.
- CPU rendering and CPU readbacks remain available only for explicit diagnostics and unsupported hardware paths.

This TODO complements:

- `docs/work/todo/rendering/gpu/gpu-rendering.md`
- `docs/work/design/rendering/zero-readback-gpu-driven-rendering-plan.md`
- `docs/work/design/rendering/default-render-pipeline-improvement-plan.md`

## Non-Goals

- Do not create a separate `IndirectRenderPipeline` that duplicates `DefaultRenderPipeline`.
- Do not remove the CPU renderer entirely.
- Do not require full bindless material unification before the zero-readback strategy becomes the default.
- Do not fold meshlet rendering into this first pass beyond reserving a clean strategy slot for it.

## Current Situation

`GPURenderDispatch` is currently the broad switch that routes mesh passes toward the GPU path. That is useful, but it is not precise enough to describe the actual execution model.

Important current behavior:

- `VPRC_RenderMeshesPassShared` routes by `GPUDispatch` plus `PathIntent`.
- `VPRC_RenderMeshesPassTraditional.RenderGPU(...)` still invokes CPU command traversal with `skipGpuCommands: true` before `RenderGPU(...)`.
- `GPURenderPassCollection` already has policy hooks for zero-readback material scatter.
- `HybridRenderingManager` already has `RenderZeroReadbackMaterialTiers(...)`.
- Diagnostic readbacks and batch readbacks still exist behind policy/debug flags.

The architectural cleanup is to make this explicit: the pipeline should select a mesh submission strategy, not a vague GPU/CPU boolean.

## Target Model

Introduce a first-class mesh submission strategy:

```csharp
public enum EMeshSubmissionStrategy
{
    CpuDirect,
    GpuIndirectDiagnostic,
    GpuIndirectZeroReadback,
    GpuMeshlet,
}
```

The exact names can change, but the model should preserve these semantics:

| Strategy | Purpose | CPU Readbacks |
|----------|---------|---------------|
| `CpuDirect` | Compatibility, simple debugging, unsupported GPU path | Allowed |
| `GpuIndirectDiagnostic` | GPU path with explicit inspection/fallback diagnostics | Explicit only |
| `GpuIndirectZeroReadback` | Production GPU-driven indirect path | Forbidden |
| `GpuMeshlet` | Future meshlet/task-shader path | Forbidden in production |

`GPURenderDispatch` should become the high-level preference for GPU mesh rendering. Runtime profile/settings then resolve that request to a concrete strategy.

## Phase 0 - Branch Setup

- [ ] Create a dedicated branch for this work.
- [ ] Name suggestion: `rendering/gpu-submission-strategy`.
- [ ] Confirm current baseline build status before edits.

## Phase 1 - Audit Existing Submission Entrypoints

- [ ] Audit all `VPRC_RenderMeshesPass` construction sites in `DefaultRenderPipeline`, `DefaultRenderPipeline2`, shadow/capture paths, and debug pipelines.
- [ ] Identify passes that must remain CPU direct by design, such as `PreRender` and `PostRender` if they still host non-GPU-compatible commands.
- [ ] Audit every call to `RenderCPU(... skipGpuCommands: true ...)` in GPU paths.
- [ ] Audit fallback counters and warnings:
  - `GpuCpuFallbackEvents`
  - `ForbiddenGpuFallback`
  - zero-visible GPU pass warnings
- [ ] Record current expected behavior in `docs/architecture/rendering/frame-lifecycle-and-dispatch-paths.md`.

## Phase 2 - Add Strategy Resolution

- [ ] Add an engine-level resolver that maps settings/profile state to `EMeshSubmissionStrategy`.
- [ ] Inputs should include:
  - requested `GPURenderDispatch`
  - `EnableZeroReadbackMaterialScatter`
  - `EnableGpuIndirectDebugLogging`
  - `EnableGpuIndirectValidationLogging`
  - `EnableGpuIndirectCpuFallback`
  - active `VulkanGpuDrivenProfile`
  - backend support for indirect-count draws
- [ ] Ensure `ShippingFast` resolves to `GpuIndirectZeroReadback` when GPU dispatch is requested and supported.
- [ ] Ensure diagnostics profiles can resolve to `GpuIndirectDiagnostic`.
- [ ] Ensure unsupported hardware resolves to `CpuDirect` with a rate-limited warning.

Primary files:

- `XREngine/Engine/Subclasses/Rendering/Engine.Rendering.cs`
- `XREngine/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs`
- `XREngine/Engine/Subclasses/Engine.EffectiveSettings.cs`
- `XREngine.Runtime.Rendering/Runtime/RuntimeEngineFacade.cs`

## Phase 3 - Replace Boolean Mesh Dispatch in VPRC Commands

- [ ] Add a strategy property to `VPRC_RenderMeshesPassShared`.
- [ ] Keep `GPUDispatch` temporarily as a compatibility shim that maps to a strategy.
- [ ] Update `GpuProfilingName` to include the resolved strategy.
- [ ] Update render graph metadata names to include the strategy where helpful.
- [ ] Change `VPRC_RenderMeshesPassTraditional.Execute(...)` to switch on strategy instead of `GPUDispatch`.
- [ ] Keep `PathIntent` or replace it with the strategy if meshlet selection becomes clean enough.

Primary files:

- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/MeshRendering/Shared/VPRC_RenderMeshesPassShared.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/MeshRendering/Traditional/VPRC_RenderMeshesPassTraditional.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/MeshRendering/Meshlet/VPRC_RenderMeshesPassMeshlet.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderMeshesPass.cs`

## Phase 4 - Promote Zero-Readback Indirect to Production Strategy

- [ ] Make `GpuIndirectZeroReadback` explicitly enable material-tier scatter for the pass.
- [ ] Ensure the zero-readback path never requires `ReadGpuBatchRanges()`.
- [ ] Ensure `UpdateVisibleCountersFromBuffer()` uses conservative capacity in zero-readback mode and does not call `ReadUIntAt(...)`.
- [ ] Ensure transparent-domain classification avoids CPU count reads in zero-readback mode.
- [ ] Ensure per-view draw count readback is diagnostics-only.
- [ ] Ensure CPU safety-net fallback is impossible in strict production profiles.
- [ ] Treat failure to prepare zero-readback scatter as a visible validation error, not a silent CPU fallback.

Primary files:

- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.Core.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.IndirectAndMaterials.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.CullingAndSoA.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.ViewSet.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/RenderCommandCollection.cs`
- `XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs`

## Phase 5 - Isolate Diagnostics and Fallbacks

- [ ] Move CPU batch readback behind an explicit diagnostic strategy or debug flag.
- [ ] Move count-buffer dumps behind explicit diagnostics.
- [ ] Move indirect command dumps behind explicit diagnostics.
- [ ] Keep warnings for skipped `ExcludeFromGpuIndirect` meshes in GPU strategies.
- [ ] Add a single structured warning when a requested production GPU strategy cannot run.
- [ ] Ensure diagnostic readbacks increment `GpuReadbackBytes` so profiler output makes them obvious.

## Phase 6 - Render Graph and Profiler Clarity

- [ ] Add render graph metadata for GPU-driven preparation stages:
  - reset counters
  - cull
  - LOD selection
  - transparency classification
  - sort/build keys
  - material-tier scatter
  - indirect count draw submission
- [ ] Make GPU profiler labels distinguish:
  - CPU direct mesh rendering
  - GPU indirect diagnostic rendering
  - GPU indirect zero-readback rendering
  - meshlet rendering
- [ ] Update `RenderPipelineGpuProfiler` grouping if needed so fixed GPU work is not mixed with diagnostic CPU readback time.

Primary files:

- `XREngine.Runtime.Rendering/Rendering/Pipelines/RenderPipelineGpuProfiler.cs`
- `XREngine.Runtime.Rendering/RenderGraph/`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/MeshRendering/`

## Phase 7 - Update Pipeline Construction

- [ ] Update `DefaultRenderPipeline.CommandChain.cs` to request strategy-resolved mesh passes.
- [ ] Update `DefaultRenderPipeline2.CommandChain.cs` the same way if V2 remains active.
- [ ] Keep `PreRender` and `PostRender` CPU-only until audited safe.
- [ ] Make capture/cubemap/texture-array helper commands pass through the resolved strategy.
- [ ] Verify shadow and GI capture passes do not accidentally force diagnostic readbacks.

Primary files:

- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.CommandChain.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRCRenderTargetHelpers.cs`
- render-to-cubemap / render-to-texture-array commands

## Phase 8 - Tests and Validation

- [ ] Add unit tests for strategy resolution.
- [ ] Add tests that `ShippingFast + GPURenderDispatch` resolves to zero-readback when supported.
- [ ] Add tests that diagnostics profiles allow explicit readback/fallback paths.
- [ ] Add tests that production GPU strategy suppresses CPU fallback.
- [ ] Add or update GPU pass tests that assert no hot-path `GpuReadbackBytes` in zero-readback mode.
- [ ] Run targeted unit tests for GPU indirect dispatch.
- [ ] Run a narrow editor/unit-testing world validation with GPU dispatch enabled.
- [ ] Inspect `Build/Logs/.../profiler-gpu-pipeline-*.log` for strategy labels and unexpected readback markers.

Likely test areas:

- `XREngine.UnitTests/`
- existing GPU indirect render dispatch tests
- unit-testing world startup path

## Phase 9 - Documentation

- [ ] Update `docs/api/rendering.md` to describe strategy resolution instead of a simple CPU/GPU toggle.
- [ ] Update `docs/architecture/rendering/frame-lifecycle-and-dispatch-paths.md`.
- [ ] Update `docs/work/todo/rendering/gpu/gpu-rendering.md` with the final strategy names and status.
- [ ] Add migration notes for settings:
  - `GPURenderDispatch`
  - `EnableZeroReadbackMaterialScatter`
  - GPU indirect debug/validation/fallback flags
  - `VulkanGpuDrivenProfile`

## Acceptance Criteria

- [ ] No separate production `IndirectRenderPipeline` exists.
- [ ] `DefaultRenderPipeline` remains the canonical production pipeline.
- [ ] Mesh passes use an explicit submission strategy instead of relying only on `GPUDispatch`.
- [ ] Production GPU strategy uses zero-readback material-tier indirect submission.
- [ ] Production GPU strategy performs no `ReadUIntAt`, `MapBufferData`, `GetDataArrayRawAtIndex`, or equivalent GPU-to-CPU readback in the hot render path.
- [ ] CPU fallback is only available through explicit diagnostics or unsupported-hardware resolution.
- [ ] Profiler and logs identify the active mesh submission strategy.
- [ ] Targeted tests/build validation pass, or unrelated failures are documented.

## Final Task

- [ ] Merge the dedicated branch back into `main` after implementation, validation, and documentation updates are complete.
