# Render Submission Performance Debug & Optimization Plan

Status: Active (updated 2026-05-12)
Owner: Rendering

## 1. Problem

Two-Sponza scene, lights off, ImGui editor hidden.

**Original problem (P1 — 2026-05-10):**

- **CpuDirect**: ~80 fps. Healthy baseline.
- **GpuIndirectZeroReadback**: ~10 fps. 8x slower than CpuDirect on the same static scene.
- **GpuIndirectInstrumented**: also degraded; froze above ~28 commands before optimizations landed.

**Current state after P6 (2026-05-12, post FBO-attach storage-commit fix, Debug build):**

| Strategy | Drop events / 60 s | Median fps during drops | p10 | p90 | Interpretation |
| --- | ---: | ---: | ---: | ---: | --- |
| CpuDirect | 1595 (~30/s, capture cut short at +54 s) | 25.64 | 21.35 | 674 | Sustained sub-threshold; **regressed** vs P1. Mirror+Hi-Z plumbing now off but Debug-build CPU work dominates. |
| GpuIndirectInstrumented | 353 (~14/s, capture cut short at +25 s) | 26.85 | 5.89 | 90 | Intermittent drops; recovered from "frozen at 28 commands". |
| GpuIndirectZeroReadback | 768 (~13/s, full 60 s) | 25.02 | 5.07 | 90 | **No longer crashes.** Drop-event profile now matches GpuIndirectInstrumented within noise. Original 8x gap closed. |

The drop log records only frames slower than the threshold, so "0 drops" can mean either healthy *or* dead-process. The measurement script (`Tools/Measure-MeshSubmissionBaselines.ps1`) now distinguishes user-closed (exit code 0, no WER) from genuine crash (non-zero exit OR WER record). ZeroReadback's earlier crash (P5) was a real fastfail in `glNamedFramebufferTexture` due to a missing immutable-storage commit before FBO attach — fixed in `GLTexture<T>.AttachToFBO` (see Findings Log P6). CpuDirect Debug-build fps is well below the original ~80 fps P1 target. **Re-measure on Release before drawing further conclusions about absolute targets.**

> **Update (P8, 2026-05-13).** P7 is retracted. The "occlusion = bottleneck"
> conclusion was an artefact of (a) `XRE_OCCLUSION_CULLING_MODE=None`
> being an invalid enum value (silently ignored — variant `C` ran the
> same `GpuHiZ` mode as `A` and `B`) and (b) variant `C` running third
> in sequence on already-warm shader / asset / upload caches. New
> per-stage instrumentation (`HiZStageStats` in
> `GPURenderPassCollection.Occlusion.cs`) shows every occlusion
> attempt actually exits at `GpuHiZ.Exit.DepthNot2D` — the depth view
> is an `XRTexture2DView` (mono) or `XRTexture2DArrayView` (stereo),
> not the bare `XRTexture2D` the path requires, so HiZ has been
> silently no-op'd. The real frame-time drivers are
> `XRWindow.ProcessPendingUploads` (6.2 s cumulative across drops) and
> cold-cache `AssetManager.DeserializeAsset` stalls. Critical-path
> work is now §10.5 C-DRP-1 / C-UPL-1 / C-CACHE-1 / C-MEAS-1; see
> Findings Log P8.

Goal: GPU-indirect paths within 20% of CpuDirect fps on B1 (two static Sponzas) and B2 (Sponzas + 100 skinned avatars). On B1 (Debug) this is now satisfied; B2 untested.

### Release acceptance targets (A5 baseline)

All numbers measured via `Measurement-Baseline-Release-All` (Release build, 25 s warmup, 60 s capture, cold shader-program cache per variant). A run is acceptable only if **every** target below is met for the named strategy.

| Strategy | Workload | Median fps | p10 fps | Drop events / 60 s | Single-chunk upload spikes |
| --- | --- | ---: | ---: | ---: | --- |
| CpuDirect | B1 (two static Sponzas) | ≥ 80 | ≥ 60 | < 30 | No `[GLUploadQueue] single-chunk upload exceeded hard budget` warnings |
| GpuIndirectInstrumented | B1 | ≥ 64 (within 20% of CpuDirect) | ≥ 48 | < 60 | Same |
| GpuIndirectZeroReadback | B1 | ≥ 64 | ≥ 48 | < 60 | Same |
| CpuDirect | B2 (Sponzas + 100 skinned avatars) | ≥ 60 | ≥ 45 | < 60 | Same |
| GpuIndirect* | B2 | ≥ 48 (within 20% of CpuDirect) | ≥ 36 | < 90 | Same |

Secondary signals (informational, not blocking):

- `profiler-render-stalls.log` shows no stall > 50 ms during the capture window.
- `profiler-main-thread-invokes.log` shows no per-second spike > 200 invokes.
- `XRWindow.ProcessPendingUploads` cumulative time across drops < 1.0 s over the capture window.

## 2. Submission Paths

| Path | Per-frame cost driver |
| --- | --- |
| CpuDirect | `RenderCommandCollection.RenderCPU` → `GLMeshRenderer.Render`. State churn dominates. |
| GpuIndirectInstrumented | `HybridRenderingManager.DispatchRenderIndirect` + `GPUScene.SwapCommandBuffers`. |
| GpuIndirectZeroReadback | Same as instrumented + material-slot scatter/buckets. Production zero-readback path = `FullBucketScan`. |

Path A (skinned bounds direct-AABB write, `SkinnedBoundsGpuDirectAabbWrite`) is a separate optional feature, default OFF.

## 3. Measurement Workflow

### Setup

1. Build configuration: Release/profile for perf numbers; Debug only for triage.
2. Set `XRE_PROFILER_ENABLED=1` and `Engine.Profiler.RenderStallThresholdMs = 16f`.
3. Verify all debug switches in [Program.cs](../../../../XREngine.Editor/Program.cs) are OFF (`DumpIndirectArguments`, `ValidateBufferLayouts`, `LogCountBufferWrites`, `ProbeSourceCommandsBeforeCopy`, `ForceCpuIndirectBuild`, `DisableCountDrawPath`, `SkipIndirectTailClear`).
4. Warm shaders/programs; discard the first few seconds of capture.

### Required defaults

See live values in [Engine.Rendering.Settings.cs](../../../../XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs).

`EnableNvidiaDlss=false`, `EnableIntelXess*=false`, `GpuOcclusionCullingMode=GpuHiZ`, `SkinnedBoundsRecomputePolicy=Never`, `CalculateSkinnedBoundsInComputeShader=false`, `SkinnedBoundsGpuDirectAabbWrite=false`, `UseGpuBvh=true`, `ZeroReadbackMaterialDrawPath=FullBucketScan`, `MsaaSampleCount=4`.

When `CpuDirect` is forced with `GpuOcclusionCullingMode=GpuHiZ`, CPU mesh draws do not consume GPU Hi-Z output. Select `CpuQueryAsync` for hardware query occlusion on CPU draws, or `CpuSoftwareOcclusion` / `XRE_CPU_SOC_OCCLUSION=1` for the CPU software rasterizer path.

### Strategy switching

Use `XRE_FORCE_MESH_SUBMISSION_STRATEGY` env var (overrides settings). Values: `CpuDirect`, `GpuIndirectInstrumented`, `GpuIndirectZeroReadback`.

### Capture tasks

Run from VS Code:

- `Measurement-Baseline-CpuDirect`
- `Measurement-Baseline-GpuIndirectInstrumented`
- `Measurement-Baseline-GpuIndirectZeroReadback`
- `Measurement-Baseline-Release-All` — builds Release and runs all three strategies through `Tools/Measure-MeshSubmissionBaselines.ps1 -Configuration Release`.
- `Measurement-GameLoopRenderPipeline-Release-All` - builds Release and runs `CpuDirect`, `GpuIndirectInstrumented`, and `GpuIndirectZeroReadback` through the game-loop/default-pipeline harness. Meshlets are intentionally excluded.
- `Measurement-PushSubDataBreakdown-GpuIndirectInstrumented` (sets `XRE_PUSHSUBDATA_BREAKDOWN=1`)

Release command-line target:

```powershell
pwsh Tools/Measure-MeshSubmissionBaselines.ps1 -Configuration Release -WarmupSec 25 -CaptureSec 60
pwsh Tools/Measure-GameLoopRenderPipeline.ps1 -Configuration Release -WarmupSec 25 -CaptureSec 60
```

The measurement script launches one editor process per strategy and clears the OpenGL shader-program binary cache between variants by default. Use `-NoClearCachesBetweenVariants` only when intentionally measuring warm-cache behavior.

Use `Measure-GameLoopRenderPipeline.ps1` for true speed comparisons. It enables `XRE_PROFILE_CAPTURE=1`, gracefully closes each editor process so GPU timing histories are dumped, and summarizes per-frame `render_dispatch_ms`, update/collect/fixed-update timings, GPU timestamp-query frame time, render-thread-minus-GPU gap, draw counts, fallback counts, and `GpuReadbackBytes`/`GpuMappedBuffers` totals. For `GpuIndirectZeroReadback`, the harness forces `XRE_ZERO_READBACK_MATERIAL_DRAW_PATH=FullBucketScan` and reports any nonzero readback or mapped-buffer frame as a violation. The summary includes capture-window `Samples` plus process-lifetime `AllSamples`, final sample timestamp/frame/timing, and final readback/fallback counters so failed warmup runs still leave usable evidence instead of a blank row. `-NoSampleHangSec` force-stops a variant when the render-stats file stops advancing after samples have begun. Summaries are written under `Build/Logs/speed-profiles/game-loop-render-pipeline/<timestamp>/`, and the harness keeps the latest three summary runs by default via `-RetainedRunCount`.

For warmed-up steady-state inspection during an interactive editor session, use **Dump Speed Profile** from the in-editor profiler window after the scene has settled. It writes the same render-stats capture files under the current session's `speed-profiles/<timestamp>_profiler-panel/` folder and retains the latest three in-session captures.

Strict `GpuIndirectZeroReadback` runs must not queue async stats-buffer readbacks for draw/triangle publication. Those counters are diagnostic readbacks and are intentionally suppressed on the zero-readback path.

Logs land in `Build/Logs/<configuration>_<tfm>/<platform>/<session>/`:

- `profiler-render-stats.ndjson` - one JSON object per completed render frame when `XRE_PROFILE_CAPTURE=1`.
- `profiler-capture-manifest.json` / `profiler-capture-summary.json` - run metadata and automatic GPU dump results.
- `profiler-gpu-pipeline-*.log` - command-level GPU timing history dumped on graceful shutdown.
- `profiler-fps-drops.log` — primary HotPath data.
- `profiler-render-stalls.log` — render-thread stalls.
- `profiler-main-thread-invokes.log` — MainThreadInvoke flooding.
- `log_opengl.txt` — `[BufferUploadAudit]` rows when breakdown env var is set.
- `profiler-indirect-calls.log` (new, see §3.x) — per-frame indirect call census + GL state-bind counts.

### Required new logging (P3)

The data needed to confirm or reject the P3 bucket-fan-out hypothesis is not currently captured. Add the following before the next measurement pass; all default OFF and aggregate to once-per-second rolling log rows.

1. **Indirect call census** — instrument `DispatchRenderIndirectCountBucket` (and the `DispatchRenderIndirectRange` callers) in [HybridRenderingManager.cs](../../../../XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs):
   - Per pass: `passName`, `materialSlotIds.Count`, `materialTierCount`, `bucketsIssued`, `bucketsSkippedEmpty`, `bucketsSkippedConfigure`, `programSwitches`, `vaoSwitches`, `setUniformsCalls`.
   - Aggregate into a single log line per frame; flush at 1 Hz.
2. **GL state-bind counters** — increment counters in `GLRenderer` for `glUseProgram`, `glBindBufferBase`, `glBindVertexArray`, `glBufferSubData`, `glMemoryBarrier`. Snapshot to the same row. Compare CpuDirect vs ZeroReadback head-to-head; a 10×+ delta on `glUseProgram` or `glBindBufferBase` confirms bucket fan-out as the dominant cost.
3. **GPU-side timer queries** — wrap with `glQueryCounter(GL_TIMESTAMP)`:
   - the whole material-tier bucket loop in `DispatchRenderIndirectMaterialTiers`,
   - `BuildMaterialScatter` compute,
   - `BuildIndirectCommands` compute,
   - `GPUScene.SwapCommandBuffers`.
   - Read back N-frames-late to avoid stalls; log medians. Without GPU timestamps we cannot tell whether the render-thread block is GPU-bound or driver-sync-bound.
4. **Bisect env vars** (default off):
   - `XRE_BUCKET_LOOP_DRY_RUN=1` — execute the slot/tier loop but skip the final `MultiDrawElementsIndirect[Count]` call. If fps recovers near CpuDirect, GL/driver submission cost dominates (validates P3-A below).
   - `XRE_BUCKET_LOOP_SKIP_EMPTY=1` — apply the CPU-side active-slot mask early (see O-18). Should be a no-op for correctness, big win if P3-A is right.
   - `XRE_SKIP_COMMAND_SWAP_IF_CLEAN=1` — short-circuit `SwapCommandBuffers` when content version is unchanged. Validates O-6 cheaply before implementing the full version-stamp gate.
   - `XRE_FORCE_SINGLE_BUCKET=1` — route every draw through bucket (slot=0, tier=0). Strictly for isolating per-bucket overhead from per-draw cost.

### Scenes

- **B1** — two Sponzas (Sponza A `Uber`, Sponza B `Deferred`), lights off. Exercises forward+deferred together. Configured via [Assets/UnitTestingWorldSettings.jsonc](../../../../Assets/UnitTestingWorldSettings.jsonc).
- **B2** — B1 + 100 idle skinned avatars. Needed for Path A measurement.
- **B2-paused** — B2 with animation paused after warmup. Validates Path A dirty-skip.

### A/B toggles (one at a time)

`SkinnedBoundsGpuDirectAabbWrite`, `CalculateSkinnedBoundsInComputeShader`, `SkinnedBoundsRecomputePolicy`, mesh submission strategy, `UseGpuBvh`, `GpuOcclusionCullingMode`, vendor upscale flags.

### GPU-side validation

RenderDoc/Nsight on B1/B2: count `MultiDrawElementsIndirect[Count]`, `glUseProgram`, `glBindBufferBase`, `glBindVertexArray`, `glBufferSubData` per frame.

## 4. Findings Log

### P8 P7 retracted — bisect was contaminated; HiZ depth-view type bug (2026-05-13)

The P7 conclusion ("occlusion ~18 ms/frame") is **wrong**. Two independent
checks falsified it:

1. At the time, `EOcclusionCullingMode` had only `Disabled / GpuHiZ / CpuQueryAsync`.
   `XRE_OCCLUSION_CULLING_MODE=None` from `Tools/Diagnose-ZeroReadbackHz.ps1`
   does not parse and is silently ignored — variant `C` ran the **same**
   `GpuHiZ` mode as variants `A` and `B`. Its 0.22 drops/s and 60 fps
   median come entirely from being the third sequential run: the shader
   binary cache, asset cache, and GL upload queue were already warm.
2. New per-stage instrumentation (`HiZStageStats` in
   [GPURenderPassCollection.Occlusion.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.Occlusion.cs))
   records every entry/exit point of `ApplyOcclusionCulling` and
   `ApplyGpuHiZOcclusion`. Result: **every single call exits at
   `GpuHiZ.Exit.DepthNot2D`**.

The depth view exposed by `DefaultRenderPipeline.DepthViewTextureName` is
constructed in `DefaultRenderPipeline2.Textures.cs::CreateDepthViewTexture()`
as `XRTexture2DView` (mono) or `XRTexture2DArrayView` (stereo). The
occlusion path requires a plain `XRTexture2D`. The `is not XRTexture2D`
check has been silently bypassing GpuHiZ for the duration of every test
we ran. There is no occlusion filtering happening — all frustum-visible
commands have always been drawn.

**Real bottlenecks** (from hot-path aggregation across the baseline drop
log): `XRWindow.ProcessPendingUploads` (6.2 s cumulative across drops),
`XRWindow.RenderFrame` (3.8 s), `AssetManager.DeserializeAsset` for cold
shader-cache entries (per `profiler-render-stalls.log`).

**Open subtasks** (replace C-OCC-1..5):

- **C-DRP-1** Fix `ApplyGpuHiZOcclusion` to accept `XRTexture2DView` /
  `XRTexture2DArrayView` (extract the underlying 2D texture / array
  slice) so GpuHiZ can actually run. Re-baseline ZeroReadback once
  HiZ is producing real occlusion output.
- **C-UPL-1** Profile `XRWindow.ProcessPendingUploads`: log what's
  dequeued, the per-chunk ms, and whether `BoostBudgetUntilDrained` is
  pinned. `GLUploadQueue.FrameBudgetMs = 2.0` but each
  `ExecuteUpload` can overshoot. Likely need post-chunk budget check.
- **C-CACHE-1** Cold shader cache deserialization in the render hot
  path (`AssetManager.DeserializeAsset .../Atmosphere/*.fs.asset` was
  on `XRViewport.Render` for 450 ms). Move to background or warm at
  scene-load.
- **C-MEAS-1** Rewrite `Tools/Diagnose-ZeroReadbackHz.ps1` to clear
  caches between variants and validate env-var parsing so the
  contamination cannot recur. Single-process per variant. **Done
  2026-05-13:** `None` was replaced with `Disabled`; enum overrides now
  validate before launch; OpenGL shader-program cache clears between
  variants unless `-NoClearCachesBetweenVariants` is set.

**Files touched during this diagnosis** (kept as instrumentation,
expected to be reverted/cleaned after C-DRP-1 lands):

- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.Occlusion.cs`
  — `HiZStageStats` aggregator + per-exit-point Record calls.
  `IsEnabled() => true` is currently hardcoded; flip back to the
  `XRE_HIZ_STAGE_LOGGING` env gate once the depth-view fix lands.
- `Tools/Diagnose-ZeroReadbackHz.ps1` — invalid `None` enum value
  in variant `C`.

### P7 ZeroReadback bottleneck = occlusion subsystem (2026-05-13) — RETRACTED, see P8

The P6 framing — "drop profiles within noise across strategies" — was
misleading. ZeroReadback at 13 Hz drop rate / 28 fps median on a static
two-Sponza scene with **no lights** is broken, not borderline. A 3-variant
bisect on B1 confirms the cause:

| Variant (45 s capture, 25 s warmup) | Drops/s | Median fps | P10 | P90 |
| --- | ---: | ---: | ---: | ---: |
| `A` ZeroReadback baseline | 13.4 | 28.48 | 3.87 | 213.66 |
| `B` + `XRE_BUCKET_LOOP_DRY_RUN=1` (skip MDEIC) | 12.49 | 25.89 | 4.37 | 155.05 |
| `C` + `XRE_OCCLUSION_CULLING_MODE=None` | **0.22** | **60.16** | 3.19 | 513.22 |

Reproducer: `pwsh Tools\Diagnose-ZeroReadbackHz.ps1`.

**Diagnosis**: skipping the actual `glMultiDrawElementsIndirectCount`
draws (variant `B`) barely moved fps → GL submission cost is **not**
the dominant factor. Disabling occlusion entirely (variant `C`)
collapsed drops to ~zero and roughly doubled median fps → the GpuHiZ
occlusion subsystem is responsible for ~18 ms/frame on a static scene
that has nothing dynamic to occlude.

**Implications**:
1. **C-GPU-6 (shadow HZB) is deferred** until the basic occlusion path
   is healthy. Building a *second* HZB cull pipeline on top of one that
   eats 50% of the frame budget makes no sense.
2. The recurring "regenerate HiZ + redispatch cull every frame on a
   static scene" pattern is exactly what C-GPU-3 (dirty-frame bypass)
   tried to solve in P5 before it tripped the FBO-attach fastfail. The
   underlying optimisation is correct; the previous attempt routed
   through an unsafe code path. Re-attack with the P6 fix in place.
3. The fact that variant `C`'s `P10` is still 3.19 suggests an
   unrelated periodic stall (~once per few seconds) — likely
   shader-compile, GC, or BVH rebuild. Capture separately.

**Open subtasks** for the next pass:

- **C-OCC-1** Identify which stage(s) of the occlusion pipeline
  contribute the 18 ms. Candidates (per [GPURenderPassCollection.Occlusion.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.Occlusion.cs)):
  (a) `BuildHiZPyramid` (init + per-mip gen compute, ~`log2(W)` dispatches),
  (b) `BvhCull` or `FrustumCull` dispatch + uniforms upload,
  (c) Hi-Z occlusion compute (`_hiZOcclusionProgram`),
  (d) `SwapCulledBufferAfterOcclusion` (any CPU-side fence/wait?),
  (e) per-frame `_hiZSharedCache` table lookups.
  Approach: add `VPRC_GPUTimerBegin`/`End` scopes around each block,
  enable GPU Pipeline Profiling, dump `profiler-gpu-pipeline-*.log`.
- **C-OCC-2** Check for CPU-side stalls inside the occlusion path —
  any `glFinish`, `glClientWaitSync`, `glGetSync(GL_SYNC_STATUS)` in
  a busy loop, or buffer-map that the "zero readback" label promised
  was gone. Grep is fast; this could be a one-liner fix.
- **C-OCC-3** Re-attempt static-scene short-circuit: if the world bvh
  generation id, command count, and camera VP are all unchanged from
  the previous frame, skip the HiZ rebuild + cull dispatch and reuse
  the prior `_culledSceneToRenderBuffer`. P5 attempted this and tripped
  the FBO crash because it short-circuited *too early* (before storage
  commit). With the P6 fix in place the safe gate is "after the FBO
  pass for this view has fully attached at least once".
- **C-OCC-4** Investigate `_hiZSharedCache` per-frame churn — survey
  flagged a `ConditionalWeakTable` access for every render-pass pass;
  on a static frame the cached pyramid should be reused but the cost
  of the lookup + the `Generation` mismatch path may itself be the
  bottleneck.
- **C-OCC-5** Verify the same diagnosis holds on Release build before
  committing significant changes. Debug-build CPU dispatch overhead
  is often 5–10x Release.

### P6 ZeroReadback fastfail root-caused + fixed (2026-05-12)

The P5 ZeroReadback crash was traced via `dotnet-dump` minidump analysis to
`Silk.NET.OpenGL.GL.NamedFramebufferTexture` → `nvoglv64.dll+0x108f36d`,
`STATUS_STACK_BUFFER_OVERRUN` (`0xc0000409`). The driver indexes a
stack-allocated mip-level table sized by the attached texture's allocated
mip count; if the texture was generated (`glGenTextures`) but immutable
storage (`glTextureStorage2D`) had never been committed, that table is empty
and the driver writes past its stack canary → fastfail. ZeroReadback's
FBO-build order exposed a window where freshly-genned textures were attached
to a freshly-created FBO before the deferred storage-commit ran. The other
strategies serialise this differently and never tripped it.

**Fix**: `GLTexture<T>.AttachToFBO` now invokes a new virtual
`EnsureStorageAllocatedForFBOAttach()` hook between `Bind()` and
`Api.NamedFramebufferTexture`. `GLTexture2D` overrides it to call the
existing `EnsureStorageAllocated()` (which performs `glTextureStorage2D`
for immutable textures). `EnsureStorageAllocated()` visibility was promoted
`private → internal` to enable this. Mutable (resizable) textures fall
through unchanged — pre-committing them with `glTexImage2D` was tried and
threw `ArgumentOutOfRangeException` on `Rg16f`/`R16f` without helping.

**Diagnostic infra (kept in tree, env-gated)**:
- `XRE_CRASH_BREADCRUMBS=1` — direct file-append crumbs in
  `GLTexture.AttachToFBO`, `HybridRenderingManager`
  (MDEIC + IndirectComp.Dispatch), and `GPURenderPassCollection.Occlusion`
  → `Build/Logs/crumbs.log`. Survives `RaiseFailFastException`.
- `XRE_GL_DEBUG=1` — synchronous GL debug callback with managed stack
  traces for high-severity messages → `Build/Logs/gldebug-high.log`.
- `Tools/Repro-ZeroReadbackCrash.ps1` — single-shot harness; reports WER
  fault offset + last crumbs.

**Caveat / open follow-up**: with `XRE_GL_DEBUG=1` still set, ZeroReadback
hits the *same* `nvoglv64+0x108f36d` fastfail offset on a tex whose storage
*has* been committed (always `fbo=11 / ColorAttachment0 / Rgba16f / 1920×1080`
during the GBuffer setup burst, ~30–35 s in). This is a separate
NVIDIA-debug-context-only failure, not the production code path. Documented
in repo memory `zeroreadback-fbo-attach-crash.md`; **do not enable
`XRE_GL_DEBUG=1` while running ZeroReadback baselines.**

**Measurement-script fix**: `Tools/Measure-MeshSubmissionBaselines.ps1` no
longer mis-reports user-closed windows as crashes. It distinguishes
`CRASHED` (non-zero exit code OR WER record matching the PID) from
`user-closed` (exit code 0, no WER) from `no-fps-log` (process never
produced the drop log).

**Re-measured baselines (B1, two Sponzas, lights off, Debug)**:
```
Strategy                Drops/60s  MedianFps  P10    P90    Note
CpuDirect                  1595      25.64    21.35  674.08 user-closed at +54s
GpuIndirectInstrumented     353      26.85     5.89   90.30 user-closed at +25s
GpuIndirectZeroReadback     768      25.02     5.07   90.20 full 60s, no crash
```

The original "8× slower" gap between CpuDirect and ZeroReadback (P1) has
collapsed: drop profiles are within noise of each other. Median
fps-during-drop is uniformly ~25 — Debug-build CPU work appears to dominate
across all three strategies on this scene. **Action items**:
1. Re-measure all three on Release build before adjusting §1 targets.
2. Add a sustained-fps probe (rolling 1 s avg, not drop-event-only) so
   future measurements aren't biased to sub-threshold statistics.
3. Move to §10.5 step 7 (C-GPU-6 caster-shadow HZB) — B2 win remains
   untested.

### P5 baseline re-measurement (2026-05-12): post C-CPU-1..2 + C-GPU-1..5

After landing the §10 path-isolated occlusion work (mirror disabled when
`XRE_CPU_HIZ_OCCLUSION` is off, GPU Hi-Z dirty-state passthrough,
BVH capacity headroom, occluder-tier audit), re-ran B1 (two Sponzas, lights
off, UnitTesting world) for 60 s per strategy after a 25 s warmup, Debug
build. Driver: same machine.

```
Strategy                Drops/60s  MedianFps  P10    P90    LogDir
CpuDirect                  1779      24.05    20.51  717.46 xrengine_..._pid18076
GpuIndirectInstrumented     423      27.26     5.12  130.80 xrengine_..._pid33864
GpuIndirectZeroReadback     N/A      —         —      —    xrengine_..._pid19012  (CRASHED — see retraction below)
```

> **Retraction (post-P5 follow-up).** The "0 drops" entry above was *not*
> a healthy steady state — the editor process crashed mid-capture with
> `nvoglv64.dll` STATUS_STACK_BUFFER_OVERRUN (`0xc0000409`, fault bucket
> `1656360385746696093`). The drop-event-only log produces zero records
> when the process is dead, which the measurement script
> (`Tools/Measure-MeshSubmissionBaselines.ps1`) misinterpreted as healthy.
> Root cause: the C-GPU-3 dirty-frame passthrough early-returns before
> `SwapCulledBufferAfterOcclusion`; under `GpuIndirectZeroReadback` the
> downstream `MultiDrawElementsIndirectCount` chain expects post-refine
> state and the resulting state-handoff inconsistency triggers the driver
> fail-fast under sustained dirty conditions. Fix landed: C-GPU-3
> dirty-bypass is now opt-in (`XRE_GPU_HIZ_DIRTY_BYPASS=1`); default OFF
> restores legacy refine-always behavior. The B1 ZeroReadback re-measure
> remains pending. Separately, the BVH "overflow" warnings observed in
> the same run are NOT capacity exhaustion — they are stage-2/3
> malformed-tree detection from duplicate Morton codes (two Sponzas with
> near-identical centroids). C-GPU-4 capacity headroom (`nodeCount + 8`
> = `2N-1+8`) is mathematically sufficient for the observed N=52 case;
> the `OVERFLOW_BVH` shader bit is overloaded across capacity and
> malformed-tree, indistinguishable without `XRE_HIZ_CULL_TRACE=1`.

Key observations:

- ZeroReadback now stays above the drop threshold the entire steady-state
  window — drops only during shader warmup. This is the **opposite** of P1
  where ZeroReadback ran ~10 fps. C-GPU-3 (dirty-frame passthrough) and
  C-GPU-4 (BVH capacity) plus the C-CPU-1/2 mirror gating did the heavy
  lifting; the bucket fan-out cost (P3 hypothesis, O-18..O-21) is no longer
  the bottleneck on this scene.
- CpuDirect regressed in absolute terms: median fps-during-drop ~24 with
  drops firing ~30/s = continuously sub-threshold. Two suspects:
  (a) Debug build (P1 numbers may have been Release/profile),
  (b) `RenderResourceRegistry` ConcurrentDictionary swap (P4 fix #1) added
  contention. Re-measure on Release before chasing.
- GpuIndirectInstrumented sits between the two — recovered from the P1
  "freezes at 28 commands" but still has periodic drops. Likely the
  per-frame readback inherent to the Instrumented path is the residual cost.

Caveat: `profiler-fps-drops.log` is conditional (records only sub-threshold
frames). Zero entries → healthy or frozen; cross-checked rendering log shows
ZeroReadback was rendering shaders at end of capture, so healthy.

Action items spawned:
- Re-run measurement in **Release** to settle the CpuDirect regression
  question before adjusting any §1 targets.
- Add a sustained-fps probe (rolling 1 s avg, not just drop events) so future
  measurements aren't biased to drop-event statistics.
- Proceed to C-GPU-6 (caster-shadow HZB) — the B2 win remains untested and is
  the next item per §10.5 step 7.

### P4 regression sweep (2026-05-12): GpuHiZ-as-default fallout

Background: making `GpuOcclusionCullingMode=GpuHiZ` the default ran the CPU-side
Hi-Z visibility plumbing in `CpuDirect`. That code path was incomplete and
produced four interacting regressions that together crushed CpuDirect from
~80 fps to ~2-9 fps. **Do not undo these fixes without replacing them.**

| # | Symptom | Root cause | Fix (do NOT revert) | Guarding flag |
| --- | --- | --- | --- | --- |
| 1 | `RenderResourceRegistry.TryGetTexture` throws `InvalidOperationException` ("Operations that change non-concurrent collections must have exclusive access") every frame | Render thread reads `_textures` Dictionary while worker threads register descriptors | `_textures`/`_frameBuffers`/`_buffers`/`_renderBuffers` are now `ConcurrentDictionary`; register paths use `GetOrAdd`, remove paths use `TryRemove` | n/a (always on) |
| 2 | `GpuBvhTree.Build` overflow logged every frame (`primitives=52, nodes=103`); CpuDirect dropped to ~2 fps once meshes appeared | `GPUScene.PrepareBvhForCulling` re-built on every Add/Remove even when post-mutation `commandCount` matched the count that just overflowed → infinite retry+log | Added "same-count overflow" short-circuit that clears dirty and returns. Next genuine count change still unsuppresses normally | n/a |
| 3 | Deferred meshes render black, forward uber meshes invisible, only resolves when camera moves fast | CPU Hi-Z visibility snapshot's static-scene short-circuit reused stale snapshot. Meshes absent from snapshot tick down `TemporalOcclusionHysteresisFrames=2` and are CPU-culled. Fast camera motion invalidated snapshot compatibility, masking the bug | (a) Removed the static-scene short-circuit in `PrepareCpuHiZVisibility`. (b) **Gated CPU-side trust of the Hi-Z snapshot behind `XRE_CPU_HIZ_OCCLUSION=1`** because the underlying GPU cull-output / source-command-decode is still under-reporting visible meshes | `XRE_CPU_HIZ_OCCLUSION` (default off) |
| 4 | CpuDirect baseline stuck at ~7-9 fps even with occlusion disabled | `VisualScene3D.ShouldMaintainCpuGpuCommandMirror()` forced the full CPU↔GPU command mirror (per-frame command buffer upload + BVH build) whenever `CpuDirect + GpuHiZ`, but with #3 gated off nothing consumed the mirror — pure dead weight | Mirror now only enabled when `XRE_CPU_HIZ_OCCLUSION=1` (or Surfel-GI is active) | `XRE_CPU_HIZ_OCCLUSION` |

Latent issue still open: the GPU cull pass output feeding
`_culledSceneToRenderBuffer` is missing mesh commands that should be visible
(the actual reason #3's snapshot is unsafe). Suspects: Hi-Z over-cull against
stale reprojected depth, BVH-fallback path (#2) skipping commands, or
`command.Reserved1 → IRenderCommandMesh` decode in
`GPURenderPassCollection.Occlusion.PublishCpuHiZVisibilitySnapshotIfReady`.
Until that's diagnosed and corrected, `XRE_CPU_HIZ_OCCLUSION` MUST stay
default-off. When you fix it, also flip the mirror gate back on.

Anti-regression rules going forward:

1. The render thread must NEVER read a plain `Dictionary<,>` that a worker
   thread can mutate. New caches in `RenderResourceRegistry`-shaped classes
   must use `ConcurrentDictionary` or an explicit lock.
2. Any GPU job whose result is consumed by CPU draw decisions must be
   *correctness*-validated against the non-GPU path on a static and a
   churning scene before becoming a default. Performance wins from skipping
   CPU work are invisible-mesh bugs if the skip set is wrong.
3. Per-frame BVH/AABB upload + GPUScene command mirror are **opt-in**, gated
   on the actual consumer being enabled. Never wire them on by side-effect
   of a settings default flipping.
4. Any GPU dispatch whose result feeds a same-frame CPU readback must use
   the fence-N-late pattern in `PublishCpuHiZVisibilitySnapshotIfReady`
   (insert `FenceSync` post-dispatch, non-blocking `ClientWaitSync(0)` on
   publish, defer one frame if not signaled). Synchronous readback inside
   the render frame collapses fps to ~7 Hz immediately.

### P1 baseline (2026-05-10): GPU paths freeze above ~28 commands

CpuDirect ran clean. Both GPU-indirect strategies froze on two Sponzas. Render-thread queue grew unboundedly (queuedRender 7650; PushSubData waits 7–11 s). Single-Sponza (28 commands) GpuIndirectInstrumented was healthy. **Conclusion:** per-frame `PushSubData` traffic scaled super-linearly with command count and saturated the render thread.

### P1.5 attribution (2026-05-10): MeshAtlas full re-upload

With `XRE_PUSHSUBDATA_BREAKDOWN=1`, the flood resolved to a single buffer instance per attribute: `MeshAtlas_Dynamic_{Tangents,Positions,Normals,UV0,Indices}`. Peak 620 MB/s on Tangents alone, 21–28 uploads/s of the entire atlas.

Root cause: `GPUScene.RebuildAtlasIfDirty` called `state.Positions.PushSubData()` with no args (full buffer) every time the atlas was dirty, even though `AppendMeshToAtlas` only modifies the tail.

### P2 post-O-11 (2026-05-11): atlas fixed, hierarchy panel exposed

After O-11 landed, MeshAtlas pushes dropped to 24–128 bytes per dump. `XRViewport.SwapBuffers.MeshCommands` still reported 150–393 ms — but with `EmittedCommands: 0`, meaning the swap was a barrier wait, not work. Recovered top stalls on the render thread:

| Scope | Max ms | Cause |
| --- | ---: | --- |
| `UI.DrawWorldHierarchyTab` | 756 | ImGui hierarchy walking ~6000 nodes per frame. |
| `RenderCommand.Render` (bare) | 605 | Single CPU draw blocking — likely barrier wait. |
| `Invoke:GPUScene.LodTransitionBuffer.Initialize` | 4729 | Buffer init on render thread (one-shot, 5 s stall). |
| `Invoke:GLDataBuffer.PushSubData` | 1457 | Large single-range PushSubData. |

### P2.5 post-O-12/13/14 (2026-05-11): hierarchy + init fixed

Profiler-panel collector moved to app thread, hierarchy off-screen rows fast-skip, LOD init slimmed, MeshDataBuffer dirty-range tracker. Hierarchy max dropped 753 → 144 ms.

### P2.6 post-O-15/16/17 (2026-05-12): material maps gated, BVH spam suppressed

`MaterialAggregationFlags` and `MaterialSlotLookup` now dirty-gated by signature (down to 3 uploads/session vs per-frame). Removed hot `List<uint>` allocation. BVH failed-build spam dropped from 376 → 5. **Original 8x fps gap not yet closed** — measured ZeroReadback runs are stuck in shader warmup; one confirmation run crashed with `-1073740791` before steady state.

### P3 hypothesis (2026-05-12): zero-readback bucket fan-out dominates

Post P2.6, per-frame buffer-byte traffic is small but the 8x gap persists. The remaining structural difference between CpuDirect and `FullBucketScan` is in the **dispatch loop shape**, not buffer uploads:

`DispatchRenderIndirectMaterialTiers` in [HybridRenderingManager.cs](../../../../XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs) walks `materialSlotIds.Count × MaterialTierCount` buckets per render pass and calls `MultiDrawElementsIndirectCount` on **every** bucket regardless of whether that (material, tier) actually has any commands this frame. Two Sponzas import ~25 unique materials each ⇒ ~50 slots × `MaterialTierCount` tiers × multiple passes (depth-prepass, gbuffer, forward, shadow, transparent) ⇒ hundreds–thousands of indirect draws/frame versus ~28 for CpuDirect.

Each `MultiDrawElementsIndirectCount` sources its draw count from a GPU-written SSBO (`MaterialTierDrawCounts`), which forces the driver to **implicitly synchronise against the scatter compute that wrote those counts**. The single coalesced `MemoryBarrier` ahead of the loop (O-7) does not eliminate this — every empty-bucket draw still costs a `glUseProgram` / `glBindVertexArray` / state-set + driver count sync.

Matches every observed symptom:

- Bare `RenderCommand.Render` timing scales with queued jobs (jobs back up because the call inside doesn't return).
- O-7 (per-draw barrier coalesce) had no effect — barrier wasn't the cost.
- Per-frame `glBufferSubData` is now tiny but fps is unchanged.
- CpuDirect at ~28 commands matches the bucket count of a single-Sponza healthy run.

**P3-A — primary suspect:** bucket fan-out dispatches dominate driver/CPU cost on the render thread.
**P3-B — secondary suspect:** swap-time `Memory.Move + PushSubData(0, byteCount)` on `_updatingCommandsBuffer` triggers an implicit driver sync against the previous frame's compute read of `RenderCommandsBuffer`, even when content is identical (motivates O-6 / O-21).

Confirm with the §3 "Required new logging (P3)" instrumentation before implementing O-18..O-21.

### Outstanding hypothesis

Bare `RenderCommand.Render` stall scales 1:1 with `QueuedRenderJobsNow` (542 jobs → 541 ms). Queue grows because the render thread blocks inside `RenderCommand.Render`, not because jobs are slow to drain. Suspect per-frame SSBO writes triggering implicit driver sync on the previous frame's GPU read. O-7 (per-draw barrier coalescing) is no longer the suspect — bare timing is invariant to it.

P3 (above) refines this: the implicit-sync cost is not from buffer *writes* but from `MultiDrawElementsIndirectCount` reading GPU-written count buffers across hundreds of empty buckets per frame. O-18 (active-slot CPU mask) is the cheapest test; O-21 (persistent-coherent ring-mapped command buffer) addresses the residual sync if P3-B is real.

## 5. Optimization Backlog

### Landed

| ID | Change |
| --- | --- |
| O-11 | MeshAtlas subrange-append push (tail only, restart on resize). |
| O-12 | Drop redundant `PushSubData`/`MapBufferData` in `LodTransitionBuffer.Initialize`. |
| O-13 | Hierarchy panel: fast-skip off-screen leaves (`IsRectVisible` + `Dummy`). |
| O-14 | MeshDataBuffer dirty-range tracker (subrange push). |
| — | `RenderCommandCollection` dirty-delta queue (per-command `_dirty`/`_swapQueued`). |
| — | Profiler-panel collector moved to app thread @ 10 Hz. |
| O-15 | Audit per-frame SSBO writes for ring/fence/dirty gating (audit only). |
| O-16 | Profiler scopes inside `RenderCommand.Render` (swap, indirect build, scatter, multi-draw). |
| O-17 (partial) | Dirty-gate `MaterialAggregationFlags` + `MaterialSlotLookup` by signature; remove hot allocation; suppress repeated identical-count BVH rebuild failures. |

### Open — GPU-indirect static-scene stall (priority order)

| ID | Change | Predicted win | Risk |
| --- | --- | --- | --- |
| O-18 | **Active-slot CPU mask.** At commit/collect time (where each command's `materialSlotId` is already known), maintain a per-pass `BitArray`/packed bitmask of slots that have ≥1 command. `DispatchRenderIndirectMaterialTiers` skips bucket iterations whose slot bit is clear. Pure CPU knowledge — not a readback, zero-readback contract preserved. | Largest expected win on B1: collapses the slot loop from O(allMaterials) to O(usedMaterials in this pass). | Low. Stale mask = missed draws; assert mask version matches command-collection version. Validate with `XRE_BUCKET_LOOP_SKIP_EMPTY=1`. |
| O-19 | **Per-pass slot pre-filter.** Build the `materialSlotIds` slice per `RenderPass` at commit time and store on `GPURenderPassCollection`. Today the loop iterates all slots and `continue`s on pass mismatch after doing material/program work. | Removes the per-slot `RenderPass` check and dead program/state work. | Low. Slot lists must invalidate when material `RenderPass` changes. |
| O-20 | **State-grouped slot order.** Sort each pass's slot list once at commit by `(program, VAO, depth/blend state)`; cache by command-version. The current loop calls `UseProgram` / `ConfigureIndirectRendererForTier` per slot regardless of redundancy. | Cuts redundant `glUseProgram` / VAO binds inside the bucket loop. | Low. Order-dependent state must not leak across slots (depth-test, blend). |
| O-6 | Version-stamp `_updatingCommandsBuffer` and `_updatingTransparencyMetadataBuffer`; skip `Memory.Move` + `PushSubData` in `SwapCommandBuffers` when versions match. Validate cheaply with `XRE_SKIP_COMMAND_SWAP_IF_CLEAN=1` before the full implementation. | Removes dominant per-frame buffer cost in static GPU frames; eliminates an implicit driver sync against the previous-frame compute consumer (P3-B). | Do NOT include `_commandAabbBuffer` — separate lifetime, feeds BVH. |
| O-21 | **Persistent-coherent ring-mapped `RenderCommandsBuffer`.** Replace the `Memory.Move + glBufferSubData(0, byteCount)` swap with a 2- or 3-slot persistent-coherent mapped region; CPU writes slot `frame % N`, shaders read by slot index bound this frame. Fences on the slot before reuse. | If P3-B is real (driver implicit-sync on the swap-time `glBufferSubData`), this is the structural fix. | Ring correctness: readers must bind the slot the CPU wrote *this* frame. Centralise slot selection in `GPUScene`. DEBUG asserts. |
| O-17 (finish) | Get a clean warmed ZeroReadback capture. Identify residual per-frame `glBufferSubData` from O-15 audit table. Apply ring-buffer or persistent-coherent + fence to remaining single-instance SSBOs (`MaterialTierIndirect*`, `MaterialTierDrawCounts`, `CulledSceneToRender`, scatter tables, indirect-build output). | Closes the residual gap after O-18..O-21. | Ring-buffer correctness: readers must read the slot the CPU wrote this frame. Centralise slot selection in `GPUScene`. |
| Extend O-11/O-14 | Tail-append for `RenderCommandsBuffer` resize uploads and `LodTableBuffer` scattered writes. | Smaller residual PushSubData traffic. | Low. |
| O-9 | Cache `CoalesceContiguousBatches` output by command-list version. | Removes per-frame batch list allocation. | Low. |
| O-8 | Frame-stable submission plan for CpuDirect (`SubmissionEntry[]` built at commit). | Better state grouping; lower collect/sort cost. | Medium. |
| O-7 | Coalesce `MemoryBarrier` calls (one per pass instead of per draw). **Deprioritised** — bare `RenderCommand.Render` is invariant to per-draw barriers. | Reduces driver serialization. | Correctness if compute interleaves writes. Default-off behind opt-in flag. |

### Open — Path A (skinned bounds direct-AABB write)

Only meaningful after P0 activation contract is proven. Default-off until then.

| ID | Change |
| --- | --- |
| P0-1 | Fix Path A activation contract. Today `TryComputeFromPrepassOutput` returns empty positions on GPU-only success, and `ApplySkinnedBoundsResult` treats that as failure. Split CPU-positions-cached from bounds-computed states, or pass an explicit success flag. |
| O-1 | Reuse Path A registry snapshot buffer in `SkinnedMeshBoundsCalculator.RefreshAllSkinnedAabbs` (kill the `RenderableMesh[]` per-call alloc). |
| O-2 | Per-mesh dirty tracking; skip Path A work for clean meshes (validates on B2-paused). |
| O-3 | Gate `RefreshAllSkinnedAabbs` behind actual BVH need (build/refit). |
| O-4 | Reduce-once-per-renderer, scatter to command slots. Removes duplicate full-vertex reductions for multi-submesh meshes. |
| O-5 | Batch contiguous sentinel writes into one `PushSubData` per run instead of N tiny 32-byte calls. |
| O-10 | Replace CPU sentinel writes with a compute kernel; one dispatch per acceleration-structure refresh. |

## 6. Next Steps

1. **Land the P3 instrumentation** in §3 (indirect call census, GL state-bind counts, GPU timestamps, four bisect env vars). One-pass change to `HybridRenderingManager.cs` + `GLRenderer`, default off, 1 Hz aggregation.
2. **Capture B1 ZeroReadback warmed** with the new logging + GPU timestamps. Compare to CpuDirect head-to-head.
3. **Read three numbers** to confirm/reject P3-A:
   - `bucketsIssued` vs `bucketsSkippedEmpty` (most should be skippable),
   - `glUseProgram` delta ZeroReadback vs CpuDirect,
   - GPU timestamp delta on the bucket-loop vs CPU wall time on the same scope.
4. **Validate cheaply** with `XRE_BUCKET_LOOP_DRY_RUN=1` and `XRE_BUCKET_LOOP_SKIP_EMPTY=1`. If skip-empty alone closes most of the gap, ship O-18 first.
5. **Implement in order:** O-18 → O-19 → O-20 → (if residual) O-6 → O-21 → O-17 finish.
6. **Re-measure** B1 ZeroReadback fps vs CpuDirect after each; target within 20%.
7. After the gap closes, move to Path A (P0-1 first, then O-1..O-5, O-10).

## 7. Risks

- Path A activation fix must not break consumers needing CPU skinned vertex positions (BVH/raycast). Make GPU-only success explicit.
- Per-mesh dirty tracking can produce stale culling. DEBUG asserts comparing dirty revisions to dispatched revisions.
- O-6 version-stamp must cover every copied payload, or stale frames render. Centralise increment helpers; DEBUG asserts on direct writes. Do NOT fold `_commandAabbBuffer` into the same gate.
- O-7 barrier coalescing is a correctness footgun; default-off, per-batch opt-in.
- O-10 compute sentinel seeding must run before BVH refresh and after any compute writing positions; encode ordering in `VPRC_BuildAccelerationStructure`.
- Ring-buffer (O-17): readers must read the slot the CPU wrote this frame. Bind by slot index, not buffer handle.

## 8. Validation Checklist

- [ ] Measurement preset output records build config, strategy, zero-readback path, debug flags, `UseGpuBvh`, Path A setting, upscale flags.
- [ ] Captures taken after warmup; first warmup frames excluded from medians.
- [ ] `profiler-fps-drops.log` HotPath delta vs baseline captured per phase.
- [ ] Report-NewAllocations diff shows zero new hot-path allocations.
- [ ] Nsight frame: indirect call count, barrier count, `glUseProgram`/`glBindVertexArray`/`glBufferSubData` counts vs baseline.
- [ ] Strict zero-readback validation uses `FullBucketScan` only.
- [ ] `Test-VulkanPhase3-Regression` and `Test-SurfelGi` green.
- [ ] Golden-image parity B1 + B2 vs pre-Path-A baseline.
- [ ] [docs/architecture/rendering/default-render-pipeline-notes.md](../../../architecture/rendering/default-render-pipeline-notes.md) updated if invariants change.

## 9. Related Code

- [HybridRenderingManager.cs](../../../../XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs)
- [GPUScene.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene.cs)
- [RenderCommandCollection.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/RenderCommandCollection.cs)
- [SkinnedMeshBoundsCalculator.cs](../../../../XREngine.Runtime.Rendering/Rendering/Compute/SkinnedMeshBoundsCalculator.cs)
- [RenderableMesh.cs](../../../../XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.cs)
- [VPRC_BuildAccelerationStructure.cs](../../../../XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_BuildAccelerationStructure.cs)
- [Engine.CodeProfiler.cs](../../../../XRENGINE/Engine/Subclasses/Engine.CodeProfiler.cs)
- [Engine.Rendering.Settings.cs](../../../../XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs)
- [docs/architecture/rendering/mesh-submission-strategies.md](../../../architecture/rendering/mesh-submission-strategies.md)

## 10. Forward Architecture: Path-Isolated Occlusion Culling

After the P4 regression sweep we are committing to a clean split: **each
submission path owns its occlusion culling end-to-end. No cross-path data
dependencies.**

### 10.0 Reference algorithms

Required reading before touching the GPU Hi-Z path:

- [Rastergrid — Hierarchical-Z map based occlusion culling (2010)](https://www.rastergrid.com/blog/2010/10/hierarchical-z-map-based-occlusion-culling/)
  — canonical algorithm: build max-depth mip pyramid from the depth
  buffer; per-object sample 4 max-depth texels at LOD chosen so the
  object's screen footprint covers ≤2×2 texels; compare to object's
  nearest depth; cull on GPU only.
- [Nick Darnell — Hierarchical Z-Buffer Occlusion Culling (2010)](https://www.nickdarnell.com/hierarchical-z-buffer-occlusion-culling/)
  — practical refinement: dedicated low-res (e.g. 512×256) occluder pass
  feeds mip 0 (don't reuse the main depth), compute-shader cull per
  bounding sphere, NDC corner sampling that handles perspective
  distortion better than the AMD original, `[numthreads(...)]` care.
- [Nick Darnell — HZB Occlusion Culling: Shadows (2010)](https://www.nickdarnell.com/hierarchical-z-buffer-occlusion-culling-shadows/)
  — caster culling: build a 2nd HZB from the light, cull casters in
  light space, then extrude bbox shadow volume (min-depth points
  re-projected via max light-HZB sample) and re-test against the
  player's HZB.

What our codebase already has that matches these (do NOT rewrite from scratch):

| Reference step | Our implementation |
| --- | --- |
| Hi-Z map mip-pyramid (max-depth reduce) | `BuildHiZPyramid` + `_hiZDepthPyramid` + `_hiZInitProgram` / `_hiZGenProgram` ([GPURenderPassCollection.Occlusion.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.Occlusion.cs)). Source today is the main depth attachment (rastergrid mode), not a dedicated occluder RT (Darnell mode). |
| GPU compute cull comparing AABB-derived depth vs 2×2 Hi-Z max | `_hiZOcclusionProgram` (`Compute/Occlusion/GPURenderOcclusionHiZ.comp`) dispatches per command, writes `CulledSceneToRenderBuffer` + `_culledCountBuffer` + `_occlusionOverflowFlagBuffer` |
| GPU-driven indirect draw count | `glMultiDrawElementsIndirectCount` path gated by `IndirectParityChecklist.UsesCountDrawPath` in [HybridRenderingManager.cs](../../../../XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs) (real flag `DebugSettings.DisableCountDrawPath` on [GPURenderPassCollection.Core.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.Core.cs); older notes referenced a `_isDrawCountAllowedToBeFromGpu` symbol that no longer exists) |
| Caster shadow culling via 2nd HZB | NOT IMPLEMENTED — see C-GPU-6 below |
| CPU-side hardware-query occlusion (NOT a Hi-Z technique) | `CpuRenderOcclusionCoordinator` ([Rendering/Occlusion/CpuRenderOcclusionCoordinator.cs](../../../../XREngine.Runtime.Rendering/Rendering/Occlusion/CpuRenderOcclusionCoordinator.cs)) — GL `AnySamplesPassedConservative` queries with temporal hysteresis |

Important correctness note: **what we used to call "CPU Hi-Z" was not a
Hi-Z algorithm.** It was the CPU reading the GPU compute Hi-Z cull's
output buffer (`_culledSceneToRenderBuffer`) and applying temporal
hysteresis to decide whether to skip CPU draws. That coupling is what
broke during the P4 sweep. The actual Hi-Z algorithm (rastergrid/Darnell)
runs entirely on the GPU and never returns visibility to CPU. The
CpuDirect path therefore should NOT use Hi-Z at all — it should use
either no occlusion, hardware occlusion queries
(`CpuRenderOcclusionCoordinator`), or a CPU software-rasterizer
occluder (Masked SOC) — never the GPU Hi-Z output.

### 10.1 Design rules

1. `CpuDirect` MUST NOT consume any GPU compute cull output. No
   `_culledSceneToRenderBuffer` reads, no GPU command mirror, no fence
   waits inside the render frame. Occlusion is CPU-only.
2. `GpuIndirectZeroReadback` MUST NOT read back to CPU for visibility.
   Cull, count, and draw are entirely GPU-side; the host issues a single
   `glMultiDrawElementsIndirectCount` with the parameter buffer on GPU.
3. `GpuIndirectInstrumented` is allowed to read back (it exists for
   debugging) but only at end-of-frame, never on the critical path.
4. The `EOcclusionCullingMode` enum is explicit. A mode that does not make
  sense for the active path does not silently select a different culler;
  for example, `GpuHiZ` selected while `CpuDirect` is forced means no CPU
  occlusion unless `CpuQueryAsync`, `CpuSoftwareOcclusion`, or the legacy
  SOC toggle is selected.

### 10.2 CpuDirect occlusion (target design)

Active components (already in tree, just need to be the default):
- `CpuRenderOcclusionCoordinator` ([Rendering/Occlusion/CpuRenderOcclusionCoordinator.cs](../../../../XREngine.Runtime.Rendering/Rendering/Occlusion/CpuRenderOcclusionCoordinator.cs))
  driving GL `AnySamplesPassedConservative` hardware occlusion queries with
  per-mesh temporal hysteresis. Vulkan path is conservative-visible until
  command-buffer integration lands.
- `AsyncOcclusionQueryManager` for previous-frame result resolution.
- Standard view-frustum cull stays on the visible-collection pass.

Note: this is the "naive occlusion query" pattern rastergrid/Darnell
explicitly cite as the slower predecessor to GPU Hi-Z. It is fine for
the CpuDirect path because (a) we have it wired up, (b) CpuDirect has
no compute pass to consume a Hi-Z buffer, (c) using previous-frame
results + hysteresis keeps the CPU off the critical sync.

Todos:

- [x] **C-CPU-1**: Keep `Engine.Rendering.Settings.GpuOcclusionCullingMode`
  explicit. `CpuDirect + GpuHiZ` no longer silently coerces to
  `CpuQueryAsync`; CPU Direct occlusion is enabled only by selecting
  `CpuQueryAsync`, selecting `CpuSoftwareOcclusion`, or using the legacy
  `XRE_CPU_SOC_OCCLUSION=1` SOC override. The getter still honors the
  `XRE_OCCLUSION_CULLING_MODE` env override in
  [Engine.Rendering.Settings.cs](../../../../XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs#L1366).
- [x] **C-CPU-2**: Delete CPU Hi-Z snapshot machinery once C-CPU-1 lands.
  Remove from
  [GPURenderPassCollection.Occlusion.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.Occlusion.cs):
  `PrepareCpuHiZVisibility`, `TryGetCpuHiZVisibility`,
  `PublishCpuHiZVisibilitySnapshotIfReady`, `IsCpuHiZSnapshotCompatible`,
  `_cpuHiZVisibleMeshCommands`, `_cpuHiZTemporalOcclusion`,
  `_cpuHiZPendingFence` and friends. Remove
  `s_cpuHiZOcclusionEnabled` + `XRE_CPU_HIZ_OCCLUSION` plumbing from
  [RenderCommandCollection.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/RenderCommandCollection.cs)
  and the CpuDirect+GpuHiZ branch in
  [VisualScene3D.ShouldMaintainCpuGpuCommandMirror](../../../../XREngine.Runtime.Rendering/Rendering/VisualScene3D.cs#L204).
- [x] **C-CPU-3**: CPU software-rasterizer occluder pass (Masked SOC-style)
  for occluder-driven cull without query latency. Implemented:
  [CpuSoftwareOcclusionCuller.cs](../../../../XREngine.Runtime.Rendering/Rendering/Occlusion/CpuSoftwareOcclusionCuller.cs)
  with `BeginFrame`/`SubmitOccludersFromOpaqueCommands`/`TestVisible` API, opt-in via
  `EOcclusionCullingMode.CpuSoftwareOcclusion` plus legacy
  `Engine.EffectiveSettings.EnableCpuSoftwareOcclusionCulling` /
  `XRE_CPU_SOC_OCCLUSION=1` overrides, scalar raster/AABB tests,
  occluder self-bypass via `StableQueryKey`, telemetry counters, and
  meshlet command visibility masking. The AVX2 setting is present for a
  future SIMD fast path; scalar remains the correctness path.
- [x] **C-CPU-4**: Stable per-command identity for CPU occlusion queries.
  Added `RenderCommand.StableQueryKey` (uint, assigned at construction
  via `Interlocked.Increment`) in
  [RenderCommand.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/RenderCommand.cs).
  `CpuRenderOcclusionCoordinator` lookups in `RenderCommandCollection`
  ([RenderCPU](../../../../XREngine.Runtime.Rendering/Rendering/Commands/RenderCommandCollection.cs)
  and `RenderCPUFiltered`) now key by `cmd.StableQueryKey` instead of
  the foreach position. Because identity is stable across insert/remove,
  the per-mutation soft-reset in `BeginPass` was removed; the existing
  `StaleEvictionFrames`-based eviction reclaims orphaned `QueryState`
  entries from removed commands. Acceptance: a mesh insert/remove no
  longer disturbs surviving meshes' `LastAnySamplesPassed` state, and
  no full-draw frame follows a mutation.

### 10.2.x Observability (C-OBS series)

User-visible verification that occlusion culling is actually doing work
on both the CPU-query path and the GPU Hi-Z path. Without this, every
subsequent perf optimization is blind.

- [x] **C-OBS-1**: Add lightweight per-frame occlusion telemetry facility
  ([OcclusionTelemetry.cs](../../../../XREngine.Runtime.Rendering/Rendering/Occlusion/OcclusionTelemetry.cs)).
  Tracks `CpuTested`, `CpuCulled`, `CpuRendered`, `GpuCandidates`,
  `GpuOccluded`, plus active mode + submission strategy. Snapshot/reset
  via `BeginFrame()` invoked from
  [Engine.Rendering.Stats.BeginFrame](../../../../XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.cs).
  No GPU readbacks introduced; GPU counts honor existing
  `IsCpuReadbackCountDisabledForPass()` gate and report unavailability
  rather than stalling.
- [x] **C-OBS-2**: Wire CPU-query path. `RenderCommandCollection.RenderCPU`
  calls `RecordCpuPassBegin` at pass start and `RecordCpuCulledOne` for
  every command the `CpuRenderOcclusionCoordinator.ShouldRender` skips.
- [x] **C-OBS-3**: Wire GPU Hi-Z path. `ApplyGpuHiZOcclusion` records
  candidates/occluded per pass plus the readback-availability flag so the
  UI can clearly distinguish "no occlusion" from "occlusion happening but
  unobservable on zero-readback strategy". It also records whether each
  Hi-Z pass sampled previous-frame history depth or current-frame depth,
  which separates "empty early-frame depth" from "real history depth, but
  no fully occluded whole-mesh bounds".
- [x] **C-OBS-4**: Add Editor → View → **Occlusion** ImGui panel
  ([EditorImGuiUI.OcclusionPanel.cs](../../../../XREngine.Editor/IMGUI/EditorImGuiUI.OcclusionPanel.cs))
  showing live tested/culled/rendered per path, percentages, and an
  explicit message when the zero-readback strategy hides GPU counts
  (suggests switching to `GpuIndirectInstrumented` to verify).

Acceptance: with the editor running on CpuDirect+CpuQueryAsync the panel
shows non-zero `Culled` numbers within a few seconds of moving the camera
to face an occluder; on GpuIndirectInstrumented+GpuHiZ it shows non-zero
`Occluded` from the compute cull; on GpuIndirectZeroReadback+GpuHiZ it
clearly says counts are unavailable (rather than misleading zeros).


Acceptance: CpuDirect baseline regains its ~80 fps headroom on B1; no GPU
buffer is mapped during the CpuDirect frame; profiler shows zero render
stalls attributed to occlusion bookkeeping.

### 10.3 GPU zero-readback occlusion (target design)

Active components:
- `GpuBvhTree` ([Rendering/Compute/GpuBvhTree.cs](../../../../XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.cs))
  GPU-resident BVH (Morton + optional SAH refine), broad-phase reject.
- `VPRC_BuildAccelerationStructure` builds/refits the BVH per-frame from
  per-command world AABBs.
- `_hiZDepthPyramid` + `BuildHiZPyramid` — the rastergrid/Darnell max-depth
  mip pyramid, built from the main depth attachment.
- `GPURenderOcclusionHiZ.comp` (`_hiZOcclusionProgram`) — compute cull
  that samples the pyramid per command and writes
  `CulledSceneToRenderBuffer` + `_culledCountBuffer`.
- Indirect draw: `glMultiDrawElementsIndirectCount` consumes the
  GPU-written count buffer directly.

This already follows the Darnell/rastergrid architecture. The work below
is correctness + sizing, not rewrites.

Todos:

- [x] **C-GPU-1**: Audit every `MapBufferData()` / `GetDataRawAtIndex` /
  `ReadUIntAt` call reachable from the zero-readback frame path. Any
  that fires when `MeshSubmissionStrategy == GpuIndirectZeroReadback`
  and the relevant `IndirectDebug.*` switch is OFF is a bug.
  Catalogue + delete or guard. Read-only audit; cheap; informs the
  rest of this list.
- [x] **C-GPU-2**: Verify the GPU count-draw path is never overridden by a
  diagnostic switch in production. Real gating chain is
  `IndirectParityChecklist.UsesCountDrawPath`
  ([HybridRenderingManager.cs](../../../../XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs)),
  which combines `DrawIndirectBufferBindingReady`, `ParameterBufferBindingReady`,
  `IndexedVaoValid`, `SupportsIndirectCountDraw`, and `!DebugSettings.DisableCountDrawPath`
  on [GPURenderPassCollection.Core.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.Core.cs).
  (Older notes referenced `_isDrawCountAllowedToBeFromGpu`; that symbol does
  not exist — corrected.) Added `[Conditional("DEBUG")]` invariants:
  `AssertZeroReadbackUsesGpuCountPath` and `AssertZeroReadbackProductionInvariants`
  in `HybridRenderingManager`, wired into all three dispatch sites
  (`DispatchRenderIndirect`, `DispatchRenderIndirectRange`, `DispatchRenderIndirectCountBucket`),
  plus `AssertZeroReadbackProductionInvariantsForPass` in
  `GPURenderPassCollection.CapturePassPolicySnapshot`. Asserts fire if any of
  `DisableCountDrawPath`, `ForceCpuFallbackCount`, `ForceCpuIndirectBuild`,
  `!DisableCpuReadbackCount`, or `EnableCpuBatching` is active while the
  pass strategy is `GpuIndirectZeroReadback`, or if the parity checklist
  reports a non-count-draw path. Compiles out of Release.
- [x] **C-GPU-3**: Fix the latent GPU-cull undercount that motivated
  `XRE_CPU_HIZ_OCCLUSION=off`. Three suspects, ranked:
  1. `command.Reserved1 → IRenderCommandMesh` decode via
     `TryGetSourceCommand` going stale after Add/Remove churn.
     **Update (2026-05-12)**: audited — `TryGetSourceCommand` is now
     only consumed by the indirect-text-glyph batch preparation path
     in [HybridRenderingManager.cs](../../../../XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs#L2990).
     The deleted CPU Hi-Z snapshot was the other consumer (C-CPU-2).
     Lookup correctness still matters for text rendering but is not
     a Hi-Z undercount source any more.
  2. **Hi-Z over-cull against the main-depth pyramid on the first frame
     after a scene mutation (no occluders yet rendered).** This was the
     remaining live suspect after C-GPU-4 closed BVH overflow. **Fixed
     (2026-05-12)** by gating the cull-refine step in
     [GPURenderPassCollection.Occlusion.cs `ApplyGpuHiZOcclusion`](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.Occlusion.cs):
     when `ShouldInvalidateGpuHiZTemporalState` returns true (scene
     mutation or large camera jump), the pyramid is still rebuilt (so
     the *next* frame's cull is correct) but `ApplyHiZOcclusionRefine`
     and `SwapCulledBufferAfterOcclusion` are skipped — every
     frustum/BVH candidate passes through. Pure CPU-side decision; no
     readback cost. Telemetry counter
     `OcclusionTelemetry.GpuPassesPassthroughDirty` records each
     bypass, surfaced in the Editor → View → Occlusion panel as
     `Passes Passthrough` (amber). Acceptance: invariant
     `_gpuHiZLastSceneCommandCount` lag of one frame after
     Add/Remove cannot produce missing meshes; first frame after a
     load/teleport draws every visible candidate, second frame onward
     resumes normal Hi-Z cull rates.
  3. BVH fallback (`GpuBvhTree.Build` overflow → non-BVH culling)
     silently dropping commands instead of marking them
     visible-conservative. **Closed by C-GPU-4 (2026-05-12)**; B1
     run with `XRE_HIZ_CULL_TRACE=1` produced zero overflow lines.
  Each gets a focused log capture: **`XRE_HIZ_CULL_TRACE=1`
  landed (2026-05-12)** in
  [GpuBvhTree.cs](../../../../XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.cs).
  Adds `[GpuBvhTree][trace] ...` line on every overflow with full
  capacity-vs-required figures and a suspect classification
  ("stage-3 malformed tree" vs "real capacity exhaustion"). Default off.

  **Follow-up before re-enabling `XRE_CPU_HIZ_OCCLUSION` /
  `VisualScene3D.ShouldMaintainCpuGpuCommandMirror`:** capture B1 with
  `GpuIndirectInstrumented + GpuHiZ` and confirm the Occlusion panel
  shows non-zero `Occluded` from frame 2 onward (frame 1 is the
  passthrough); compare visible-mesh count against `CpuQueryAsync`
  baseline. Only after that parity is established is it safe to flip
  the CPU mirror gate back on.
- [x] **C-GPU-4**: Fix `GpuBvhTree` node capacity. The "primitives=52
  nodes=103 overflow" was originally blamed on `EnsureBuffers` being
  undersized for the SAH refine path. **Corrected hypothesis (2026-05-12,
  after C-GPU-3 trace audit):** SAH refine in
  [bvh_sah_refine.comp](../../../../Build/CommonAssets/Shaders/Scene3D/RenderPipeline/bvh_sah_refine.comp)
  rearranges child pointers but does NOT allocate new nodes, so
  `2N-1 = 103` is sufficient capacity for 52 primitives. The math:
  C# `EnsureBuffers` allocates `4 + nodeCount*20 = 2064` node scalars;
  shader's `maxNodesByBuffer = (2064-4)/20 = 103 = totalNodes`, so
  capacity ties exactly (not exceeded). The only remaining trigger for
  `OverflowBvhBit` was **stage-3 malformed-tree detection** in
  [bvh_build.comp](../../../../Build/CommonAssets/Shaders/Scene3D/RenderPipeline/bvh_build.comp)
  — a parent-pointer cycle from a stage-2 race on duplicate Morton
  codes (Karras tie-break in `lcp_leaf` resolves equal codes via
  `clz(i ^ j)` but the resulting overlapping internal-node ranges
  let two distinct internal nodes claim the same child, and stage 2's
  plain `parentIndex` store would then race).

  **Landed (2026-05-12):**
  1. **Capacity headroom** in
     [GpuBvhTree.EnsureBuffers](../../../../XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.cs#L301):
     allocate `nodeCount + 8` slots (~640 extra bytes) so the
     shader's boundary check has unambiguous slack. Frees `OverflowBvhBit`
     to mean exclusively "malformed tree" so the C-GPU-3 trace
     classification is now correct.
  2. **`atomicCompSwap` on `parentIndex`** in
     [bvh_build.comp stage 2](../../../../Build/CommonAssets/Shaders/Scene3D/RenderPipeline/bvh_build.comp):
     only the first writer succeeds (the child's parentIndex was
     initialized to `uint(-1)` at stages 0/1). The losing writer signals
     `OVERFLOW_BVH` explicitly. This deterministically prevents the
     parent-pointer cycle that previously caused stage 3 to detect a
     malformed tree.
  3. The existing suppress-loop (`_bvhBuildSuppressed` in
     [GPUScene.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene.cs#L4338))
     remains as the per-frame retry/log spam guard.

  Build validated clean. **Confirmed (2026-05-12)**: B1 run with
  `XRE_HIZ_CULL_TRACE=1` for ~2 minutes produced **zero**
  `[GpuBvhTree]` overflow lines and zero `[GpuBvhTree][trace]` lines
  (previous baseline: 5 overflows per session). Capacity headroom +
  stage-2 `atomicCompSwap` on `parentIndex` together eliminate the
  overflow source. C-GPU-4 closed; BVH overflow is no longer a
  candidate root cause for the C-GPU-3 undercount.
- [x] **C-GPU-5**: Cross-check the `GPURenderOcclusionHiZ.comp` LOD
  selection against the Darnell formula
  `LOD = ceil(log2(sphereWidthNDC * max(viewportWidth, viewportHeight)))`.
  Confirm it samples 4 NDC corners (not center+radius), accounts for
  perspective distortion via the tangent-radius adjustment Darnell
  documents, and uses the maximum of the 4 samples.
  **Audit (2026-05-12, partial)** —
  [GPURenderOcclusionHiZ.comp](../../../../Build/CommonAssets/Shaders/Compute/Occlusion/GPURenderOcclusionHiZ.comp):
  - `HiZOccluded` (primary path, sphere approximation): LOD formula is
    `floor(log2(maxDimPixels))`. Darnell's optimal is
    `ceil(log2(maxDim)) - 1`, which is identical or one LOD finer. Our
    `floor` is **one LOD coarser in some cases** → larger texel
    footprint → larger reported max-depth → under-cull (conservative,
    safe). Not a correctness bug; a small efficiency miss.
  - 4-corner sampling: `SampleHiZConservative` samples the rect's
    four UV corners (`t00`, `t01`, `t10`, `t11`) at the chosen mip
    and takes `max` (or `min` for reversed-Z). Matches Darnell. ✓
  - Perspective distortion: `HiZOccluded` uses a screen-space
    radius approximation (`rNdc = radius / clip.w`); Darnell's
    tangent-radius adjustment is **not present** in the sphere path.
    `HiZOccludedAabbRefine` (called only when sphere result is
    within `|nearestDepth - hiz| < 0.02`) does the proper 8-corner
    AABB→NDC projection, which sidesteps perspective entirely. ✓ for
    the borderline path, ✗ for the primary path.
  - Screen-edge handling: both paths return "visible (uncertain)"
    when `uvMin < 0 || uvMax > 1`. Robust impls clamp the rect to
    [0,1] and test the clipped region. Minor visibility-loss bug on
    large objects near the screen edge.
  - Depth-extent approximation for sphere (`depthDelta = rNdc * 0.5`)
    uses screen-space rather than view-space depth, understating the
    sphere's depth extent. Always conservative (under-culls).
  Decision: LOD formula and corner sampling are spec-compliant or
  strictly more conservative; no correctness bug. **Closed (2026-05-12)**
  as audited â€” every divergence from Darnell biases toward
  *under-culling* (visible-conservative), which is the safe direction
  and cannot explain the C-GPU-3 visible-mesh *undercount*. The
  follow-up efficiency wins (LOD off-by-one, sphere tangent-radius,
  edge clipping) are tracked as a deferred tier and are not on the
  critical path for the §1 fps target.
- [ ] **C-GPU-6**: Caster shadow culling via Darnell's HZB-shadows
  technique. Build a 2nd HZB from each shadow-casting light's POV; cull
  casters in light space; extrude bbox shadow volumes for non-culled
  casters and re-test against the player's HZB. Deferred until C-GPU-1..5
  stable. Largest correctness uplift on B2 once landed because the
  avatar-heavy scene cost is shadow-pass dominated.

  **Design subtasks** (added 2026-05-12, post P6):

  - **C-GPU-6.1 — Shadow caster command buffer.** Today shadow draws
    re-iterate the scene `RenderCommandCollection` filtered by pass; this
    survey ([Lights3DCollection.Shadows.cs](../../../../XREngine.Runtime.Rendering/Rendering/Lighting/Lights3DCollection.Shadows.cs),
    `ShadowAtlasManager.cs`) confirms there is **no separate indirect-draw
    buffer per light**. Before any HZB cull is useful, allocate a
    per-light (or per-cascade) indirect-draw buffer that mirrors the
    main pass's `_culledSceneToRenderBuffer` shape so the cull pass has
    somewhere to write its output. Reuse the existing
    `_culledSceneToRenderBuffer` allocator in
    [GPURenderPassCollection.CullingAndSoA.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.CullingAndSoA.cs).
  - **C-GPU-6.2 — Per-light HZB.** Add a `_lightHiZDepthPyramid[]`
    parallel to `_hiZDepthPyramid` in `GPURenderPassCollection.Occlusion`,
    keyed on shadow atlas page. Build via the existing
    `HiZGen.comp`/`GPURenderHiZInit.comp` pair (no new shader); source
    is the shadow atlas tile's depth texture instead of the main depth.
    Atlas-tile build runs **after** the shadow depth pass for that light
    has finished so the depth is committed.
  - **C-GPU-6.3 — Light-space caster cull.** Extend
    `GPURenderOcclusionHiZ.comp` (or fork to a `_shadowOcclusionProgram`)
    to accept the light-space VP matrix + light-space HZB and write into
    the per-light indirect buffer from C-GPU-6.1. Cull is a sphere/AABB
    test exactly like the main pass.
  - **C-GPU-6.4 — Shadow-volume extrude + player-HZB re-test.** For
    non-culled casters, extrude AABB along the light direction up to a
    cap distance, project the extruded volume corners into player space,
    sphere/AABB-test against `_hiZDepthPyramid`. Casters fully occluded
    in player space don't contribute visible shadows → drop them. This
    is a second compute pass that consumes the C-GPU-6.3 output and
    writes a refined indirect buffer.
  - **C-GPU-6.5 — Indirect shadow draw.** Replace the immediate-mode
    per-light render loop in `Lights3DCollection.Shadows.cs` /
    `ShadowAtlasManager.cs` with `glMultiDrawElementsIndirectCount`
    against the C-GPU-6.4 buffer. Gated on the
    `GpuIndirectZeroReadback` and `GpuIndirectInstrumented` paths;
    CpuDirect continues to use the existing immediate path (per §10.1
    rule 1).
  - **C-GPU-6.6 — B2 baseline measurement.** Re-run
    `Tools/Measure-MeshSubmissionBaselines.ps1` against the B2
    benchmark (Sponzas + 100 skinned avatars) — see §3 — to confirm
    the expected B2 win and rule out regressions on B1.

  **Order of attack**: 6.1 (buffer plumbing, no behavior change) → 6.2
  (HZB build, dead) → 6.3 (cull, output unused) → 6.5 (indirect draw,
  uses 6.3) → 6.4 (refine pass) → 6.6 (measure). Each step is
  independently mergeable; the buffer in 6.1 can default to the full
  unfiltered command list so shadows keep working at every intermediate
  stage.

  **Risks**:
  - Driver fastfail similar to the P6 FBO-attach bug if light-HZB
    textures are attached/sampled before storage commit — the P6 fix
    in `GLTexture<T>.AttachToFBO` covers all attaches, but watch for
    image-store binds and bindless sampler resolves which are separate.
  - Shadow atlas page reallocation during a frame would invalidate the
    cached HZB; gate HZB rebuild on `page.GenerationId`.
  - Extruding AABBs along the light direction needs care for
    directional lights with parallel-light-to-AABB axis (degenerate
    volume) — use the existing cascade frustum bounds as a fallback
    cap distance.
- [ ] **C-GPU-7** (optional, after C-GPU-6): Darnell-style dedicated
  occluder RT (512×256-ish). Artist-tagged occluder geometry only.
  Decouples Hi-Z cull from main pass ordering, removes "first frame
  after mutation" failure mode in C-GPU-3.2.
- [ ] **C-GPU-8**: GPU-resident overdraw debug drawer (parity with main
  pass under GPU occlusion modes). First parity fix: `VPRC_RenderFullOverdrawPass`
  resolves the actual active submission strategy; `CpuDirect` redraws the CPU
  mesh list, while GPU submission uses `RenderGPU(pass, strategy)` for
  GPU-eligible meshes and only re-traverses the CPU list for forced-CPU /
  `ExcludeFromGpuIndirect` fallback meshes. A tighter follow-up can bind the
  overdraw override material + count FBO and reissue `MultiDrawElementsIndirect`
  against the already-culled indirect-draw buffer instead of re-running GPU
  culling for the debug pass. Same shape as the GPU BVH debug drawer. Broader
  audit row: any **secondary CPU traversal** (wireframe, motion-vector debug,
  selection outline) can silently desync from GPU culling and needs the same
  treatment - either coordinator-peek + force CPU occlusion, or a GPU-resident
  sibling pass.

Acceptance: ZeroReadback B1 baseline lands within 20% of CpuDirect;
RenderDoc capture shows zero CPU buffer-map operations on the
`GpuIndirectZeroReadback` frame; `XRE_CPU_HIZ_OCCLUSION` flag stays
deleted.

### 10.4 What this means for the current code

| Component | Today | Target |
| --- | --- | --- |
| `PrepareCpuHiZVisibility` / `TryGetCpuHiZVisibility` | gated off by `XRE_CPU_HIZ_OCCLUSION` | DELETED (C-CPU-2) |
| Temporal occlusion hysteresis dict in `GPURenderPassCollection.Occlusion` | dormant | DELETED (C-CPU-2) |
| `_cpuHiZVisibleMeshCommands` / `_cpuHiZTemporalOcclusion` / `_cpuHiZPendingFence` | unused with flag off | DELETED (C-CPU-2) |
| `VisualScene3D.ShouldMaintainCpuGpuCommandMirror()` CpuDirect+GpuHiZ branch | gated by `XRE_CPU_HIZ_OCCLUSION` | DELETED, Surfel-GI branch keeps (C-CPU-2) |
| `Engine.Rendering.Settings.GpuOcclusionCullingMode` getter | env override only | explicit enum mode, no CpuDirect path-coercion (C-CPU-1) |
| `CpuRenderOcclusionCoordinator` | optional `CpuQueryAsync` mode | explicit `CpuQueryAsync` mode for CpuDirect |
| `GpuBvhTree.EnsureBuffers` overflow path | logs + non-BVH fallback | grow capacity, never silently drop commands (C-GPU-4) |
| `_hiZDepthPyramid` source | main depth attachment | unchanged for now; consider dedicated occluder RT (C-GPU-7) |
| `_culledSceneToRenderBuffer` consumers on CPU | several (snapshot, GPU debug, hybrid validator) | only `IndirectDebug.*` opt-in diagnostics (C-GPU-1) |
| First frame after scene mutation / camera jump | Hi-Z cull consumed pyramid built from stale depth → over-cull | refine bypassed for that pass, telemetry `GpuPassesPassthroughDirty` (C-GPU-3) |
| `GPURenderOcclusionHiZ.comp` LOD formula | unverified | matches Darnell formula (C-GPU-5) |
| Caster shadow HZB | not implemented | second HZB + shadow-volume re-test (C-GPU-6) |

### 10.5 Execution order

1. **C-GPU-1** (audit) — read-only, cheap, immediately informs everything else.
2. **C-CPU-1 + C-CPU-2** — small surgical changes, restores CpuDirect to a
   clean state and lets us delete a lot of code.
3. **C-GPU-3** — find and fix the underlying undercount. Capture artifacts
   land in the Findings Log.
4. **C-GPU-4** — size BVH correctly.
5. **C-GPU-5** — verify LOD math matches Darnell.
6. ✅ Re-measure all three baselines. Update §1 numbers. — done 2026-05-12;
   first attempt (Findings P5) recorded a ZeroReadback crash, root-caused
   and fixed under P6 (FBO-attach immutable-storage commit). Re-measured
   post-fix: all three strategies now within drop-event noise of each
   other on Debug B1 (CpuDirect 1595/60s, GpuIndirectInstrumented 353/25s,
   ZeroReadback 768/60s). Release re-measure still pending.
7. **C-DRP-1 + C-UPL-1 + C-CACHE-1 + C-MEAS-1** — Findings P8
   retracts P7. GpuHiZ has been silently no-op'd because the depth
   view exposed by the default pipeline is an `XRTexture2DView`/
   `XRTexture2DArrayView`, not the bare `XRTexture2D` the path
   requires. Real bottleneck is upload-queue spikes + cold shader
   deserialization on the render thread. **This is now the critical
   path**; everything below is gated on resolving it. ← **next**
8. **C-GPU-6** (shadow HZB) — deferred. Largest B2 win on paper but
   meaningless until C-DRP-1 lands; building a second HZB cull on top
   of a primary HZB that never even runs makes no sense.
8. **C-GPU-7** (dedicated occluder RT) only if §1 still misses target.
9. **C-CPU-3** (software occluder) only if hardware queries don't cull
   enough on B2 (avatar-heavy).
10. **C-GPU-8** (GPU-resident overdraw debug drawer) — fold in once
    GPU indirect-draw buffers are the canonical visibility set
    (post C-GPU-3..6). Temporary parity via `RenderGPU`; full sibling pass later.

## 11. Related Code

- [HybridRenderingManager.cs](../../../../XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs)
- [GPUScene.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene.cs)
- [RenderCommandCollection.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/RenderCommandCollection.cs)
- [GPURenderPassCollection.Occlusion.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.Occlusion.cs)
- [CpuRenderOcclusionCoordinator.cs](../../../../XREngine.Runtime.Rendering/Rendering/Occlusion/CpuRenderOcclusionCoordinator.cs)
- [VisualScene3D.cs](../../../../XREngine.Runtime.Rendering/Rendering/VisualScene3D.cs)
- [GpuBvhTree.cs](../../../../XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.cs)
- [VPRC_BuildAccelerationStructure.cs](../../../../XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_BuildAccelerationStructure.cs)
- [Engine.Rendering.Settings.cs](../../../../XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs)
- [docs/architecture/rendering/mesh-submission-strategies.md](../../../architecture/rendering/mesh-submission-strategies.md)
