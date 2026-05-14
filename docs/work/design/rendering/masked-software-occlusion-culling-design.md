# Masked Software Occlusion Culling (Masked SOC) — XRENGINE Design

## Status

- Author: rendering team
- Implementation state: opt-in scalar SOC is implemented for traditional CPU mesh rendering and meshlet command visibility. Rectangle tests now use the masked tile data instead of a per-pixel scan. `CpuSocUseAvx2` is exposed as the planned SIMD selector; current correctness uses the scalar path.
- Status: Phase A implemented; AVX2, stereo buffers, and promotion validation remain tracked in the TODO.
- Supersedes the C-CPU-3 scaffold in [render-submission-perf-debug-plan.md](render-submission-perf-debug-plan.md). Existing scaffold (`CpuSoftwareOcclusionCuller`, telemetry counters, `CpuSoftwareOcclusion` mode plus the legacy `EnableCpuSoftwareOcclusionCulling` toggle) is repurposed; no public API is removed.
- Implementation TODO:
  [masked-software-occlusion-culling-todo.md](../../todo/rendering/masked-software-occlusion-culling-todo.md).

## Motivation — Why a Software Rasterizer

### The diagnostic that made this necessary

Instrumented the CPU-query occlusion path with per-decision distribution
counters. Two snapshots of the same scene (forward Sponza + deferred Sponza,
identical camera) with each acting as the "front" occluder:

|                  | Deferred in front | Forward in front |
| ---------------- | ----------------- | ---------------- |
| Tested           | 77                | 77               |
| Visible(query)   | **10 (13%)**      | **6 (8%)**       |
| Skip(occluded)   | 40 (53%)          | 29 (38%)         |
| Probe(depth)     | 0                 | 15 (20%)         |
| Rendered         | 16                | 8                |

The system is healthy: most commands cull, hysteresis carryover is 0, and
prepass/color share decisions via cache (33% Cached). What's left is
`Visible(query)`: 10 (resp. 6) commands whose hardware
`GL_ANY_SAMPLES_PASSED_CONSERVATIVE` query honestly reports samples-passed.

Those are real fragments passing the depth test. Sponza is an open building
(arches, windows, an open nave). Back-Sponza geometry projects through
front-Sponza's openings; the per-fragment depth test kills the *pixels*
behind solid walls, but each back-Sponza mesh's AABB still has *some* visible
pixels, and the hardware query is a boolean. We can never get below
"at least one fragment of this mesh is visible ⇒ Visible" with per-mesh
hardware queries on coarse render commands.

### Why hardware queries can't fix this

1. **Boolean granularity.** `AnySamplesPassedConservative` is "≥1 sample"
   not "fraction of samples." A 99%-occluded mesh and a 1%-occluded mesh
   look identical to the query.
2. **Self-reporting.** The query tests fragments of the mesh we already
   submitted. We've already paid command-buffer cost, descriptor binding,
   pipeline state validation, and driver translation by the time the GPU
   answers.
3. **One-frame latency, minimum.** Even with `AsyncOcclusionQueryManager`,
   the result arrives 1–3 frames after we submitted, so the decision we
   make this frame is based on stale visibility.
4. **No AABB-only mode.** OpenGL/Vulkan don't expose "test this AABB
   against current depth without rasterizing geometry." The depth-only
   AABB proxy we use for ProbeOnly is a hack — it still goes through full
   vertex/setup/rasterization on the GPU.

### Why splitting meshes isn't sufficient on its own

Splitting Sponza into per-arch render commands would help, but:

- It's an authoring/import-time change, doesn't help arbitrary user content.
- It multiplies command count by 10–100×, increasing the per-frame
  iteration cost of every other system (sort, frustum, decisions, dispatch).
- Even with fine commands, each command's AABB still over-conservatively
  captures empty space around the arch.
- The hardware-query path's worst case is unchanged: every command needs
  its own GL query begin/end pair around the draw — pure latency cost.

Mesh splitting *complements* SOC; SOC works regardless of granularity.

### Why a CPU rasterizer specifically

Conservative tiled depth rasterization on the CPU gives us things the GPU
hardware queries cannot:

1. **Answers this frame, before draw submission.** No round-trip latency.
   We decide before issuing any commands, so a culled mesh costs ~0 (no
   command buffer entry, no driver overhead, no GPU work, not even an
   AABB proxy).
2. **AABB testing, no real rasterization needed for tests.** We rasterize
   *occluders* (large opaque triangles) once. Testing 10000 mesh AABBs
   against the resulting buffer is just transform + compare, no draw.
3. **Arbitrary occluder selection.** We pick what counts as an occluder
   per frame (largest projected area, in front of camera, opaque). We
   can also use simplified proxy hulls for high-poly content.
4. **No state machine, no driver involvement.** The whole thing is plain
   .NET (with SIMD intrinsics in the hot path). Easy to profile, easy to
   thread.
5. **Industry-standard answer.** Frostbite (Andersson & Hasselgren 2016,
   "Masked Software Occlusion Culling"), id Tech 7 (Doom Eternal),
   Crytek's coverage buffer, Unity Burst Occlusion Culling — all use a
   tiled, conservative-rasterizer-on-CPU design for exactly this case.
   The reference Intel implementation is open source
   (Apache-2.0; <https://github.com/GameTechDev/MaskedOcclusionCulling>).

### Non-goals

- Not replacing hardware queries. Hardware queries continue to handle:
  fine-grained leakage cases (AABB-tight meshes the SOC pass conservatively
  marks visible), shadow passes (currently disabled there too), and
  transparent / non-standard-depth passes.
- Not replacing GPU Hi-Z. GPU indirect / Hi-Z keeps its role in the GPU
  submission strategies. Current SOC scope is traditional `CpuDirect`
  draws plus meshlet command visibility; non-meshlet GPU indirect
  zero-readback remains GPU-culling-owned unless a future SSBO mask is
  added to its compute cull path.
- Not a "perfect" rasterizer. Conservative, tile-aligned, no
  perspective-correct attribute interpolation — only depth, only inside
  bounds, only with rounding that errs toward "visible."

### Research pass corrections

This pass validated the design against the Intel reference implementation,
the HPG 2016 paper, and the current XRENGINE render-command code. The
architecture remains valid, with these corrections now treated as hard
implementation constraints:

- The stored masked Hi-Z tile is **8 x 4 pixels**: two reciprocal-depth
  values plus a 32-bit coverage/layer mask. The reference also has
  **32 x 8** SIMD/bin/scissor granularities; that is not the stored tile.
- `CpuSoftwareOcclusionCuller` already exists as a public sealed facade
  with instance methods and a static `IsEnabled` gate. Extend that facade
  rather than replacing it with a static-only API.
- Visibility tests need the command's `StableQueryKey`; otherwise selected
  occluders cannot be exempted from self-occlusion.
- The pass stays opt-in until scalar correctness, AVX2 parity, editor
  validation, and Sponza capture acceptance pass. The current scaffold and
  settings default to disabled.
- Occluder rasterization must use render-thread snapshots of mesh, material,
  and model matrix data. Reading app-thread mutation properties from the
  render loop would make the software buffer disagree with the draw.
- Imported mesh data must expose a compact CPU position/index snapshot for
  SOC. Do not call GPU buffer build/read helpers or allocate transient
  triangle/index arrays from the render hot path.
- Skinned, morph/deformed, transparent, alpha-tested/masked, depth-disabled,
  and non-standard-depth materials are visible-conservative by default
  until an explicit SOC-safe proxy path exists.
- If Apache-2.0 source is ported instead of reimplemented from the paper,
  source headers and license attribution must be preserved.

## Performance Targets

| Metric                          | Budget                                       |
| ------------------------------- | -------------------------------------------- |
| SOC pass total CPU time / frame | ≤ 0.6 ms on Ryzen 7950X3D (single thread)    |
| AVX2-enabled hot loop           | ≤ 0.15 ms                                    |
| Occluder rasterization (per tri)| ≤ 1 µs scalar / ≤ 0.1 µs AVX2                |
| AABB test                       | ≤ 50 ns scalar / ≤ 10 ns AVX2                |
| Max occluder triangles / frame  | 5000 default (tunable; budget enforced)      |
| Max test queries / frame        | 50000 (every CPU render command)             |
| Buffer resolution               | 256 × 128 (configurable; W%8==0, H%4==0)     |
| Memory footprint                | < 256 KB resident                            |

These are validation targets, not hard contracts. The reference Intel
implementation reports ~0.5 ms / frame for an Atrium-style scene at
1920×1080 (paper §7) with AVX2 single-threaded; our 256×128 target is
~13× cheaper to rasterize. Holding 0.6 ms is conservative.

### Conventions and units

Aligned with the Intel reference:

- **Depth representation:** `1/w` (reciprocal of clip-space w).
  Larger value = closer to camera. Compares are reversed from a
  conventional z-buffer (`>=` for "closer than").
- **Buffer resolution:** width must be a multiple of 8, height a
  multiple of 4 (subtile mask grid). Default 256 × 128 satisfies both.
- **Screen-space Y axis:** D3D convention (Y-down) to match the
  reference's `USE_D3D=1` default. XRENGINE's existing screen-space
  conventions accept this; rasterizer Y-flip happens once during
  AABB/triangle viewport mapping.
- **Backface winding:** CW = backfacing (D3D convention; matches the
  reference's `BACKFACE_CW` default). Configurable per-occluder.
- **Coordinate input to the rasterizer:** clip-space `(x, y, w)`
  triplets. Clip-space z is ignored.

## Architecture Overview

### Where it fits

```
RenderCommandCollection.RenderCPU(pass, camera)
    └── if useCpuQueryOcclusion (pass ∈ opaque set):
        ├── Phase 0:  SOC.BeginFrame(camera, viewport)             [NEW]
        ├── Phase 0a: SOC.SelectAndSubmitOccluders(commandList)     [NEW]
        │              (scan, score, rasterize)
        ├── Phase 1:  for each cmd: prefilter via SOC.TestVisible   [NEW]
        │              if SOC says occluded → decision = Skip
        │              else fall through to hardware-query path
        ├── Phase 2:  visible draws (unchanged)
        └── Phase 3:  deferred probes (unchanged)
```

The SOC pass runs **before** the hardware-query decision. SOC is a more
aggressive cull: if SOC says "behind", we trust it and skip both the
draw and the hardware query. If SOC says "visible", the hardware-query
path takes over with its existing logic.

This composition is conservative-correct because:

- SOC's "visible" answer is upper-bounded (it can over-report visible).
- SOC's "occluded" answer is lower-bounded (no false positives — we never
  cull something that's actually visible, modulo floating-point + rounding
  guards documented below).
- Hardware queries run only on SOC's "visible" set, refining further.

### Subsystem boundaries

```
XREngine.Runtime.Rendering/
  Rendering/Occlusion/
    Soc/                                            [NEW namespace]
      MaskedOcclusionBuffer.cs        // The tiled depth buffer + ops
      MaskedOcclusionRasterizer.cs    // Scalar + AVX2 triangle raster
      MaskedOcclusionAabbTester.cs    // AABB → screen rect → depth compare
      OccluderSelector.cs             // Per-frame occluder selection
      CpuSoftwareOcclusionCuller.cs   // Public facade
      ESoftwareOccluderClassification.cs
      MaskedOcclusionTelemetry.cs     // Extension of OcclusionTelemetry
  Rendering/Commands/
    RenderCommandCollection.cs        // SOC integration point
```

`CpuSoftwareOcclusionCuller` is the only externally visible type; everything
under `Soc/` is `internal`. The facade exposes:

```csharp
public sealed class CpuSoftwareOcclusionCuller
{
    public static bool IsEnabled { get; }

    public bool IsFrameOpen { get; }
    public bool IsFrameInitializedFor(XRCamera camera, int viewportW, int viewportH);
    public void BeginFrame(XRCamera camera, int viewportW, int viewportH);
    public void SubmitOccludersFromOpaqueCommands(RenderCommandCollection commands);
    public bool TestVisible(uint stableQueryKey, in AABB worldBounds);

    // Compatibility/debug helper for callers that do not have a command key.
    public bool TestVisible(in AABB worldBounds);

    public MaskedOcclusionBufferReadback? DebugReadback { get; }
}
```

`RenderCommandCollection` owns a singleton culler next to
`s_cpuOcclusionCoordinator`. `BeginFrame` is called lazily the first time an
occlusion-testable CPU pass sees a new `(RenderFrameId, camera, viewport)`
tuple. No explicit `EndFrame` is required for correctness; the next
`BeginFrame` clears or resizes the buffer and resets selected-occluder keys.
`TestVisible(stableQueryKey, worldBounds)` is the only call from the
per-command inner loop.

## Data Structures

### `MaskedOcclusionBuffer`

Tiled, two-layer **hierarchical depth buffer** per Intel's Masked SOC.
Crucial implementation detail per the reference: the buffer stores
**depth = 1/w** (reciprocal of clip-space w), *not* z. Larger = closer.
The `z` clip-space coordinate is ignored entirely. We feed (x, y, w)
triplets to the rasterizer.

Per tile we store:

```
struct Tile
{
    uint   Mask;                  // 32 bits, one per sample in an 8 x 4 tile
    float  ZMin0;                 // reference layer reciprocal depth
    float  ZMin1;                 // working layer reciprocal depth
}
```

Stored tile size: **8 x 4 pixels**. The rasterizer may process larger
**32 x 8** SIMD/bin/scissor regions, but those are traversal grains, not
the stored `Tile`. The buffer's resolution **width must be a multiple of
8, height a multiple of 4** (matching the reference API requirement).

At 256 x 128 -> 32 x 32 = **1024 tiles**, total 1024 x (4 + 4 + 4) =
**12 KB** before padding/alignment. Doubled for stereo. Per-tile cache
locality is excellent because the binning rasterizer iterates
tile-row-major.

The **two-layer scheme** is the *masked* part: when an occluder fragment
lands in a tile, instead of immediately merging into a single far-layer
depth, the fragment is accumulated in `ZMin1` with `Mask` flagging which
subtile slots were covered. When the tile fills (`Mask == 0xFFFFFFFF`) or
the heuristic decides to merge, `Mask` is reset and `ZMin0` is updated.
Two algorithms exist for the merge heuristic (reference defines them with
`QUICK_MASK`): the speed-focused paper algorithm (default) and a more
accurate heuristic ("Masked Depth Culling for Graphics Hardware",
Andersson et al.). We default to `QUICK_MASK` and expose an option for
the accurate heuristic if leakage becomes a problem.

### `OccluderCandidate`

```csharp
internal readonly struct OccluderCandidate
{
    public readonly RenderCommand Command;
    public readonly Matrix4x4 ModelMatrix;
    public readonly XRMesh Mesh;
    public readonly float ScreenAreaEstimate;     // Sort key
    public readonly float NearDepth;              // Filter (closer = better)
    public readonly int TriangleCount;
}
```

Selection produces a `Span<OccluderCandidate>` per frame, sorted by
`ScreenAreaEstimate` descending. The selector walks the opaque-deferred
+ opaque-forward command lists once.

### `MaskedOcclusionBufferReadback`

Debug-only RGBA8 readback of the buffer's `ZMin0` layer, exposed to
the ImGui occlusion panel for "what the SOC sees" overlay. Allocated
only when `Engine.EffectiveSettings.CpuSocDebugVisualization` is on.

## Algorithms

### Occluder selection (`OccluderSelector`)

Automatic where the command is SOC-safe, with opt-in proxy support for
content that needs hand-authored occluders. Per frame:

1. **Frustum filter.** Reject candidates whose AABB doesn't intersect the
   frustum. Use existing frustum test from `XRCamera`.
2. **Near filter.** Reject candidates that cross or sit behind the near
   plane. Near-plane clipping belongs in the rasterizer, but near-straddling
   occluders are not worth spending the default occluder budget on.
3. **Depth/material filter.** Reject candidates whose material has
   `RenderingParameters.HasBlending`, `ExcludeFromCpuOcclusion`, disabled
   depth testing, `DepthTest.UpdateDepth == false`, or render pass in
   {TransparentForward, WeightedBlendedOitForward, OnTopForward}. Masked /
   alpha-test passes are rejected by default; a future
   `CanActAsCpuOccluder` or proxy-occluder flag may opt them in only when
   the depth written by the material is conservative.
4. **Geometry filter.** Reject non-triangle meshes, skinned/morph/deformed
   meshes, and meshes without CPU-side position/index snapshots. Rigid
   transform animation is allowed because the current render-snapshot model
   matrix is applied during SOC vertex transform.
5. **Triangle budget.** Estimate per-mesh triangle count from
   `XRMesh.Triangles?.Count` (or the compact SOC index snapshot). Do not
   build or read GPU index buffers from this path.
6. **Screen-area scoring.** Project AABB to NDC, compute screen-space area
   (or 0 if behind camera), score = area / sqrt(tri_count). Penalizing
   tri count picks "big simple walls" over "tessellated cathedrals."
7. **Selection.** Greedy top-N by score until either the occluder
   triangle budget is met (5000 tris default) or N is reached
   (`MaxOccluders` default 64).
8. **Output.** A `Span<OccluderCandidate>` sorted near→far. We rasterize
   near occluders first so far-occluder fragments are quickly rejected
   against an already-populated buffer.

Memory: selector uses a single instance-field `List<OccluderCandidate>`
cleared each frame. No per-frame allocation in steady state.

### Rasterization (`MaskedOcclusionRasterizer`)

Per occluder, per triangle:

1. **Vertex transform.** `position_world = ModelMatrix * vertex; clip = ViewProj * position_world`.
   Output is clip-space `(x, y, z, w)` but we **only retain (x, y, w)** —
   z is unused (reference uses `depth = 1/w`). Scalar path uses
   `Vector4.Transform`; AVX2 path transforms 8 vertices in parallel via
   `Vector256<float>`.
2. **Clipping.** Reject triangle if entirely outside any clip plane. If
   straddling near plane (w ≤ near), clip against near (only — far is
   fine because 1/w → 0). We expose per-call clip-plane disable flags
   matching the reference's `CLIP_PLANE_NONE` / `CLIP_PLANE_ALL` so
   trusted-clean meshes can skip clip work.
3. **Perspective divide + viewport mapping.** `(x', y') = (x/w, y/w)`,
   map to integer pixel coords. Snap to 8×4 subtile grid with `floor`
   for min and `ceil` for max (conservative coverage).
4. **Backface cull.** Compute signed screen-space area. **Default
   convention matches the reference: CW = backfacing → cull.** This is
   the DirectX/D3D convention; the reference's `USE_D3D` define is on
   by default. XRENGINE uses left-handed/D3D winding, so this aligns.
   Per-occluder override exposed via `RenderingParameters.CullMode`.
5. **Edge function setup.** Standard half-plane edge functions evaluated
   in fixed-point integer arithmetic. AVX2 path evaluates 8 pixels'
   edge values per cycle using `Vector256<int>` adds.
6. **Per-pixel `depth = 1/w` interpolation.** Reciprocal w is linear in
   screen space (this is the key insight enabling the cheap algorithm),
   so we interpolate `1/w` directly using the edge gradients — no
   perspective correction needed for depth.
7. **Tile loop / binning.** For each 8 x 4 stored tile the triangle's
   bounding rect overlaps:
   - Evaluate edge functions for all 32 tile samples. The AVX2 path may
     group work through 32 x 8 traversal bins, then update the underlying
     8 x 4 tiles.
   - Coverage mask = AND of three edges' sign bits.
   - Per covered subtile, compute the *farthest* `1/w` corner of the
     subtile (conservative: closest-corner would over-occlude).
   - Update tile's two-layer mask state per the masked-SOC merge rules
     (paper §4 / `QUICK_MASK` heuristic).

8. **Tile-state merge.** When the near-layer mask saturates, merge into
   the far-layer (`ZMin0 ← max(ZMin0, ZMin1); Mask ← 0`). When a tile
   becomes fully closed (`ZMin0 ≥ near-plane reciprocal`), record it in
   a tile-skip mask for fast early-out in subsequent triangles.

**Render order matters.** Per the reference: "rendering order can also
affect the quality of the hierarchical depth buffer, with the best order
being rendering objects front-to-back." Our `OccluderSelector` sorts
near→far precisely so the rasterizer benefits from the per-tile
early-out path the moment a near occluder closes a tile.

**Interleaving with queries** (paper §6, README "Interleaving occluder
rendering and occlusion queries") is supported by the design — `TestVisible`
can be called between occluder submits. We don't use it in Phase A but
the API doesn't preclude a future "interleaved BVH traversal" style
caller.

This is a faithful port or clean-room reimplementation of the reference
Apache-2.0 algorithm.
Scalar version first (clear correctness), AVX2 version second (performance);
both maintained alongside (the scalar path is the reference oracle for
`Debug.Assert` parity tests).

### Visibility test (`MaskedOcclusionAabbTester.TestVisible`)

This is the equivalent of the reference's `TestRect` function — and per
the reference README, this is the recommended fast path: "It can be used
to, for example, quickly test the projected bounding box of an object to
determine if the entire object is visible or not. The function is
considerably faster than `TestTriangles()` because it does not require
input assembly, clipping, or triangle rasterization … we've personally
seen best overall performance using this type of culling."

Per `TestVisible(stableQueryKey, worldBounds)` call:

1. **Self-occlusion guard.** If `stableQueryKey` is in the current frame's
   selected-occluder key set, return `true` immediately.
2. **Compute 8 AABB corners in world space**, transform to clip space.
   8 × `Vector4.Transform`; AVX2 path uses one matrix multiply on a
   packed 8-corner SoA layout.
3. **Frustum reject.** If all 8 corners are outside any single clip
   plane, return `false` (frustum-culled). Frees the caller from a
   pre-frustum check.
4. **Near-plane guard.** If any corner has `w <= near + epsilon`, return
   `true`. Near-straddling bounds are too close to cull safely with a
   projected screen rectangle.
5. **Project + bounding rect.** Per-corner `(x', y') = (x/w, y/w)`,
   compute screen-space bounding rect in pixel space, plus
   **`max(1/w)`** — the *nearest* reciprocal-depth of the AABB (largest
   1/w = closest). This is the conservative depth for the query: if even
   the nearest part of the AABB is behind the buffer's near layer, the
   whole AABB is occluded.
6. **Tile rect (8 x 4 snapping).** Convert to tile coords with
   `floor` for min and `ceil` for max — conservative inclusion.
7. **Tile loop.** For each tile in the rect:
   - If `tile.ZMin0 ≥ queryNearReciprocal` (tile's far layer is closer
     than the AABB's nearest corner) **and** `tile.Mask == 0` (no near
     layer present) — the AABB is fully behind this tile; **continue**
     to next tile (this tile rejects it).
   - Else if `Mask != 0` and `tile.ZMin1 ≥ queryNearReciprocal` and
     subtile masks fully cover the AABB's rect within the tile —
     also rejects.
   - Otherwise the AABB might be visible; **return `true` immediately**.
8. **Default.** If every overlapping tile rejected, return `false`.

The test is conservative: any tile that *might* show the AABB returns
`true` immediately. We only return `false` after every overlapping tile
fully occludes it. Note `1/w` ordering: "closer = larger" — we test
`tile.ZMin ≥ queryNearRecip` (reverse of a z-depth test).

### Stereo / multi-view

Two parallel `MaskedOcclusionBuffer`s, one per eye. `BeginFrame` takes
the camera; for stereo we get two cameras from `XRViewport`. Occluder
rasterization runs twice. AABB tests evaluate against both buffers and
return `true` if **either** says visible (an object visible in one eye
must be submitted).

Cost roughly 2×. Stereo is opt-in via existing `Stereo`-aware paths;
we don't pay it for mono cameras.

## Integration

### Settings

Engine settings (existing, repurposed):

```csharp
GpuOcclusionCullingMode=CpuSoftwareOcclusion // preferred SOC opt-in mode
EnableCpuSoftwareOcclusionCulling          // legacy side toggle (default false until promotion)
CpuSocBufferWidth                          // default 256
CpuSocBufferHeight                         // default 128
CpuSocOccluderTriangleBudget               // default 5000
CpuSocMaxOccluders                         // default 64
CpuSocMinOccluderScreenArea                // default 0.005 (NDC²)
CpuSocUseAvx2                              // default true if CPU supports
CpuSocDebugVisualization                   // default false (overlay)
CpuSocDebugForceVisible                    // default false (kill-switch)
```

All settings above are exposed through engine effective settings and the
runtime rendering facade. `XRE_OCCLUSION_CULLING_MODE=CpuSoftwareOcclusion`
is the preferred environment override; `XRE_CPU_SOC_OCCLUSION=1` remains as
a legacy toggle.

`RenderingParameters.ExcludeFromCpuOcclusion` already exists and applies to
the hardware-query path. SOC honors it for both occludee tests and occluder
selection. A separate positive occluder/proxy knob may be added later, but
it must not make masked/alpha-tested content an occluder by default.

### Wiring into `RenderCommandCollection.RenderCPU`

```csharp
if (useCpuQueryOcclusion)
{
    s_cpuOcclusionCoordinator.BeginPass(...);
    // NEW: SOC pass - first eligible pass for this frame/camera initializes the buffer.
    if (CpuSoftwareOcclusionCuller.IsEnabled
        && !s_cpuSoftwareOcclusionCuller.IsFrameInitializedFor(camera!, viewportW, viewportH)
        && RenderPassIsOcclusionTestable(renderPass))
    {
        s_cpuSoftwareOcclusionCuller.BeginFrame(camera!, viewportW, viewportH);
        s_cpuSoftwareOcclusionCuller.SubmitOccludersFromOpaqueCommands(this);
    }
}
// ...
foreach (var cmd in list)
{
    // ...
    if (useCpuQueryOcclusion && cmd is IRenderCommandMesh occlMesh)
    {
        // NEW: SOC prefilter
        if (s_cpuSoftwareOcclusionCuller.IsFrameOpen && cmd.CullingVolume is AABB worldAABB)
        {
            if (!s_cpuSoftwareOcclusionCuller.TestVisible(cmd.StableQueryKey, worldAABB))
            {
                OcclusionTelemetry.RecordCpuCulledOne();
                cpuCmdIndex++;
                continue;
            }
        }

        // Existing hardware-query path — unchanged
        var decision = s_cpuOcclusionCoordinator.ShouldRender(...);
        // ...
    }
}
```

`CpuSoftwareOcclusionCuller` owns `CpuSocTested` / `CpuSocCulled`
telemetry so the caller cannot double-count tests. The caller only records
the aggregate CPU cull (`RecordCpuCulledOne`) when SOC returns `false`.
`viewportW` / `viewportH` should come from the active render execution
state's internal render size, not the OS window size, so dynamic resolution,
VR eye textures, and offscreen captures do not poison the frame key.

### `SubmitOccludersFromOpaqueCommands`

Scans the `OpaqueDeferred` and `OpaqueForward` lists, runs `OccluderSelector`,
then rasterizes selected occluders into the buffer. This is one pass over
the opaque commands, capped by `MaxOccluders` and triangle budget. The
selector caches per-command `OccluderCandidate` data into a pooled list.

`RenderCommandCollection` needs an allocation-free render-pass enumerator
for mesh commands, or `SubmitOccludersFromOpaqueCommands` should live inside
`RenderCommandCollection` where it can walk `_renderingPasses` directly.
The selector must use render-thread snapshots of the command's mesh,
material, model matrix, and culling volume. Add an explicit render-snapshot
accessor if the current `IRenderCommandMesh` properties are app-thread
mutation state.

### Self-occlusion concern

A mesh selected as an occluder must not be tested against the buffer it
rasterized into — otherwise it occludes itself. Solution: tag the selected
commands' `StableQueryKey`s in an instance-field `HashSet<uint>` that is
cleared each frame; `TestVisible(stableQueryKey, ...)` on those keys is an
always-visible fast path. Cost: O(1) lookup per test, negligible, with no
steady-state allocation.

## Threading

Phase 1: single-threaded. Selector, rasterizer, and tester all run on the
calling thread (the render-thread orchestrator that already calls
`RenderCPU`). The implementation is render-thread-confined in Phase 1 and
does not take a lock inside the per-command `TestVisible` hot path.

Phase 2 (post-validation): job-parallelize the rasterizer across tile-rows
and parallelize `TestVisible` across command chunks via existing
`ThreadingHelpers`. Targets ~3× speedup on 8-thread machines, but holding
this for after we prove single-threaded perf hits budget.

## Telemetry

Extend `OcclusionTelemetry`:

```csharp
public static int CpuSocOccludersSelected   { get; }
public static int CpuSocOccludersRasterized { get; }
public static int CpuSocTilesClosed         { get; }   // full-coverage tile count
public static double CpuSocBeginFrameMs     { get; }
public static double CpuSocRasterMs         { get; }
public static double CpuSocTestMs           { get; }
```

Surfaced in the ImGui Occlusion panel as a new "CPU SOC" subsection,
alongside the existing "CPU-Query Path" and "GPU Hi-Z" blocks. Each
sub-counter mirrors the same color convention.

Debug overlay: a toggle in the panel that renders the `ZMin0` layer of the
buffer as a viewport-corner inset texture (sampled in a debug shader).
Toggle uses the new `CpuSocDebugVisualization` setting.

## Correctness Strategy

### Invariants

1. **No false negatives.** SOC never reports `false` on a mesh that has
   ≥1 visible pixel after the hardware depth test. Verified by:
   - All conservative rounding errs toward visible (`ceil` on max
     coverage, `floor` on min, `max` of corner depths).
   - Near-plane clip handled per-triangle.
   - Stereo OR-combined.
   - Backface culling honors per-material `RasterizerState.CullMode`.
2. **No NaN propagation.** All transforms guard against `w ≈ 0` by
   returning "visible" (cull as if outside frustum).
3. **Deterministic given same inputs.** No random / time-based heuristics.
   Selector is stable-sort on score.
4. **Per-frame budget enforced.** Selector hard-stops at
   `OccluderTriangleBudget`. Rasterizer stops at `MaxOccluders`.

### Tests

`XREngine.UnitTests/Rendering/Occlusion/` (new directory):

1. **`MaskedOcclusionBufferTests`** — buffer state transitions, tile-merge
   correctness, save/restore round trips.
2. **`MaskedOcclusionRasterizerTests`** — scalar vs AVX2 parity on a corpus
   of test triangles (axis-aligned, degenerate, near-clip-spanning,
   tiny, huge, back-facing).
3. **`MaskedOcclusionAabbTesterTests`** — AABB inside/outside/straddling
   tile boundaries; AABB closer than / farther than / mixed vs occluder.
4. **`OccluderSelectorTests`** — selection determinism, budget enforcement,
   frustum/opacity/near filtering.
5. **`CpuSoftwareOcclusionCullerIntegrationTests`** — end-to-end with a
   synthetic mini-scene: place a 10×10 quad occluder + 100 small AABBs
   behind it, assert `TestVisible` rejects all 100; place a small hole
   in the occluder, assert tests through the hole report visible.

Acceptance for the Sponza diagnostic:
- Capture the same view as the screenshot.
- With SOC on: `Visible(query)` drops from 10 to ≤ 3 (the genuinely-leaking
  arch fragments) and `CpuSocCulled` shows ≥ 7.
- With SOC forced-disabled (`CpuSocDebugForceVisible`), behavior identical
  to today's numbers (no regression).

## Risks and Mitigations

| Risk                                                | Mitigation                                                              |
| --------------------------------------------------- | ----------------------------------------------------------------------- |
| Rasterizer bugs cull visible meshes                 | Conservative rounding + `CpuSocDebugForceVisible` kill-switch.          |
| AVX2 not available on target CPU                    | Runtime check via `Avx2.IsSupported`; scalar fallback first, then optional SSE 4.1 if profiling shows it is worth carrying. |
| Reciprocal-depth (1/w) sign confusion vs z-depth    | Single helper `IsBehind(tileZ, queryZ)` documented + tested; scalar oracle parity tests cover every comparator. |
| Selector picks bad occluders (high tri, small area) | Score penalizes tri count; minimum-area threshold; budget caps.         |
| Stereo correctness                                  | OR-combine across eyes; per-eye buffers; AABB test surveys both.        |
| Hot-path allocations                                | All buffers pooled / instance-fields; no per-call allocs in `TestVisible`. |
| Render-thread blocking                              | Phase 1 single-threaded; budget enforced at ≤0.6 ms total.              |
| Self-occlusion of selected occluders                | Tag occluder keys; `TestVisible` returns visible on tagged keys.        |
| Near-plane clipping precision                       | Use reference's per-call `CLIP_PLANE_NEAR` enable; emit clipped-triangle pairs in scalar path before AVX2 rasterizes. |
| Ported-source license obligations                   | Preserve Apache-2.0 headers / notices for any translated reference code. |

## Implementation Plan

### Phase A — Scalar baseline (single PR, ~3 days work)

1. `MaskedOcclusionBuffer` struct + tile layout + state ops.
2. Scalar `MaskedOcclusionRasterizer` (no SIMD).
3. Scalar `MaskedOcclusionAabbTester`.
4. `OccluderSelector` with screen-area scoring.
5. Facade `CpuSoftwareOcclusionCuller` repurposed from scaffold.
6. Wiring into `RenderCommandCollection.RenderCPU`.
7. Telemetry counters + ImGui panel section.
8. Unit tests (1–4 from list above), integration test (5).
9. Validation against Sponza diagnostic.

Acceptance: SOC remains opt-in; Sponza `Visible(query)` drops when enabled;
forced-disabled behavior matches today's path; no visual regression; total
budget <= 1.5 ms (relaxed for scalar).

### Phase B — AVX2 hot loop (separate PR, ~2 days)

1. AVX2 rasterizer (port or reimplement the Apache-2.0 Intel reference algorithm).
2. AVX2 AABB tester (8 corners + tile loop SIMD).
3. Parity tests between scalar and AVX2 paths.
4. Re-profile, target ≤ 0.6ms budget.

### Phase C — Threading (separate PR, ~2 days, optional)

1. Per-tile-row job parallelism for rasterization.
2. Per-chunk parallelism for `TestVisible` (current API stays
   thread-safe because the buffer is read-only after rasterization).
3. Re-profile, target ≤ 0.3ms.

### Phase D — Debug visualization (separate PR, ~1 day, optional)

1. `MaskedOcclusionBufferReadback` RGBA8 export.
2. ImGui inset overlay shader.
3. Per-occluder highlight in scene view.

Phase D is purely diagnostic; ship A+B before D.

## Open Questions

1. **Should masked/alpha-tested content ever opt into SOC occluders?**
   Masked materials punch holes in occlusion (think foliage). Default
   false is the correctness baseline; revisit only with conservative
   alpha-test depth proxies or asset-authored occluder meshes.
2. **Buffer resolution.** 256×128 is a starting point. Burst Occlusion
   Culling uses 320×180. Higher resolution = better cull rate but more
   raster cost. Decide post-Phase-A benchmark.
3. **Promotion to GPU compute later?** A future GPU Hi-Z compute pass
   could read the same selected-occluder list to seed a GPU side. Not
   required, but the selector design leaves it open.

## References

- Andersson, Hasselgren, Akenine-Möller, *Masked Software Occlusion
  Culling*, HPG 2016. <https://www.intel.com/content/dam/develop/external/us/en/documents/masked-software-occlusion-culling.pdf>
- Intel reference implementation:
  <https://github.com/GameTechDev/MaskedOcclusionCulling>
  (**Apache-2.0** — code is reusable in commercial products with attribution).
- Unity Burst Occlusion Culling architecture notes:
  <https://docs.unity3d.com/Packages/com.unity.rendering.hybrid@0.50/manual/burst-occlusion-culling.html>
- id Tech 7 / Doom Eternal occlusion: Billy Khan, Siggraph 2020 advances.
- Diagnostic that led here: `OcclusionTelemetry.RecordCpuDecision` panel
  capture, Sponza-in-Sponza, 13% / 8% `VisibleQuery` survival.
