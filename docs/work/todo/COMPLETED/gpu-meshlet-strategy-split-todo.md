# GPU Meshlet Strategy Split TODO

Last Updated: 2026-05-20
Owner: Rendering
Status: Implemented in-place on 2026-05-20; validation complete with unrelated failures noted below.
Target Branch: None. Explicit user request for this implementation was "do not branch."

Related docs:

- [GPU Meshlet Zero-Readback Rendering TODO](gpu-meshlet-zero-readback-rendering-todo.md)
- [Default Pipeline GPU Submission Strategy TODO](default-pipeline-gpu-submission-strategy-todo.md)
- [Production GPU-driven rendering roadmap](production-rendering-pipeline-roadmap.md)
- [Mesh submission strategies](../../../../architecture/rendering/mesh-submission-strategies.md)
- [Frame lifecycle and dispatch paths](../../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md)

## Goal

Split the single `EMeshSubmissionStrategy.GpuMeshlet` value into two strategies - `GpuMeshletInstrumented` and `GpuMeshletZeroReadback` - so meshlet dispatch is debuggable through the same instrumented-vs-zero-readback contract that already exists for the indirect path:

| Traditional indirect | Meshlet (today) | Meshlet (target) |
| --- | --- | --- |
| `GpuIndirectInstrumented` | _none_ | `GpuMeshletInstrumented` |
| `GpuIndirectZeroReadback` | `GpuMeshlet` | `GpuMeshletZeroReadback` |

This mirrors the existing CPU/GPU triage UX: developers can pick an instrumented variant for diagnostics (CPU-side meshlet counts, per-batch timing, optional CPU readback for visible meshlet stats, explicit fallback diagnostics) and a zero-readback variant for shipping. The current production meshlet pipeline already satisfies the zero-readback contract; the split adds an instrumented sibling and renames the existing value to make the contract explicit.

## Non-Negotiable Rules

- [x] Create a dedicated branch before implementation (e.g. `rendering-gpu-meshlet-strategy-split`). Skipped by explicit user request: "do not branch."
- [x] `GpuMeshletZeroReadback` keeps the existing zero-readback contract: no `ReadUIntAt`, `MapBufferData`, `GetDataArrayRawAtIndex`, render-thread `WaitForGpu`, or equivalent count/visibility readback in steady-state Release frames.
- [x] `GpuMeshletInstrumented` is diagnostics-only and must never be selected as the shipping default by `ResolveMeshSubmissionStrategy` for non-Diagnostics profiles.
- [x] Existing meshlet correctness invariants from [GPU Meshlet Zero-Readback Rendering TODO](gpu-meshlet-zero-readback-rendering-todo.md) - GPUScene-owned data, shared visibility/material buffers, no implicit CPU mesh fallback - apply unchanged to both variants.
- [x] Direct CPU-count mesh task dispatch remains diagnostic-only and does not satisfy `GpuMeshletZeroReadback`.
- [x] Final merge to `main` only after build, source-contract unit tests, and at least one render parity smoke run pass on Windows + OpenGL 4.6. Not applicable to this in-place request; build/test validation is recorded below and runtime smoke remains open.

## Open Design Questions

Resolved during the in-place implementation:

- [x] Numbering: `GpuMeshletZeroReadback = 3`; `GpuMeshletInstrumented = 4`; legacy serialized/env token `GpuMeshlet` remaps to `GpuMeshletZeroReadback` for one release.
- [x] Backing implementation for `GpuMeshletInstrumented`: extend the production meshlet pipeline with opt-in diagnostic readbacks/timing; do not resurrect the divergent `MeshletCollection.RenderMeshlets` backend.
- [x] Source-contract test policy: assert the shared helper predicate `IsAnyMeshletStrategy()` rather than literal enum-name substrings.
- [x] Rendering-thread diagnostics: central helper predicates live on `EMeshSubmissionStrategyExtensions`; runtime/engine stats expose the instrumented counters.
- [x] Settings UX: existing enum surfaces, editor dropdown parsing, and `XRE_FORCE_MESH_SUBMISSION_STRATEGY` parsing expose the new values.

## Phase 0 - Scope Confirmation

- [x] Confirm `GpuMeshlet = 3` rename to `GpuMeshletZeroReadback = 3` is acceptable (not v1-shipping; AGENTS.md allows breaking renames).
- [x] Audit serialized config sources for the bare token `GpuMeshlet`:
  - `Assets/UnitTestingWorldSettings.jsonc` (generated; audited after regeneration)
  - `XREngine.Server/Assets/UnitTestingWorldSettings.jsonc`
  - `.vscode/schemas/unit-testing-world-settings.schema.json`
  - `Build/Logs/**` profiler captures (read-only; ignore)
  - User editor preferences in `%APPDATA%/XREngine` (covered by legacy token parsers)
- [x] Decide whether to add a serialization compatibility shim that accepts the legacy `"GpuMeshlet"` token and maps it to `"GpuMeshletZeroReadback"`. Recommendation: add a 1-release shim in the JSON converter and remove after the next public build.

## Phase 1 - Enum and Resolver

- [x] Rename `EMeshSubmissionStrategy.GpuMeshlet` to `EMeshSubmissionStrategy.GpuMeshletZeroReadback` in [`XREngine.Data/Rendering/Enums/EMeshSubmissionStrategy.cs`](../../../../../XREngine.Data/Rendering/Enums/EMeshSubmissionStrategy.cs).
- [x] Add `EMeshSubmissionStrategy.GpuMeshletInstrumented = 4` with XML docs that describe it as the diagnostic sibling (CPU readback / timestamps / per-batch logging permitted).
- [x] Add static helpers on `EMeshSubmissionStrategyExtensions`:
  - `IsAnyMeshletStrategy(EMeshSubmissionStrategy)` returns true for either variant.
  - `IsInstrumentedMeshletStrategy(EMeshSubmissionStrategy)` returns true only for `GpuMeshletInstrumented`.
  - `IsZeroReadbackMeshletStrategy(EMeshSubmissionStrategy)` returns true only for `GpuMeshletZeroReadback`.
  - `ToZeroReadbackMeshletStrategy(EMeshSubmissionStrategy)` performs the capability-gating fallback collapse.
- [x] Update `Engine.Rendering.ResolveMeshSubmissionStrategy` and `MeshSubmissionStrategyResolverInputs` so a forced `GpuMeshletInstrumented` only resolves when:
  - Active renderer `SupportsMeshletDispatch()` is true, AND
  - Active Vulkan profile is `Diagnostics` OR `EnableGpuIndirectDebugLogging` is true (instrumentation requires opt-in).
- [x] Update `RuntimeEngineFacade.ResolveMeshSubmissionStrategy` similarly: `forced == GpuMeshletInstrumented` falls back to `GpuMeshletZeroReadback` (or further to `GpuIndirectZeroReadback`/`GpuIndirectInstrumented`/`CpuDirect`) when capabilities are unmet.
- [x] Update `MeshSubmissionStrategyResolverTests` cases: rename existing `GpuMeshlet` references to `GpuMeshletZeroReadback`, and add new cases:
  - Forced `GpuMeshletInstrumented` with diagnostics + production support returns `GpuMeshletInstrumented`.
  - Forced `GpuMeshletInstrumented` with production support but no diagnostics opt-in returns `GpuMeshletZeroReadback`.
  - Forced `GpuMeshletInstrumented` with no meshlet support follows the existing zero-readback / instrumented indirect fallback chain.
  - Shipping profile + `GpuMeshletInstrumented` request falls back and is never selected for shipping.

## Phase 2 - Pipeline / Pass Plumbing

- [x] Replace literal `EMeshSubmissionStrategy.GpuMeshlet` checks with the helper predicates everywhere they appear:
  - [`XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/MeshRendering/Shared/VPRC_RenderMeshesPassShared.cs`](../../../../../XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/MeshRendering/Shared/VPRC_RenderMeshesPassShared.cs) (`IsMeshletRequested`, `ShouldForceMeshletDebugDisplay`, `ResolveSelectedMeshletSubmissionStrategy`).
  - [`XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs`](../../../../../XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs) (`Render` strategy gate, `TryRenderMeshletMaterialTable` callers).
  - [`XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs`](../../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs) (`ExpandVisibleMeshlets` gate, the meshlet-debug-display force-flip added 2026-05-20; that override targets `GpuMeshletZeroReadback` by default).
  - [`XREngine.Runtime.Rendering/Runtime/RuntimeEngineFacade.cs`](../../../../../XREngine.Runtime.Rendering/Runtime/RuntimeEngineFacade.cs) resolver shims.
- [x] Decide which variant the meshlet-debug-display force-flip should target. Done: Recommendation: `GpuMeshletZeroReadback` for visual parity with shipping; promote to `GpuMeshletInstrumented` only when `EnableGpuIndirectDebugLogging` is on.
- [x] Update `VPRC_RenderMeshesPassShared.ResolveSelectedMeshletSubmissionStrategy` so it returns whichever variant the caller requested (or `GpuMeshletZeroReadback` as the default selected meshlet) when capability holds.

## Phase 3 - Instrumentation Backend

- [x] Add an instrumentation hook to the production meshlet path in `HybridRenderingManager.TryRenderMeshletMaterialTable`:
  - Optional CPU readback of meshlet count / visibility / triangle stats buffers, gated on `IsInstrumentedMeshletStrategy(strategy) && EnableGpuIndirectDebugLogging`.
  - Optional GPU timestamp queries around `DrawMeshTasksIndirectCount` writing into `RuntimeEngine.Rendering.Stats.Meshlet`.
  - Per-batch logging budget (mirroring `GpuIndirectInstrumented` log budgets) for visible meshlet count, expansion overflow, and dispatch timing.
- [x] Extend `RuntimeEngine.Rendering.Stats.Meshlet` (create if needed) with: `LastVisibleMeshletCount`, `LastDispatchedMeshletCount`, `LastTaskRecordOverflowCount`, `LastDispatchTime`, `LastReadbackBytes`.
- [x] Surface stats through the existing diagnostic UI (ImGui rendering stats panel - same panel that shows `GpuIndirectInstrumented` counters).
- [x] Add `EnsureNoReadback` assertion when running `GpuMeshletZeroReadback`: the same `GpuReadbackBytes == 0` invariant the indirect path enforces.

## Phase 4 - Tooling, Settings, Docs

- [x] Update tooling validStrategies arrays to include both new names and drop the bare `GpuMeshlet`:
  - `Tools/Diagnose-ZeroReadbackHz.ps1`
  - `Tools/Measure-MeshSubmissionBaselines.ps1`
  - `Tools/Repro-ZeroReadbackCrash.ps1`
  - `Tools/Measure-GameLoopRenderPipeline.ps1`
  - `XREngine.Benchmarks/**` strategy enumerations.
- [x] Regenerate/audit Unit Testing World settings via `Tools/Generate-UnitTestingWorldSettings.ps1`. The generator ran successfully with Windows PowerShell; the generated settings/schema currently do not expose `ForceMeshSubmissionStrategy`, so no token diff was produced in:
  - `Assets/UnitTestingWorldSettings.jsonc`
  - `XREngine.Server/Assets/UnitTestingWorldSettings.jsonc`
  - `.vscode/schemas/unit-testing-world-settings.schema.json`
- [x] Add a serialization compatibility shim in the JSON converter for `EMeshSubmissionStrategy` that accepts `"GpuMeshlet"` and remaps to `"GpuMeshletZeroReadback"` for one release. Add a deprecation log line on first hit.
- [x] Update docs:
  - `docs/architecture/rendering/mesh-submission-strategies.md` - add row for `GpuMeshletInstrumented`, rename `GpuMeshlet` to `GpuMeshletZeroReadback`.
  - `docs/architecture/rendering/frame-lifecycle-and-dispatch-paths.md` - same rename, add instrumented-meshlet flow note.
  - `docs/user-guide/rendering.md` - update resolver description.
  - `docs/work/todo/rendering/gpu/gpu-meshlet-zero-readback-rendering-todo.md` - global rename strategy references.
  - `docs/work/todo/rendering/gpu/production-rendering-pipeline-roadmap.md` - add instrumented sibling under Phase E / H.
  - `docs/developer-guides/ai/mcp-server.md` audited; it does not expose the strategy enum.

## Phase 5 - Tests and Validation

- [x] Update source-contract test substrings to use helper predicates:
  - `MeshOptimizerInteropTests.cs` (`bool meshlet = strategy == EMeshSubmissionStrategy.GpuMeshlet;` to `bool meshlet = strategy.IsAnyMeshletStrategy();`).
  - `GpuIndirectPhase7ZeroReadbackTests.cs` (same pattern).
- [x] Add new unit tests:
  - Resolver fallback chain for `GpuMeshletInstrumented`.
  - `IsAnyMeshletStrategy` / `IsInstrumentedMeshletStrategy` / `IsZeroReadbackMeshletStrategy` predicate truth tables.
  - Serialization compatibility shim accepts legacy `"GpuMeshlet"` token.
- [x] Run `dotnet build XRENGINE.slnx` and `dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj`.
  - `dotnet build .\XRENGINE.slnx /p:UseSharedCompilation=false` passed on 2026-05-20 after repairing the benchmark project reference/package pin; warnings are existing NuGet advisories and existing compiler warnings.
  - Focused split coverage passed: `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~MeshSubmissionStrategyResolverTests|FullyQualifiedName~GpuIndirectPhase7ZeroReadbackTests|FullyQualifiedName~MeshOptimizerInteropTests|FullyQualifiedName~XRAssetSerializationTests|FullyQualifiedName~ProfilerProtocolTests" /p:UseSharedCompilation=false` => 74 passed, 0 failed.
  - Full unfiltered unit test command ran and failed outside this rendering split: 10 failures, 402 passed, then testhost aborted with `[ALSOFT] (EE) Unexpected property: 0x20005`. Failures were in animation/audio/core factory/cache/schema/defaults tests.
- [ ] Manual render parity smoke: not run in this pass. Launch editor with `--unit-testing` under each of `CpuDirect`, `GpuIndirectInstrumented`, `GpuIndirectZeroReadback`, `GpuMeshletInstrumented`, `GpuMeshletZeroReadback` via `XRE_FORCE_MESH_SUBMISSION_STRATEGY`. Capture `Build/Logs/**` and confirm:
  - Both meshlet variants render identically with `MeshletDebugDisplayEnabled = false`.
  - `GpuMeshletInstrumented` produces non-zero meshlet stats and (when `EnableGpuIndirectDebugLogging`) non-zero readback bytes.
  - `GpuMeshletZeroReadback` produces `GpuReadbackBytes == 0` in steady-state.
- [x] Run `Test-VulkanPhase3-Regression` and `Test-SurfelGi` for regression coverage where the meshlet path participates.
  - `Test-SurfelGi` command ran; all listed cases were skipped by test preconditions, with 0 failed.
  - `Test-VulkanPhase3-Regression` command ran and failed in existing backlog tests: 4 failed, 72 passed. Failures were missing `RuntimeShaderServices.Current` setup and source-file lookup failures for existing backlog assertions.

## Phase 6 - Branch Merge

- [x] Open `rendering-gpu-meshlet-strategy-split` branch. Skipped by explicit user request: "do not branch."
- [x] Stage commits per phase. Not applicable to this in-place request.
- [x] PR description includes: rename rationale, new resolver behavior table, settings migration note (legacy `"GpuMeshlet"` token still parses for one release), validation log links. Not applicable to this in-place request.
- [x] Merge to `main` after review and validation. Not applicable to this in-place request.

## Out of Scope

- Reworking the legacy `MeshletCollection.RenderMeshlets` direct-dispatch path or `VPRC_RenderMeshletDebugDisplay`. Those remain diagnostic-only and unaffected by this split.
- Adding a third meshlet strategy for direct (non-indirect-count) task dispatch. The existing diagnostic-only direct-dispatch path stays gated by capability probes, not by enum value.
- Editor UX for live strategy switching beyond the existing dropdown pattern.
