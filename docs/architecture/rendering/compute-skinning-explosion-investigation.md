# Compute Skinning "Explosion" — Investigation & Mitigations

Status: **Root cause identified and fix confirmed** across repeated cold starts.
Branch: `gpu-skinning-buffer-compression`.

> **Confirmed (2026-05-31).** Over 3 cold starts post-fix, the output-residency
> diagnostic (`NO-GPU-DATA` / `UNDER-ALLOCATED`) dropped to **0** (was 40× in the
> repro run) and no geometry rendered missing. A small number of `EXPLODED`
> readbacks still appear, but **only** on mid-settle dispatches
> (`reseed#1 settled=False`); they self-correct on the next dispatch
> (`reseed#2 settled=True`, bounds matching the authoritative mesh) via the §5.2
> pose-settle re-seed and are never latched by the output cache — i.e. they are a
> benign transient, not a visible defect.

This document records a hard-to-reproduce GPU compute-skinning defect, the
evidence-driven investigation behind it, every mitigation implemented, and the
diagnostics armed along the way.

> **Symptom evolved.** It first presented as meshes rendering **"exploded"**
> (vertices flung outward). After the input-residency + pose-settle mitigations
> (§5.1–5.2), the residual symptom changed to **missing triangles / entire
> missing meshes**. Both turned out to be downstream consequences of the same
> class of bug: a **GPU buffer not being resident when the compute dispatch
> needs it**, with the result frozen by the output-reuse cache. The exploded
> form came from stale/garbage **input** buffers; the missing form came from an
> unallocated **output** buffer (§3.1).

---

## 1. Symptom

With compute skinning enabled, a **subset** of skinned meshes occasionally render
wrong immediately at load — originally **"exploded"** (vertices flung far outside
the mesh's real bounds), later **"missing"** (whole mesh invisible, or missing
triangles). The defining behavioral signature is identical in both forms:

- The mesh is wrong **from the first frame it appears**.
- It **stays wrong indefinitely**.
- **Manually moving any bone** on that mesh's skeleton **fixes it permanently.**

That "manual bone move fixes it" property is the single most important clue: it
means the *source data* is fundamentally correct, and the corruption is a **stale
cached compute result** that only gets recomputed when a bone-transform change
event forces a re-dispatch.

The bug is **intermittent** — it depends on load timing / GPU upload ordering and
does not reproduce on every run.

---

## 2. Affected subsystem

The compute-skinning prepass runs once per visible skinned renderer per frame:

- `XREngine.Runtime.Rendering/Rendering/Compute/SkinningPrepassDispatcher.cs`
  - `Run(renderer)` — per-renderer dispatch entry point.
  - `RendererResources` — per-renderer GPU state + the **output-reuse cache**.
- `XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.cs`
  - Bone palette construction (`PopulateBoneMatrixBuffers`, `ComposeSkinPaletteMatrix`).
  - Dirty/clean signaling (`MarkSkinnedOutputDirty`, `BoneTransformRenderMatrixChanged`).
- `XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.Skinning.cs`
  - Skinning buffer (re)build from authoritative `Vertices[]`.
- `Build/CommonAssets/Shaders/Compute/Animation/SkinningPrepass.comp`
  - `p' = palette · vec4(p, 1)` per vertex (3 dot products against a 3×4 palette).

The skinning math was **verified equivalent** to the pre-compression code and is
**not** the cause (see §4 "Ruled out").

---

## 3. Root mechanism (why it stays exploded until a bone moves)

The `gpu-skinning-buffer-compression` branch introduced an **output-reuse cache**
to skip redundant compute dispatches (commit `9f5c069a`, "More improvements to
skinning"):

```
RendererResources.CanReuseOutput(...)   // returns true -> skip dispatch, reuse last output
RendererResources.MarkOutputValid(...)  // latches _hasValidOutput = true after a dispatch
```

`CanReuseOutput` returns `true` (skip work) unless one of these trips:

- `!_hasValidOutput` (never dispatched yet)
- `_renderer.SkinnedOutputDirty`
- skinning/blendshape mode changed
- `_renderer.HasPendingComputeSkinningInputChanges`
- output version bumped
- external/GPU-driven palette source

For a **static (non-animated) mesh**, after the first dispatch *none of these ever
trip again*. So the cache freezes whatever the first dispatch produced.

The two ways a **first** dispatch can produce garbage:

1. **Input-residency race.** Compute INPUT buffers (vertex positions, bone
   core indices/weights, spill, palette, blendshape source) upload to the GPU
   **asynchronously** over multiple frames. The dispatch binds them with
   `SetBlockIndex`, which — unlike `BindSSBO` — does **not** force the pending
   upload to complete. So the first dispatch can read **not-yet-uploaded GPU
   memory** (garbage indices/positions) → garbage output.

2. **Pose-settle race.** A runtime-imported avatar publishes several frames of
   **intermediate** bone poses at startup. The initial palette is seeded in the
   renderer constructor (`PopulateBoneMatrixBuffers`), which can run **before**
   the bone transforms publish their final render matrices. Stale bones are only
   corrected by `RenderMatrixChanged` events; if this renderer subscribed *after*
   the final pose was already published (init-order race), no further event fires.

In both cases the bad first dispatch is **latched by `MarkOutputValid`** and
reused forever. Manually moving a bone fires
`XRMeshRenderer.BoneTransformRenderMatrixChanged` →
`MarkBoneMatrixDirty + MarkSkinnedOutputDirty` → `CanReuseOutput` returns false →
re-dispatch with now-resident inputs and final pose → **correct output**. This
exactly matches the observed "move a bone to fix it" behavior.

**The cache converts a transient first-frame glitch into a permanent explosion.**

### 3.1 Output-buffer race (the "missing geometry" form)

A **third** residency race — on the **output** buffer — is the confirmed cause of
the *missing* symptom, and it is the one with hard evidence:

- The skinned output buffer (`XRMeshRenderer.SkinnedPositionsBuffer`, SSBO
  binding 11) is created **client-side** in `EnsureOutputBuffers`, but its **GPU
  storage allocates lazily** — normally not until the **draw** binds it, which
  happens **after** the compute dispatch.
- The dispatch binds the output via `SetBlockIndex(11)`, which (exactly like the
  inputs) does **not** force storage allocation.
- So the compute shader writes its skinned positions into a **zero-byte GPU
  buffer** → the results are discarded. The subsequent draw then allocates the
  storage initialized to the buffer's **zeroed client data** → every vertex reads
  `(0,0,0)` → the mesh collapses to a **degenerate point**:
  - **Whole buffer unallocated** → entire mesh **missing**.
  - **Partially allocated** (`gpuBytes < expectedBytes`, "UNDER-ALLOCATED") →
    **missing triangles**.
- The output-reuse cache latches after the first dispatch, so the mesh **stays
  missing** until a bone move forces a re-dispatch (by which point the storage
  has been allocated) — matching the "move a bone to fix it" behavior.

**Direct evidence:** the `[SkinReadback]` diagnostic reported
`*** NO-GPU-DATA *** gpuBytes=0 expectedBytes=N` on the **output** buffer **40
times** in the repro run (pid34224), with the palette reading 100% identity.

> The input-residency mitigation (§5.1) **deliberately excluded** the output
> buffers ("they are GPU-written and must not be re-uploaded"), which is why
> fixing the input race changed the symptom from *exploded* to *missing* rather
> than eliminating it. §5.4 closes this remaining gap.

---

## 4. Hypotheses ruled out (with evidence)

The investigation chased and **disproved** several plausible-but-wrong causes.
Recording them so they are not re-investigated:

| Hypothesis | Why it was rejected |
|---|---|
| Per-vertex core index packing wrong | 1-based via `BoneIndex+1`, matches shader (slot 0 reserved). Correct. |
| Palette math changed by compression | `rootBindMtx · invBind · current`, mathematically identical pre/post compression. |
| `SkinPaletteMatrix.FromRowVectorMatrix` decode | Verified correct row-vector decode. |
| Identity palette at load is the bug | An all-identity palette **at bind pose is correct** (`boneWorld · invBind = I`, no deformation). |
| Source vertex positions corrupted | GPU input `bind8` == CPU `PositionsBuffer` == authoritative `Vertices[]` (`cpu0 == vert0` on **every** mesh). No corruption. |
| `BindRootMatrix` / root placement double-transform | `BindRootMatrix` is intentionally null for weighted meshes; root probe showed `rootWorldT=(0,0,0)` while `composedT≠0` — disproving the residual-root theory. |
| `verts=12852` mesh is "the exploding one" | **False.** Its large ~86-unit span **is its real geometry**, confirmed by its own authoritative `Vertices[]`. Output span ≈ authoritative span (ratio ≈ 0.96). |

**Critical lesson:** absolute bounds size does **not** indicate an explosion. A
large mesh has large legitimate bounds. The only reliable explosion signal is
**skinned-output span ≫ authoritative `Vertices[]` span**.

---

## 5. Mitigations implemented

All mitigations live in `SkinningPrepassDispatcher.cs` unless noted.

### 5.1 Force input residency before every dispatch
`RendererResources.EnsureSkinningInputsResident(...)` runs **before** binding and
dispatching. It forces all **read-only** input buffers (positions/normals/tangents
or interleaved, bone core indices/weights, spill headers/entries, blendshape
source, palette) fully GPU-resident — mirroring the `BindSSBO` guard that
`SetBlockIndex` skips. **Output buffers are deliberately excluded** (they are
GPU-written and must not be re-uploaded). This closes race #1.

### 5.2 Re-seed the palette until inputs are resident AND pose is stable
`RendererResources.EnsureSeededFromRenderState(...)` re-pushes **all** bone
matrices from the current render state (`RefreshBoneMatricesFromRenderState`,
which also marks the output dirty so it recomputes) on **every** dispatch until
**both**:

- `inputsResident` — inputs observed **naturally** resident
  (`AreSkinningInputsNaturallyResident`, which checks readiness **without** forcing
  the upload), and
- `poseStable` — `ComputeCurrentBonePoseHash()` is unchanged across two
  consecutive frames.

Only when `_seedInputsSettled = inputsResident && poseStable` does the output
cache get to hold. This deterministically captures the **final settled pose with
final input data**, closing race #2 without dispatching forever. Emits a one-shot
`[SkinSettle]` log when it settles.

### 5.3 Cache invalidation completeness
`CanReuseOutput` was hardened to bail on `SkinnedOutputDirty`, skinning/blendshape
mode change, `HasPendingComputeSkinningInputChanges`, output-version mismatch, and
external/GPU-driven palette sources — so any legitimate input change forces a
re-dispatch rather than serving stale output.

### 5.4 Force output-buffer storage resident before dispatch (the missing-geometry fix)
`RendererResources.EnsureSkinningOutputResident(isInterleaved, doSkinning)` is
called in `Run(...)` immediately after `EnsureSkinningInputsResident`, **before**
the dispatch. It runs `EnsureBufferResident` on the skinned **output** buffers
(`SkinnedPositionsBuffer` / `SkinnedNormalsBuffer` / `SkinnedTangentsBuffer`, or
`SkinnedInterleavedBuffer`), forcing their GPU storage allocated so the compute
shader writes into **real** storage that the draw then reads.

This is safe to call every dispatch: `GLDataBuffer.EnsureStorageAllocatedForGpuCopy`
only pushes client data while `_lastPushedLength < Data.Length`, so it **allocates
once** (with the zeroed client data) and then becomes a **no-op** — it can never
re-push and therefore never clobbers the compute-written results. This directly
closes the §3.1 output-residency race.

> 5.1 + 5.2 closed the **input**/pose races (turning *exploded* into *missing*);
> 5.4 closes the **output** race (the *missing* form). Together they address all
> three identified residency races. **Confirmed:** across 3 post-fix cold starts
> the `[SkinReadback]` diagnostic reported **zero** `NO-GPU-DATA` /
> `UNDER-ALLOCATED` lines and no missing geometry. Residual `EXPLODED` lines occur
> only on mid-settle dispatches (`settled=False`) and self-correct on the next
> settled dispatch — a benign transient, not a latched defect.

---

## 6. Diagnostics currently armed

These exist to **capture a definitive repro**, not as fixes. They should be
removed once the root cause is confirmed fixed.

- **`[SkinReadback]`** (`DebugReadbackSkinnedOutput`) — reads the **actual**
  compute-skinned output back from the GPU after a dispatch and compares it
  against: the GPU input positions (`bind8`), the CPU `PositionsBuffer` (`cpu0`),
  and the **authoritative** `mesh.Vertices[]` bounds (`vertX/Y/Z`).
  - **Missing-geometry flag:** `*** NO-GPU-DATA *** gpuBytes=0` (output buffer
    unallocated) and `UNDER-ALLOCATED!` (`gpuBytes < expectedBytes`) — the direct
    signals of the §3.1 race. These should drop to zero after §5.4.
  - **Reliable explosion flag:** emits
    `*** EXPLODED outVsAuth ratio=N ... settled=... reseed#... ***` when the
    skinned **output span exceeds the authoritative source span by > 2.5×**.
    This replaced an earlier `> 50` absolute-translation trigger that
    false-positived on legitimate mid-animation poses.
- **`[SkinSettle]`** — logs when a renderer's seed/input state settles, with the
  reseed and pose-change counts.
- **`[SkinPaletteGpu]`** (`DebugReadbackSkinPalette`) — reads the GPU palette at
  SSBO binding 0; flags an all-identity (passthrough) palette.
- **`[SkinPaletteOk]` / `[SkinPaletteStale]`** (`VerifyBonePaletteOrderMatchesMesh`)
  — confirms bone palette order matches the mesh (drift detector).

### Detector validation
Across two consecutive clean runs the detector evaluated every visible mesh:

| Run | Readbacks | `EXPLODED` | Worst output/authoritative ratio |
|---|---|---|---|
| pid21964 | (all meshes) | 0 | < 1.5 |
| pid39396 | 98 | 0 | **1.0** (most < 1; posed bbox ≤ rest span) |

A posed mesh's bounding box is typically **smaller** than its rest-pose span, so
ratios at or below 1.0 are expected and healthy. Nothing approached the 2.5×
threshold.

---

## 7. How to reproduce / what we still need

The explosion is timing-dependent. Most likely to provoke it:

1. **Cold start / first load** — the input-upload race window is widest the instant
   a mesh pops in. Observe meshes *as they appear*, not after the scene settles.
2. **Heavy simultaneous load** — many skinned meshes (or other GPU upload pressure)
   delays input residency and widens the window.
3. **Static skeletons** — meshes whose bones never animate cannot self-correct (no
   `MarkSkinnedOutputDirty` ever fires), so they are the most likely to *stay*
   exploded.

When a visible explosion does occur, the `[SkinReadback] *** EXPLODED ***` line
captures the mesh `verts`, `settled=`, and `reseed#` at that exact moment. That
tells us definitively whether the bad dispatch ran **before** `_seedInputsSettled`
became true — the missing evidence needed to commit a confirmed root-cause fix
instead of an inferred one.

---

## 8. Cleanup checklist (after confirmed fix)

Remove the diagnostics (keep the confirmed mitigations 5.1–5.3):

- `DebugReadbackSkinnedOutput` / `[SkinReadback]` (incl. authoritative-bounds +
  EXPLODED flag)
- `DebugReadbackSkinPalette` / `[SkinPaletteGpu]`
- `VerifyBonePaletteOrderMatchesMesh` / `[SkinPaletteOk]` / `[SkinPaletteStale]`
- `[SkinSettle]`, `[SkinSeed]`, `[SkinResidency]` logging
- `DetectSkinPaletteExplosion` / `[SkinExplode]` (the disproven root-world probe)

Keep the zero-influence `[Skinning]` diagnostic in `XRMesh.Skinning.cs`.

---

## 9. Reference timeline (commits)

- `5493dd67` — `origin/master` base (pre-compression; no cache, no bug).
- `36b2a8e1` — "Implement skinning compression" (3×4 palette packing).
- `9f5c069a` — "More improvements to skinning" — **added the output-reuse cache**
  (`CanReuseOutput` / `_hasValidOutput`); this is the mechanism that freezes a bad
  first dispatch.
- `6cb182b3` / `77b1d8ef` — subsequent skinning fixes + the mitigations and
  diagnostics described above.
