# Default Pipeline GPU Submission Strategy TODO

Last Updated: 2026-05-08
Status: Implemented in progress
Owner: Codex
Target Branch: `rendering-gpu-submission-strategy`

Implementation note: `rendering/gpu-submission-strategy` could not be created on this checkout because Git could not create the nested branch ref directory. The implementation branch is `rendering-gpu-submission-strategy`.

## Goal

Make GPU-driven mesh rendering the production execution strategy inside `DefaultRenderPipeline`, without creating a separate full indirect render pipeline.

The desired architecture is:

- `DefaultRenderPipeline` remains the source of truth for pass ordering, render targets, post-processing, transparency, stereo, and UI.
- Mesh draw commands choose a clear submission strategy per pass.
- The production GPU strategy uses zero-readback indirect submission.
- CPU rendering and CPU readbacks remain available only for explicit diagnostics and unsupported hardware paths.

This TODO complements:

- `docs/work/todo/rendering/gpu/production-rendering-pipeline-roadmap.md`
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
    GpuIndirectInstrumented,
    GpuIndirectZeroReadback,
    GpuMeshletZeroReadback,
    GpuMeshletInstrumented,
}
```

The exact names can change, but the model should preserve these semantics. `GpuIndirectInstrumented` (previously "diagnostic") is the GPU indirect path that *allows* readbacks and CPU fallback for inspection and bring-up; "diagnostic" was overloaded with "for debugging" and is avoided.

### Strategy Contract

| Strategy | Purpose | CPU Readbacks | CPU Fallback | Hot-Path Allocations | Validation Logging |
|----------|---------|---------------|--------------|----------------------|--------------------|
| `CpuDirect` | Compatibility, simple debugging, unsupported GPU path | Allowed | N/A (already CPU) | Existing rules apply | Standard |
| `GpuIndirectInstrumented` | GPU path with explicit inspection and fallback | Allowed (counted in `GpuReadbackBytes`) | Allowed, warn-once | Forbidden | Verbose |
| `GpuIndirectZeroReadback` | Production GPU-driven indirect path | Forbidden | Forbidden in strict profiles; warn-once + downgrade in permissive | Forbidden | Errors only |
| `GpuMeshletInstrumented` | Meshlet/task-shader path with explicit inspection | Allowed only under diagnostics | Forbidden in production | Forbidden | Verbose |
| `GpuMeshletZeroReadback` | Production meshlet/task-shader path | Forbidden in production | Forbidden in production | Forbidden | Errors only |

Orthogonality note: "readback allowed" and "CPU fallback allowed" are conceptually independent. They are bundled per strategy here for simplicity; if more combinations are needed, split into orthogonal flags rather than adding more enum values.

`GPURenderDispatch` is being retired in favor of the resolver. During migration it acts as a compatibility shim mapping to `GpuIndirectInstrumented`. The end state replaces it with `PreferGpuMeshSubmission` (bool) consumed by the resolver, or removes it entirely once profile state drives the choice.

## Phase 0 - Branch Setup

- [x] Create a dedicated branch for this work.
- [x] Name suggestion: `rendering/gpu-submission-strategy`.
- [x] Confirm current baseline build status before edits.
- [x] Decide whether `DefaultRenderPipeline2` is in scope for this migration or is removed/parked first. Carrying both pipelines doubles the surface area of every Phase 3/4 change.
- [x] Decide the fate of `PathIntent` up front: keep alongside the strategy enum, or fold into it. Record the decision here.

Decision: `DefaultRenderPipeline2` is in scope and was updated alongside the canonical pipeline. `PathIntent` stays as a separate path selector for now; `EMeshSubmissionStrategy` owns CPU/GPU submission semantics, while `PathIntent` keeps the traditional-vs-meshlet routing intent explicit.

## Phase 0.5 - Strategy Contract Document

- [x] Lift the Strategy Contract table above into `docs/architecture/rendering/mesh-submission-strategies.md` as the canonical contract.
- [x] For each strategy, enumerate explicit must / must-not rules for: hot-path allocations, GPU-to-CPU readbacks, CPU fallback, validation logging level, and required render-graph metadata.
- [x] Cross-link from `docs/architecture/rendering/default-render-pipeline-notes.md` and `AGENTS.md` rendering section.
- [x] All later phases must cite this contract; behavior changes that contradict it require updating the contract first.

## Phase 1 - Audit Existing Submission Entrypoints

- [x] Audit all `VPRC_RenderMeshesPass` construction sites in `DefaultRenderPipeline`, `DefaultRenderPipeline2`, shadow/capture paths, and debug pipelines.
- [x] Identify passes that must remain CPU direct by design, such as `PreRender` and `PostRender` if they still host non-GPU-compatible commands.
- [x] Audit every call to `RenderCPU(... skipGpuCommands: true ...)` in GPU paths.
- [x] Audit fallback counters and warnings:
  - `GpuCpuFallbackEvents`
  - `ForbiddenGpuFallback`
  - zero-visible GPU pass warnings
- [x] Audit `PreRender` and `PostRender` mesh commands explicitly: list any GPU-incompatible operations they host, and either greenlight them for strategy migration or document specific blockers. Result feeds Phase 7.
- [x] Record current expected behavior in `docs/architecture/rendering/frame-lifecycle-and-dispatch-paths.md`.

## Phase 2 - Add Strategy Resolution

- [x] Add an engine-level resolver that maps settings/profile state to `EMeshSubmissionStrategy`.
- [ ] Inputs should include:
  - requested `GPURenderDispatch` (or its successor `PreferGpuMeshSubmission`)
  - `EnableZeroReadbackMaterialScatter`
  - `EnableGpuIndirectDebugLogging`
  - `EnableGpuIndirectValidationLogging`
  - `EnableGpuIndirectCpuFallback`
  - active `VulkanGpuDrivenProfile`
  - backend capability probes (see below)
  - optional `ForceMeshSubmissionStrategy` override (kill switch)
- [x] Define backend capability surface on the renderer abstraction:
  - `IRenderer.SupportsIndirectCountDraw`
  - `IRenderer.MeshShaderDialect`
  - `IRenderer.SupportsDirectMeshTaskDispatch`
  - `IRenderer.SupportsIndirectCountMeshTaskDispatch`
  - `IRenderer.SupportsMeshletDispatch` for production zero-readback meshlet dispatch only
  - Implementations for OpenGL, Vulkan, and DX12 backends; conservative defaults for unknown backends.
- [x] Add `ForceMeshSubmissionStrategy` setting / CVar that, when set, bypasses the resolver. Used for triage and not intended for production profiles.
- [x] Ensure `ShippingFast` resolves to `GpuIndirectZeroReadback` when GPU dispatch is requested and supported.
- [x] Ensure diagnostics profiles can resolve to `GpuIndirectInstrumented`.
- [x] Ensure unsupported hardware resolves to `CpuDirect` with a rate-limited warning.

Primary files:

- `XREngine/Engine/Subclasses/Rendering/Engine.Rendering.cs`
- `XREngine/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs`
- `XREngine/Engine/Subclasses/Engine.EffectiveSettings.cs`
- `XREngine.Runtime.Rendering/Runtime/RuntimeEngineFacade.cs`

## Phase 3 - Replace Boolean Mesh Dispatch in VPRC Commands

Phase 3 is a **pure refactor with no behavior change**. The compatibility shim must map `GPUDispatch == true` to whatever strategy preserves current runtime behavior (`GpuIndirectInstrumented`). Behavior changes belong in Phase 4. This is a hard acceptance gate for landing Phase 3.

- [x] Add a strategy property to `VPRC_RenderMeshesPassShared`.
- [x] Keep `GPUDispatch` temporarily as a compatibility shim that maps to `GpuIndirectInstrumented` (current behavior preserved).
- [x] Update `GpuProfilingName` to include the resolved strategy.
- [x] Update render graph metadata names to include the strategy where helpful.
- [x] Change `VPRC_RenderMeshesPassTraditional.Execute(...)` to switch on strategy instead of `GPUDispatch`.
- [x] Apply the `PathIntent` decision from Phase 0 (keep, fold, or remove).

Primary files:

- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/MeshRendering/Shared/VPRC_RenderMeshesPassShared.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/MeshRendering/Traditional/VPRC_RenderMeshesPassTraditional.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/MeshRendering/Meshlet/VPRC_RenderMeshesPassMeshlet.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderMeshesPass.cs`

## Phase 4 - Promote Zero-Readback Indirect to Production Strategy

- [x] Make `GpuIndirectZeroReadback` explicitly enable material-tier scatter for the pass.
- [x] Ensure the zero-readback path never requires `ReadGpuBatchRanges()`.
- [x] Ensure `UpdateVisibleCountersFromBuffer()` uses conservative capacity in zero-readback mode and does not call `ReadUIntAt(...)`.
- [x] Ensure transparent-domain classification avoids CPU count reads in zero-readback mode.
- [x] Ensure per-view draw count readback is diagnostics-only.
- [x] Ensure CPU safety-net fallback is impossible in strict production profiles.
- [x] Treat failure to prepare zero-readback scatter as a visible validation error, not a silent CPU fallback.

Primary files:

- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.Core.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.IndirectAndMaterials.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.CullingAndSoA.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.ViewSet.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/RenderCommandCollection.cs`
- `XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs`

## Phase 5 - Isolate Diagnostics and Fallbacks

- [x] Move CPU batch readback behind `GpuIndirectInstrumented` only.
- [x] Move count-buffer dumps behind `GpuIndirectInstrumented` only.
- [x] Move indirect command dumps behind `GpuIndirectInstrumented` only.
- [x] Keep warnings for skipped `ExcludeFromGpuIndirect` meshes in GPU strategies.
- [x] Define resolver behavior when a requested production GPU strategy cannot run:
  - Strict profiles (e.g. `ShippingFast`): refuse the production GPU strategy, emit a single structured warning, and resolve to `CpuDirect`.
  - Permissive profiles: warn-once and downgrade to `GpuIndirectInstrumented` (not silent CPU fallback).
  - Both paths emit one structured event including the resolver inputs that failed.
- [ ] Ensure instrumented readbacks increment `GpuReadbackBytes` so profiler output makes them obvious, and ensure zero-readback paths leave that counter at zero in steady state.

## Phase 6 - Render Graph and Profiler Clarity

- [ ] Add render graph metadata for GPU-driven preparation stages:
  - reset counters
  - cull
  - LOD selection
  - transparency classification
  - sort/build keys
  - material-tier scatter
  - indirect count draw submission
- [x] Make GPU profiler labels distinguish:
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

- [x] Update `DefaultRenderPipeline.CommandChain.cs` to request strategy-resolved mesh passes.
- [x] Apply the V2 decision from Phase 0: either update `DefaultRenderPipeline2.CommandChain.cs` the same way, or confirm V2 is parked/removed before this phase begins.
- [x] Apply the `PreRender`/`PostRender` audit result from Phase 1: either migrate them to strategy-resolved passes or document blockers and keep them CPU-only.
- [x] Make capture/cubemap/texture-array helper commands pass through the resolved strategy.
- [x] Verify shadow and GI capture passes do not accidentally force instrumented readbacks.

Primary files:

- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.CommandChain.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRCRenderTargetHelpers.cs`
- render-to-cubemap / render-to-texture-array commands

## Phase 8 - Tests and Validation

- [x] Add unit tests for strategy resolution.
- [x] Add tests that `ShippingFast + GPURenderDispatch` resolves to zero-readback when supported.
- [x] Add tests that instrumented profiles allow explicit readback/fallback paths.
- [x] Add tests that production GPU strategy suppresses CPU fallback (strict) or downgrades to instrumented (permissive) per Phase 5 policy.
- [ ] Add or update GPU pass tests that assert no hot-path `GpuReadbackBytes` in zero-readback mode.
- [ ] Add a positive test that `GpuIndirectInstrumented` produces non-zero `GpuReadbackBytes` when readbacks are enabled, so the counter cannot silently break.
- [ ] Run targeted unit tests for GPU indirect dispatch.
- [ ] Run a narrow editor/unit-testing world validation with GPU dispatch enabled.
- [ ] Stereo / XR validation: run the unit-testing world in stereo (and at least one OpenVR session) under `GpuIndirectZeroReadback`, asserting per-view counters and indirect dispatch behave correctly with no readback regressions.
- [ ] Performance gate: sample N frames of the unit-testing world under `GpuIndirectZeroReadback` and confirm frame time is at parity or better than the prior `GPURenderDispatch` path. Record numbers in the PR.
- [ ] Allocation gate: run `Report-NewAllocations` (and any equivalent readback-detection report) over the mesh submission paths; production strategy hot path must show no new allocations.
- [ ] Inspect `Build/Logs/.../profiler-gpu-pipeline-*.log` for strategy labels and unexpected readback markers.

Likely test areas:

- `XREngine.UnitTests/`
- existing GPU indirect render dispatch tests
- unit-testing world startup path
- stereo / OpenVR smoke path

## Phase 9 - Documentation

- [x] Update `docs/user-guide/rendering.md` to describe strategy resolution instead of a simple CPU/GPU toggle.
- [x] Update `docs/architecture/rendering/frame-lifecycle-and-dispatch-paths.md`.
- [x] Update `docs/architecture/rendering/default-render-pipeline-notes.md` to reference the strategy contract.
- [x] Update `AGENTS.md` rendering section to point at the strategy contract document.
- [x] Update `docs/work/todo/rendering/gpu/production-rendering-pipeline-roadmap.md` with the final strategy names and status.
- [x] Add migration notes for settings:
  - `GPURenderDispatch` (deprecation / replacement with `PreferGpuMeshSubmission`)
  - `ForceMeshSubmissionStrategy` (new kill switch)
  - `EnableZeroReadbackMaterialScatter`
  - GPU indirect debug/validation/fallback flags
  - `VulkanGpuDrivenProfile`

## Acceptance Criteria

- [ ] No separate production `IndirectRenderPipeline` exists.
- [ ] `DefaultRenderPipeline` remains the canonical production pipeline.
- [ ] Mesh passes use an explicit submission strategy instead of relying only on `GPUDispatch`.
- [ ] Phase 3 lands as a pure refactor with no observable behavior change (compatibility shim maps `GPUDispatch == true` to `GpuIndirectInstrumented`).
- [ ] Production GPU strategy uses zero-readback material-tier indirect submission.
- [ ] Production GPU strategy performs no `ReadUIntAt`, `MapBufferData`, `GetDataArrayRawAtIndex`, or equivalent GPU-to-CPU readback in the hot render path.
- [ ] Production GPU strategy hot path passes the `Report-NewAllocations` audit (no new per-frame allocations).
- [ ] Production GPU strategy frame time is at parity or better than the prior `GPURenderDispatch` path on the unit-testing world.
- [ ] Production GPU strategy validated under stereo and at least one OpenVR session.
- [ ] CPU fallback is only available through `GpuIndirectInstrumented` or unsupported-hardware resolution; strict profiles refuse the production GPU strategy instead of falling back.
- [x] `ForceMeshSubmissionStrategy` kill switch works and is documented.
- [ ] Profiler and logs identify the active mesh submission strategy.
- [x] Strategy contract document exists at `docs/architecture/rendering/mesh-submission-strategies.md` and is linked from canonical rendering docs.
- [ ] Targeted tests/build validation pass, or unrelated failures are documented.

## Final Task

- [ ] Merge the dedicated branch back into `main` after implementation, validation, and documentation updates are complete.
