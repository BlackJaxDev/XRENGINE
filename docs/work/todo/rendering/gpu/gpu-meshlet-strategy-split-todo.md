# GPU Meshlet Strategy Split TODO

Last Updated: 2026-05-20
Owner: Rendering
Status: Not started. Scoping doc only — no branch yet.
Target Branch (proposed): `rendering-gpu-meshlet-strategy-split`

Related docs:

- [GPU Meshlet Zero-Readback Rendering TODO](gpu-meshlet-zero-readback-rendering-todo.md)
- [Default Pipeline GPU Submission Strategy TODO](default-pipeline-gpu-submission-strategy-todo.md)
- [Production GPU-driven rendering roadmap](production-rendering-pipeline-roadmap.md)
- [Mesh submission strategies](../../../../architecture/rendering/mesh-submission-strategies.md)
- [Frame lifecycle and dispatch paths](../../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md)

## Goal

Split the single `EMeshSubmissionStrategy.GpuMeshlet` value into two strategies — `GpuMeshletInstrumented` and `GpuMeshletZeroReadback` — so meshlet dispatch is debuggable through the same instrumented-vs-zero-readback contract that already exists for the indirect indirect path:

| Traditional indirect | Meshlet (today) | Meshlet (target) |
| --- | --- | --- |
| `GpuIndirectInstrumented` | _none_ | `GpuMeshletInstrumented` |
| `GpuIndirectZeroReadback` | `GpuMeshlet` | `GpuMeshletZeroReadback` |

This mirrors the existing CPU/GPU triage UX: developers can pick an instrumented variant for diagnostics (CPU-side meshlet counts, per-batch timing, optional CPU readback for visible meshlet stats, explicit fallback diagnostics) and a zero-readback variant for shipping. The current production meshlet pipeline already satisfies the zero-readback contract; the split adds an instrumented sibling and renames the existing value to make the contract explicit.

## Non-Negotiable Rules

- [ ] Create a dedicated branch before implementation (e.g. `rendering-gpu-meshlet-strategy-split`).
- [ ] `GpuMeshletZeroReadback` keeps the existing zero-readback contract: no `ReadUIntAt`, `MapBufferData`, `GetDataArrayRawAtIndex`, render-thread `WaitForGpu`, or equivalent count/visibility readback in steady-state Release frames.
- [ ] `GpuMeshletInstrumented` is diagnostics-only and must never be selected as the shipping default by `ResolveMeshSubmissionStrategy` for non-Diagnostics profiles.
- [ ] Existing meshlet correctness invariants from [GPU Meshlet Zero-Readback Rendering TODO](gpu-meshlet-zero-readback-rendering-todo.md) — GPUScene-owned data, shared visibility/material buffers, no implicit CPU mesh fallback — apply unchanged to both variants.
- [ ] Direct CPU-count mesh task dispatch remains diagnostic-only and does not satisfy `GpuMeshletZeroReadback`.
- [ ] Final merge to `main` only after build, source-contract unit tests, and at least one render parity smoke run pass on Windows + OpenGL 4.6.

## Open Design Questions

These should be resolved before opening the branch:

- [ ] Numbering: keep `GpuMeshlet = 3` as a renamed alias for `GpuMeshletZeroReadback`, or renumber? Recommendation: rename to `GpuMeshletZeroReadback = 3` and add `GpuMeshletInstrumented = 4`. Renumbering existing values risks breaking serialized settings, JSONC schemas, and capture artifacts.
- [ ] Backing implementation for `GpuMeshletInstrumented`: extend the production meshlet pipeline with an instrumentation toggle (per-frame CPU readback of meshlet visibility/count buffers, per-batch GPU timestamps), or wire the legacy `MeshletCollection.RenderMeshlets` direct-dispatch path used by `VPRC_RenderMeshletDebugDisplay` as the instrumented backend? Recommendation: extend the production path with opt-in CPU readback and timestamp queries — keeps shading parity with zero-readback, avoids resurrecting a divergent CPU-built meshlet path, and reuses GPUScene data.
- [ ] Source-contract test policy: should `MeshOptimizerInteropTests.PassCoreContract` and `GpuIndirectPhase7ZeroReadbackTests.PolicySnapshotContract` assert on both new strategy names, or be relaxed to a `meshlet = strategy is GpuMeshletInstrumented or GpuMeshletZeroReadback` substring? Recommendation: update tests to assert the helper predicate `IsAnyMeshletStrategy(strategy)` (added in Phase 1) rather than literal substring matches.
- [ ] Rendering-thread diagnostics: do we want a `IsInstrumentedMeshletStrategy(strategy)` predicate routed through `RuntimeEngineFacade` so passes can opt into instrumentation without checking the enum directly?
- [ ] Settings UX: surface new strategy as a Global Editor Preference dropdown, or only via `XRE_FORCE_MESH_SUBMISSION_STRATEGY` and `Diagnostics` Vulkan profile? Recommendation: Editor preference dropdown (matches `GpuIndirectInstrumented`).

## Phase 0 — Scope Confirmation

- [ ] Confirm `GpuMeshlet = 3` rename to `GpuMeshletZeroReadback = 3` is acceptable (not v1-shipping; AGENTS.md allows breaking renames).
- [ ] Audit serialized config sources for the bare token `GpuMeshlet`:
  - `Assets/UnitTestingWorldSettings.jsonc` (generated; will regenerate from settings type)
  - `XREngine.Server/Assets/UnitTestingWorldSettings.jsonc`
  - `.vscode/schemas/unit-testing-world-settings.schema.json`
  - `Build/Logs/**` profiler captures (read-only; ignore)
  - User editor preferences in `%APPDATA%/XREngine` (will need a one-time settings migration helper or a documented reset)
- [ ] Decide whether to add a serialization compatibility shim that accepts the legacy `"GpuMeshlet"` token and maps it to `"GpuMeshletZeroReadback"`. Recommendation: add a 1-release shim in the JSON converter and remove after the next public build.

## Phase 1 — Enum and Resolver

- [ ] Rename `EMeshSubmissionStrategy.GpuMeshlet` to `EMeshSubmissionStrategy.GpuMeshletZeroReadback` in [`XREngine.Data/Rendering/Enums/EMeshSubmissionStrategy.cs`](../../../../../XREngine.Data/Rendering/Enums/EMeshSubmissionStrategy.cs).
- [ ] Add `EMeshSubmissionStrategy.GpuMeshletInstrumented = 4` with XML docs that describe it as the diagnostic sibling (CPU readback / timestamps / per-batch logging permitted).
- [ ] Add static helpers (likely on `EMeshSubmissionStrategyExtensions` or on `RuntimeEngine.Rendering`):
  - `IsAnyMeshletStrategy(EMeshSubmissionStrategy)` → true for either variant.
  - `IsInstrumentedMeshletStrategy(EMeshSubmissionStrategy)` → true only for `GpuMeshletInstrumented`.
  - `IsZeroReadbackMeshletStrategy(EMeshSubmissionStrategy)` → true only for `GpuMeshletZeroReadback`.
  - `ToZeroReadbackMeshletStrategy(EMeshSubmissionStrategy)` → fallback collapse used by capability gating.
- [ ] Update `Engine.Rendering.ResolveMeshSubmissionStrategy` and `MeshSubmissionStrategyResolverInputs` so a forced `GpuMeshletInstrumented` only resolves when:
  - Active renderer `SupportsMeshletDispatch()` is true, AND
  - Active Vulkan profile is `Diagnostics` OR `EnableGpuIndirectDebugLogging` is true (instrumentation requires opt-in).
- [ ] Update `RuntimeEngineFacade.ResolveMeshSubmissionStrategy` similarly: `forced == GpuMeshletInstrumented` falls back to `GpuMeshletZeroReadback` (or further to `GpuIndirectZeroReadback`/`GpuIndirectInstrumented`/`CpuDirect`) when capabilities are unmet.
- [ ] Update `MeshSubmissionStrategyResolverTests` cases: rename existing `GpuMeshlet` references to `GpuMeshletZeroReadback`, and add new cases:
  - Forced `GpuMeshletInstrumented` with diagnostics + production support → `GpuMeshletInstrumented`.
  - Forced `GpuMeshletInstrumented` with production support but no diagnostics opt-in → `GpuMeshletZeroReadback`.
  - Forced `GpuMeshletInstrumented` with no meshlet support → existing zero-readback / instrumented indirect fallback chain.
  - Shipping profile + `GpuMeshletInstrumented` requested → falls back (never selected for shipping).

## Phase 2 — Pipeline / Pass Plumbing

- [ ] Replace literal `EMeshSubmissionStrategy.GpuMeshlet` checks with the helper predicates everywhere they appear:
  - [`XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/MeshRendering/Shared/VPRC_RenderMeshesPassShared.cs`](../../../../../XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/MeshRendering/Shared/VPRC_RenderMeshesPassShared.cs) (`IsMeshletRequested`, `ShouldForceMeshletDebugDisplay`, `ResolveSelectedMeshletSubmissionStrategy`).
  - [`XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs`](../../../../../XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs) (`Render` strategy gate, `TryRenderMeshletMaterialTable` callers).
  - [`XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs`](../../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs) (`ExpandVisibleMeshlets` gate, the meshlet-debug-display force-flip added 2026-05-20 — that override should target `GpuMeshletZeroReadback` by default).
  - [`XREngine.Runtime.Rendering/Runtime/RuntimeEngineFacade.cs`](../../../../../XREngine.Runtime.Rendering/Runtime/RuntimeEngineFacade.cs) resolver shims.
- [ ] Decide which variant the meshlet-debug-display force-flip should target. Recommendation: `GpuMeshletZeroReadback` for visual parity with shipping; promote to `GpuMeshletInstrumented` only when `EnableGpuIndirectDebugLogging` is on.
- [ ] Update `VPRC_RenderMeshesPassShared.ResolveSelectedMeshletSubmissionStrategy` so it returns whichever variant the caller requested (or `GpuMeshletZeroReadback` as the default selected meshlet) when capability holds.

## Phase 3 — Instrumentation Backend

- [ ] Add an instrumentation hook to the production meshlet path in `HybridRenderingManager.TryRenderMeshletMaterialTable`:
  - Optional CPU readback of meshlet count / visibility / triangle stats buffers, gated on `IsInstrumentedMeshletStrategy(strategy) && EnableGpuIndirectDebugLogging`.
  - Optional GPU timestamp queries around `DrawMeshTasksIndirectCount` writing into `RuntimeEngine.Rendering.Stats.Meshlet`.
  - Per-batch logging budget (mirroring `GpuIndirectInstrumented` log budgets) for visible meshlet count, expansion overflow, and dispatch timing.
- [ ] Extend `RuntimeEngine.Rendering.Stats.Meshlet` (create if needed) with: `LastVisibleMeshletCount`, `LastDispatchedMeshletCount`, `LastTaskRecordOverflowCount`, `LastDispatchTime`, `LastReadbackBytes`.
- [ ] Surface stats through the existing diagnostic UI (ImGui rendering stats panel — same panel that shows `GpuIndirectInstrumented` counters).
- [ ] Add `EnsureNoReadback` assertion when running `GpuMeshletZeroReadback`: the same `GpuReadbackBytes == 0` invariant the indirect path enforces.

## Phase 4 — Tooling, Settings, Docs

- [ ] Update tooling validStrategies arrays to include both new names and drop the bare `GpuMeshlet`:
  - `Tools/Diagnose-ZeroReadbackHz.ps1`
  - `Tools/Measure-MeshSubmissionBaselines.ps1`
  - `Tools/Repro-ZeroReadbackCrash.ps1`
  - `Tools/Measure-GameLoopRenderPipeline.ps1`
  - `XREngine.Benchmarks/**` strategy enumerations.
- [ ] Regenerate Unit Testing World settings via `Tools/Generate-UnitTestingWorldSettings.ps1` and confirm the new tokens land in:
  - `Assets/UnitTestingWorldSettings.jsonc`
  - `XREngine.Server/Assets/UnitTestingWorldSettings.jsonc`
  - `.vscode/schemas/unit-testing-world-settings.schema.json`
- [ ] Add a serialization compatibility shim in the JSON converter for `EMeshSubmissionStrategy` that accepts `"GpuMeshlet"` and remaps to `"GpuMeshletZeroReadback"` for one release. Add a deprecation log line on first hit.
- [ ] Update docs:
  - `docs/architecture/rendering/mesh-submission-strategies.md` — add row for `GpuMeshletInstrumented`, rename `GpuMeshlet` → `GpuMeshletZeroReadback`.
  - `docs/architecture/rendering/frame-lifecycle-and-dispatch-paths.md` — same rename, add instrumented-meshlet flow note.
  - `docs/api/rendering.md` — update resolver description.
  - `docs/work/todo/rendering/gpu/gpu-meshlet-zero-readback-rendering-todo.md` — global rename of `GpuMeshlet` references.
  - `docs/work/todo/rendering/gpu/production-rendering-pipeline-roadmap.md` — add instrumented sibling under Phase E / H.
  - `docs/features/mcp-server.md` if MCP exposes the strategy enum (audit).

## Phase 5 — Tests and Validation

- [ ] Update source-contract test substrings to use helper predicates:
  - `MeshOptimizerInteropTests.cs` (`bool meshlet = strategy == EMeshSubmissionStrategy.GpuMeshlet;` → `bool meshlet = strategy.IsAnyMeshletStrategy();`).
  - `GpuIndirectPhase7ZeroReadbackTests.cs` (same pattern).
- [ ] Add new unit tests:
  - Resolver fallback chain for `GpuMeshletInstrumented`.
  - `IsAnyMeshletStrategy` / `IsInstrumentedMeshletStrategy` / `IsZeroReadbackMeshletStrategy` predicate truth tables.
  - Serialization compatibility shim accepts legacy `"GpuMeshlet"` token.
- [ ] Run `dotnet build XRENGINE.slnx` and `dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj`.
- [ ] Manual render parity smoke: launch editor with `--unit-testing` under each of `CpuDirect`, `GpuIndirectInstrumented`, `GpuIndirectZeroReadback`, `GpuMeshletInstrumented`, `GpuMeshletZeroReadback` via `XRE_FORCE_MESH_SUBMISSION_STRATEGY`. Capture `Build/Logs/**` and confirm:
  - Both meshlet variants render identically with `MeshletDebugDisplayEnabled = false`.
  - `GpuMeshletInstrumented` produces non-zero meshlet stats and (when `EnableGpuIndirectDebugLogging`) non-zero readback bytes.
  - `GpuMeshletZeroReadback` produces `GpuReadbackBytes == 0` in steady-state.
- [ ] Run `Test-VulkanPhase3-Regression` and `Test-SurfelGi` for regression coverage where the meshlet path participates.

## Phase 6 — Branch Merge

- [ ] Open `rendering-gpu-meshlet-strategy-split` branch.
- [ ] Stage commits per phase.
- [ ] PR description includes: rename rationale, new resolver behavior table, settings migration note (legacy `"GpuMeshlet"` token still parses for one release), validation log links.
- [ ] Merge to `main` after review and validation.

## Out of Scope

- Reworking the legacy `MeshletCollection.RenderMeshlets` direct-dispatch path or `VPRC_RenderMeshletDebugDisplay`. Those remain diagnostic-only and unaffected by this split.
- Adding a third meshlet strategy for direct (non-indirect-count) task dispatch. The existing diagnostic-only direct-dispatch path stays gated by capability probes, not by enum value.
- Editor UX for live strategy switching beyond the existing dropdown pattern.
