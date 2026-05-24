# Occlusion & Meshlet Execution — Phased Todo

Companion to [occlusion-regression-investigation.md](occlusion-regression-investigation.md). This doc captures the actionable plan from the May 2026 audit of GPU Hi-Z, CPU async-query occlusion, and meshlet dispatch.

## Status Snapshot

| Subsystem | Verdict | Notes |
| --- | --- | --- |
| GPU Hi-Z compute | Wired correctly; disabled on the only passes that need it | `IsCurrentGpuHiZDepthSelfOcclusionRisk` blocks HiZ on OpaqueForward/MaskedForward when `ForwardDepthPrePassEnabled` |
| CPU async-query occlusion (`CpuQueryAsync`) | Dead | `SubmitCpuOcclusionQueryBatch` is a documented no-op |
| Meshlet dispatch on OpenGL | Wired but inert | `GL_EXT_mesh_shader` rarely exposed; resolver silently downgrades to `GpuIndirectZeroReadback` |
| Meshlet dispatch on Vulkan | Wired | `vkCmdDrawMeshTasksIndirectCountEXT` path implemented |
| `CpuDirect` / `GpuIndirectInstrumented` / `GpuIndirectZeroReadback` | Working | Three distinct, exercised paths |
| `UseDepthNormalMaterialVariants` gate at occlusion entry | Correct by design | Only pushed inside `VPRC_ForwardDepthNormalPrePass` |

## Source-of-Truth References

- Occlusion entry / Hi-Z / CpuQueryAsync / pyramid build: [XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Occlusion.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Occlusion.cs)
- HiZ shader program load points: [XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.ShadersAndInit.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.ShadersAndInit.cs#L202-L204)
- HiZ compute kernels: `Build/CommonAssets/Shaders/Compute/Occlusion/{GPURenderHiZInit,HiZGen,GPURenderOcclusionHiZ}.comp`
- Meshlet dispatch call site: [XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs](../../../../XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs#L2459)
- OpenGL meshlet capability gates: [XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/OpenGLRenderer.Meshlets.cs](../../../../XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/OpenGLRenderer.Meshlets.cs)
- Vulkan meshlet path: [XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanRenderer.Meshlets.cs](../../../../XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanRenderer.Meshlets.cs)
- Strategy resolver: [XREngine.Runtime.Rendering/Runtime/RuntimeEngineFacade.cs](../../../../XREngine.Runtime.Rendering/Runtime/RuntimeEngineFacade.cs#L274)
- Depth-normal prepass scope (sole `UseDepthNormalMaterialVariants` push site): [XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_ForwardDepthNormalPrePass.cs](../../../../XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_ForwardDepthNormalPrePass.cs#L41)

## Branch & Merge Plan

- [ ] Phase 0 task: create branch `feature/occlusion-meshlet-fixes` for this todo and land all phases there.
- [ ] Final task (after all phases verified): merge `feature/occlusion-meshlet-fixes` into `main`.

---

## Phase 0 — Diagnostic Baseline (no code changes)

Goal: prove the audit verdicts on the user's hardware before changing anything. Output is a short capture stored alongside this doc under `docs/work/todo/rendering/assets/occlusion-meshlet-baseline-<date>/`.

- [ ] Run `Start-Editor-NoDebug` with `XRE_FORCE_MESH_SUBMISSION_STRATEGY=GpuMeshletInstrumented` and the unit-testing world. Capture `Build/Logs/.../profiler-main-thread-invokes.log` and look for `Meshlet.BackendSelected` lines.
- [ ] Confirm `RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletProductionFrame` is zero in the same session (panel or stats dump).
- [ ] Run with `EOcclusionCullingMode.GpuHiZ` + `ForwardDepthPrePassEnabled=true` and confirm HiZ skip telemetry (currently silent) — verify nothing fires by checking that `_occlusionCandidatesTested` stays 0 on `OpaqueForward`/`MaskedForward`.
- [ ] Run with `EOcclusionCullingMode.CpuQueryAsync` and confirm the one-time "scaffolded but NOT functional" warning fires and that `_cpuOcclusionPending` stays empty across frames.
- [ ] Record current `OpenGLRenderer.MeshShaderDialect` value at startup (one-line `Debug.Out` is acceptable scaffolding; remove before merge).

Exit criteria: baseline notes committed to `docs/work/todo/rendering/assets/...` documenting the three confirmed defects.

---

## Phase 1 — GPU Hi-Z: Remove the Self-Occlusion Gate

Goal: HiZ runs on `OpaqueForward` and `MaskedForward` after the depth prepass has written conservative depth.

- [ ] Replace `IsCurrentGpuHiZDepthSelfOcclusionRisk` semantics. The current check disables HiZ whenever a prepass is *enabled*; the correct check is whether the prepass actually produced the depth the resolver will read this frame.
  - Option A (simplest): delete the gate entirely. The depth prepass completes before opaque-forward; depth is final and conservative.
  - Option B (defensive): replace with `pipeline.RenderState.DepthPrePassRanThisFrame` flag set by `VPRC_ForwardDepthNormalPrePass` after it pops `UseDepthNormalMaterialVariants`.
- [ ] When HiZ is intentionally skipped (e.g., no depth available, stereo array view rejected, missing programs), call `RecordOcclusionFrameStats(candidates, 0, 0, 0)` and add an `OcclusionTelemetry.RecordHiZSkipped(reason)` so the editor panel shows a non-zero "skipped" bucket instead of silently passing all candidates.
- [ ] Add stereo `XRTexture2DArrayView` support in `TryResolveHiZDepthSource`: either generate a per-eye HiZ chain or add a `sampler2DArray` variant of the HiZ shaders. Until implemented, log a single throttled warning rather than silently disabling HiZ.
- [ ] Audit the history-depth fallback (`TryResolveHiZDepthSource` lines ~758-782). Either keep it with a clearly logged "prev-frame fallback" telemetry bucket or gate it behind a debug pref `XRE_HIZ_ALLOW_PREVFRAME_FALLBACK`.

Validation:
- [ ] With `GpuHiZ` + `GpuIndirectInstrumented`, `_occlusionCandidatesTested` and `_occlusionCandidatesCulled` show non-zero values on `OpaqueForward` and `MaskedForward`.
- [ ] Sponza camera-inside-mesh sanity test: walking the camera through a wall culls meshes outside the frustum *and* occluded large meshes. Capture screenshot.
- [ ] No regressions in `Test-VulkanPhase3-Regression` or `Measurement-P3-ZeroReadback-Census`.

---

## Phase 2 — CpuQueryAsync: Implement or Remove

Two acceptable outcomes. Pick one and execute fully; do not leave partial scaffolding.

### Option A — Implement (preferred if we want the mode at all)

- [ ] Implement `SubmitCpuOcclusionQueryBatch` (Occlusion.cs ~L1145):
  - For each candidate (capped at `CpuOcclusionMaxQueriesPerFrame`, LRU on `RenderCommandKey`):
    1. `glBeginQuery(GL_ANY_SAMPLES_PASSED_CONSERVATIVE, queryId)`
    2. Draw proxy AABB using cached unit-cube VBO + per-instance bounds transform; disable color writes (`glColorMask(0,0,0,0)`) and depth writes (`glDepthMask(false)`) but keep depth test against the current frame's prepass depth.
    3. `glEndQuery(GL_ANY_SAMPLES_PASSED_CONSERVATIVE)`.
  - Push (`candidateKey`, `queryId`, `submissionFrame`) into `_cpuOcclusionPending`.
- [ ] Implement `ResolveCpuOcclusionQueryResults` to poll N-frames-old queries with `GL_QUERY_RESULT_AVAILABLE` (never block); on result, write `(key, visible)` into `_cpuOcclusionLastResolved`.
- [ ] Ensure `ApplyTemporalCpuOcclusionFilter` already consumes `_cpuOcclusionLastResolved` (it does) — verify candidate keys actually match.
- [ ] Add a `CpuOcclusionTelemetry` bucket: submitted-this-frame, resolved-this-frame, culled, max-latency-frames.
- [ ] Replace the one-time `"scaffolded but NOT functional"` warning with a per-frame stats update.

### Option B — Remove

- [ ] Remove `EOcclusionCullingMode.CpuQueryAsync` from the enum.
- [ ] Migrate any preference, settings JSONC, or default-value referencing the mode to `EOcclusionCullingMode.Disabled` with a one-time migration log.
- [ ] Delete `SubmitCpuOcclusionQueryBatch`, `ResolveCpuOcclusionQueryResults`, `ApplyTemporalCpuOcclusionFilter`, `_cpuOcclusionPending`, `_cpuOcclusionLastResolved`, and `AsyncOcclusionQueryManager` allocation at Occlusion.cs L137.
- [ ] Update `Assets/UnitTestingWorldSettings.jsonc` schema and the generator at `Tools/Generate-UnitTestingWorldSettings.ps1` if the enum value is referenced.

Validation:
- [ ] If A: with `GpuIndirectInstrumented` + `CpuQueryAsync`, observe non-zero `submitted`/`resolved`/`culled` telemetry across at least 5 frames after a camera move.
- [ ] If B: `dotnet build XRENGINE.slnx` clean; no references remain (grep for `CpuQueryAsync`).

---

## Phase 3 — Meshlet Resolver Honesty

Goal: when a `GpuMeshlet*` strategy is requested but cannot run, the user knows.

- [ ] In `RuntimeEngineFacade.ResolveMeshSubmissionStrategy` (around L361/L389), when a meshlet strategy is requested but `supportsMeshletDispatch` is false:
  - Emit a single warning per process (or throttled to once per N seconds) including the renderer's `MeshShaderDialect` and `MeshletDispatchUnsupportedReason`.
  - Surface the downgraded-to strategy explicitly: `"Meshlet requested but downgraded to <X> because <reason>"`.
- [ ] Add a render stats counter `MeshletResolverDowngrades` so the editor stats panel exposes this without log-diving.
- [ ] At startup, log `MeshShaderDialect` (or "None") and whether `glMultiDrawMeshTasksIndirectCountEXT` was resolved, exactly once.
- [ ] Decision point (documented in this todo before implementation): do we ship a `GL_NV_mesh_shader` indirect-count code path for legacy NVIDIA hardware in v1, or accept that OpenGL meshlets require `GL_EXT_mesh_shader`?
  - If yes: add a parallel NV indirect-count delegate load in `OpenGLRenderer.Meshlets.cs` and lift `SupportsIndirectCountMeshTaskDispatch` to accept `OpenGLNV` once an NV indirect-count proc is wired.
  - If no: change `MeshletDispatchUnsupportedReason` for `OpenGLNV` to be louder ("OpenGL NV mesh shaders are diagnostic-only by design; use Vulkan or update drivers to expose GL_EXT_mesh_shader").

Validation:
- [ ] `XRE_FORCE_MESH_SUBMISSION_STRATEGY=GpuMeshletInstrumented` on the test machine logs the downgrade exactly once with the dialect + reason.
- [ ] On a Vulkan run (if hardware available), `GpuMeshletProductionFrame` counter increments and the warning does not fire.

---

## Phase 4 — Hi-Z Dirty-Bypass Buffer Hazard

Goal: GpuIndirectZeroReadback sees the same buffer shape regardless of whether refine ran this frame, so `XRE_GPU_HIZ_DIRTY_BYPASS` is safe by default and the crash documented in `zero-readback-predraw-barriers-load-bearing.md` cannot recur.

**Shipped 2026-05.** Implementation diverged from the original passthrough-copy plan: the actual hazard was the *implicit* `ShaderStorage | Command` memory barrier the refine compute dispatch provided between the cull SSBO writes and the downstream `MultiDrawElementsIndirectCount` read. The cull pass already writes `_culledSceneToRenderBuffer` + `_culledCountBuffer` with the same shape downstream consumers expect post-swap on the refine path, so no buffer-pointer swap or full-command copy is required in bypass — only the explicit barrier.

- [x] Bypass branch in `ApplyGpuHiZOcclusion` now issues `AbstractRenderer.Current?.MemoryBarrier(ShaderStorage | Command | ClientMappedBuffer)` before returning. No swap, no command-buffer copy.
- [x] `XRE_GPU_HIZ_DIRTY_BYPASS` defaulted ON; `=0` or `=false` disables.
- [x] Repo memory: `/memories/repo/gpu-hiz-dirty-bypass-phase4.md` records the new contract; `zero-readback-predraw-barriers-load-bearing.md` left as-is (separate concern: material-CPU-write visibility).

Validation:
- [ ] `Measurement-P3-ZeroReadback-Census` and `Measurement-P3-ZeroReadback-Census-NoOcclusion` baselines unchanged — owner to run.
- [ ] Stress test: scripted camera teleport every 4 frames for 60 seconds in `GpuIndirectZeroReadback` + `GpuHiZ` — no `STATUS_STACK_BUFFER_OVERRUN`, no visual popping — owner to run.

---

## Phase 7 — Path Audit (added 2026-05)

Findings from auditing the two paths after Phase 4 shipped:

- [x] **HiZ refine dispatch sizing under zero-readback** (`ApplyHiZOcclusionRefine` ~L1043). Old `Math.Min(Math.Max(VisibleCommandCount, 1u), CulledSceneToRenderBuffer.ElementCount)` collapsed to one workgroup when the stale CPU mirror was 0, false-occluding everything past index 256. Fixed: zero-readback now dispatches `CulledSceneToRenderBuffer.ElementCount` and the shader's `idx >= InCount` early-return makes excess threads near-free.
- [x] **CpuQueryAsync GPU-dispatch path hot-path allocation** (`SubmitCpuOcclusionQueryBatch`). Per-frame `new HashSet<uint>(_cpuOcclusionPending.Count)` was a hot-path heap allocation. Fixed: pooled in `_cpuOcclusionPendingScratch` field, `Clear()`'d per submission.
- [ ] (low-priority) `ReadUIntAt(_culledCountBuffer, ...)` is called both in submit and filter — potential redundant stall in CpuQueryAsync. Cache once per pass if it shows up in profiles.
- [ ] (low-priority) OpenGL gate at submit-time silently bails on non-OpenGL backends. Acceptable today; add a one-time log if diagnosability becomes painful.

---

## Phase 5 — Docs, Memory, Reports

- [ ] Update [docs/architecture/rendering/mesh-submission-strategies.md](../../../architecture/rendering/mesh-submission-strategies.md) with the resolver downgrade contract from Phase 3.
- [ ] Update [docs/architecture/rendering/default-render-pipeline-notes.md](../../../architecture/rendering/default-render-pipeline-notes.md) HiZ section with the corrected prepass-feeds-occlusion semantics from Phase 1.
- [ ] Add `/memories/repo/` notes:
  - `gpu-hiz-self-occlusion-gate-removed.md` — record the corrected reasoning so future agents don't reintroduce the gate.
  - `meshlet-resolver-opengl-downgrade.md` — record the dialect requirement and where the downgrade is logged.
  - `cpuqueryasync-occlusion-implemented.md` or `cpuqueryasync-occlusion-removed.md` depending on Phase 2 outcome.
- [ ] Regenerate audit reports: `Report-All-AuditReports` task; review `Build/Reports/` for any new warnings introduced.

---

## Risks & Open Questions

- Removing the HiZ self-occlusion gate (Phase 1) assumes the depth prepass *always* runs before `OpaqueForward`/`MaskedForward` in every pipeline configuration. If a pipeline variant ever runs the lit pass without a prepass, occlusion would use stale or absent depth. Mitigation: use the `DepthPrePassRanThisFrame` runtime flag (Option B) instead of a blanket removal.
- Implementing `CpuQueryAsync` adds per-frame GL state churn (color/depth mask flips, query begin/end). Budget impact must be validated against `Benchmark-ShaderPipeline-FrameBudget` before turning it on by default.
- An `GL_NV_mesh_shader` indirect-count path (Phase 3 decision) is non-trivial; recommend deferring to post-v1 unless customer hardware demands it.

## Out of Scope

- CPU software occlusion (`CpuSoftwareOcclusion`) — separately tracked in [masked-software-occlusion-culling-todo.md](masked-software-occlusion-culling-todo.md).
- Sponza uber-shader link timeouts (different root cause for "missing geometry") — see [uber-shader-sponza-link-failures-todo.md](uber-shader-sponza-link-failures-todo.md).
- DX12 meshlet backend.
