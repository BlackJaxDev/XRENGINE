# DDGI Integration Plan

Last Updated: 2026-04-15
Status: design
Scope: add a Dynamic Diffuse Global Illumination (DDGI) mode to XRENGINE that uses visibility-aware probe grids, integrates with the existing GI mode selection, and coexists cleanly with current light probes, Surfel GI, ReSTIR GI, and voxel cone tracing.

Primary reference: Morgan McGuire, Zander Majercik, Adam Marrs, "Dynamic Diffuse Global Illumination" (2019), parts 1–4.

Related docs:

- `docs/features/gi/global-illumination.md`
- `docs/features/gi/light-probes.md`
- `docs/features/gi/surfel-gi.md`
- `docs/features/gi/restir-gi.md`
- `docs/features/gi/voxel-cone-tracing.md`
- `docs/architecture/secondary-gpu-context.md`
- `docs/work/design/vxao-implementation-plan.md`
- `docs/work/todo/voxel-cone-tracing-and-vxao-implementation-todo.md`
- Morgan McGuire DDGI introduction: `https://morgan3d.github.io/articles/2019-04-01-ddgi/index.html`
- Morgan McGuire DDGI overview: `https://morgan3d.github.io/articles/2019-04-01-ddgi/overview.html`
- Morgan McGuire DDGI algorithm: `https://morgan3d.github.io/articles/2019-04-01-ddgi/algorithm.html`
- McGuire GI primer: `https://morgan3d.github.io/articles/2019-04-01-ddgi/intro-to-gi.html`
- Majercik et al., "Dynamic Diffuse Global Illumination with Ray-Traced Irradiance Probes", JCGT, April 2019

---

## 1. Executive Summary

DDGI is the missing middle ground in XRENGINE's current GI lineup.

The engine already has:

- classic light probes plus IBL for static or mostly-static lighting
- Surfel GI for fully runtime, compute-driven diffuse GI
- ReSTIR GI for high-end ray-traced lighting
- voxel cone tracing hooks for a future voxel-based dynamic GI path

What it does not have is a dynamic, noise-free, probe-based diffuse GI mode that:

- avoids the leaking behavior of classic irradiance probes
- amortizes work over time instead of paying a full per-pixel ray-tracing bill
- samples cheaply enough to be attractive for stereo and VR
- fits naturally beside existing reflection-probe and IBL workflows

DDGI should enter XRENGINE as a new diffuse-only GI mode. It should not replace the current `LightProbesAndIbl` path, and it should not be implemented by extending `LightProbeComponent` into a second unrelated role.

The correct product split is:

- current light probes remain the engine's reflection-probe and static-probe path
- DDGI becomes the engine's dynamic diffuse probe-grid path
- ReSTIR remains the premium ray-traced GI option
- Surfel GI remains the fully runtime fallback or alternative for scenes that do not fit probe grids well
- voxel cone tracing remains a separate scene-representation-driven GI family with different tradeoffs

Key reference numbers from the original article (McGuire, Majercik, Marrs 2019):

- ~5 MB GPU RAM per cascade (590 kB irradiance + ~4 MB visibility), peak ~20 MB for all cascades and intermediates
- 1–2 ms/frame at budget (fixed-time mode) on RTX 2080 Ti at 1080p60, or 1–2 Mrays/frame in fixed-quality mode
- overhead is minimized when ray tracing is shared with other effects (glossy, shadow rays)
- ~100 ms world-space latency on indirect light at 1–2 ms budget, 60 Hz, 1080p
- cannot prevent leaks from zero-thickness / single-sided walls
- shadowmap-like bias parameter must be tuned to scene scale
- must be paired with a separate glossy GI solution (SSR, env probes, geometric RT, etc.)

Recommended delivery sequence:

1. add a new DDGI mode, a dedicated DDGI volume owner, and honest pipeline scaffolding
2. implement probe-ray tracing and hit shading behind a backend that can use existing GPU BVH tracing now and hardware ray tracing later
3. implement probe irradiance and visibility atlas updates with hysteresis
4. implement screen-space DDGI sampling and GI compositing
5. add relocation, classification, scheduling, and cascades once the first bounded-volume path is correct
6. decouple diffuse GI mode selection from specular IBL usage so DDGI can coexist with the current light-probe reflection path
7. add baked / infinite-latency DDGI path reusing the same sampling and atlas infrastructure for legacy or budget platforms

---

## 2. Current Reality

### 2.1 Relevant Engine Seams

The most relevant current files and systems are:

- `XREngine.Data/Core/Enums/EGlobalIlluminationMode.cs`
- `XREngine.Data/Core/UserSettings.cs`
- `XRENGINE/Settings/GameStartupSettings.cs`
- `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.cs`
- `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs`
- `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs`
- `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.Textures.cs`
- `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.Textures.cs`
- `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs`
- `XRENGINE/Rendering/Pipelines/Commands/Features/GI/VPRC_SurfelGIPass.cs`
- `XRENGINE/Rendering/Pipelines/Commands/Features/GI/VPRC_ReSTIRPass.cs`
- `XRENGINE/Rendering/Pipelines/Commands/Features/GI/VPRC_VoxelConeTracingPass.cs`
- `XRENGINE/Scene/Components/Capture/LightProbeComponent.cs`
- `XRENGINE/Scene/Components/Capture/LightProbeComponent.IBL.cs`
- `XRENGINE/Scene/Components/Capture/LightProbeGridSpawnerComponent.cs`
- `XRENGINE/Rendering/Lights3DCollection.LightProbes.cs`
- `XRENGINE/Rendering/Compute/BvhRaycastDispatcher.cs`
- `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.SecondaryContext.cs`

### 2.2 What Already Exists

- `EGlobalIlluminationMode` already routes the renderer through a single GI mode selector.
- Both default render pipelines already expose mode-specific helpers such as `UsesSurfelGI`, `UsesRestirGI`, `UsesVoxelConeTracing`, and `UsesLightProbeGI`.
- The command chain already has a GI feature layer for light volumes, radiance cascades, Surfel GI, ReSTIR GI, and voxel cone tracing.
- The engine already has a mature light-probe path for scene-capture-driven cubemaps, irradiance, specular prefiltering, and influence blending.
- `LightProbeGridSpawnerComponent` already provides useful authoring patterns for regular grids, placement bounds, padding, and editor/runtime probe management.
- `Lights3DCollection.LightProbes` already manages classical probe registration and Delaunay interpolation.
- `BvhRaycastDispatcher` already provides a GPU BVH raycast dispatch path on the OpenGL baseline.
- The secondary GPU context already exists for offload-friendly GPU work such as probe or irradiance updates.

### 2.3 What Is Missing

- No DDGI mode in `EGlobalIlluminationMode`.
- No DDGI scene component or volume owner.
- No DDGI probe-field resource contract.
- No DDGI irradiance atlas, visibility atlas, or probe-state buffers.
- No DDGI trace, update, sampling, or debug passes.
- No probe relocation, classification, or scheduling rules.
- No runtime path that decouples diffuse GI selection from reflection-probe and IBL usage.

### 2.4 Consequence For Design

DDGI should be implemented as a new GI family, not as a hidden extension of the current light-probe capture path.

That means:

- do not model DDGI probes as `SceneNode + LightProbeComponent`
- do not route DDGI through `SceneCaptureComponent`
- do not reuse the current Delaunay tetrahedral interpolation path for DDGI sampling
- do reuse the engine's GI mode selection, command-chain structure, debug conventions, placement tooling ideas, GPU trace infrastructure, and secondary-context scheduling where they fit

---

## 3. Product Position

DDGI should be positioned as the engine's dynamic diffuse probe-grid GI mode.

Best-fit scenarios:

- dynamic time-of-day or moving-light scenes that still need stable diffuse bounce
- stereo and VR workloads where low-noise, shared-across-eyes GI is more valuable than per-pixel stochastic GI
- medium and large environments where classic light probes leak too much and ReSTIR is too expensive
- scenes that benefit from regular-grid or cascaded coverage instead of surfel lifetime heuristics

First-version non-goals:

- do not attempt glossy or mirror-accurate GI as part of DDGI (article explicitly positions DDGI as diffuse-only; glossy is a separate ray-trace pass)
- do not require Vulkan-only or RTX-only hardware to make the feature useful
- do not replace `LightProbesAndIbl` as the default GI mode on day one
- do not force DDGI probes to exist as scene nodes or serializable per-probe components
- do not solve infinite-scale open-world clipmaps in the first milestone before bounded volumes are correct

DDGI should initially solve diffuse indirect lighting only. However, note that DDGI **does** light surfaces seen in glossy reflections (second-order glossy → diffuse paths), and it can incorporate glossy reflection bounces within its multi-bounce convergence. The first-order glossy reflection into the camera is handled separately by SSR, environment probes, or geometric RT — not by DDGI.

---

## 4. Relationship To Existing GI Modes

| Existing mode | Role after DDGI lands | Should coexist? | Notes |
|---|---|---|---|
| `LightProbesAndIbl` | Static or mostly-static probe lighting plus reflection-probe IBL | Yes | Keep as the default static path. Also keep its specular/IBL contribution available when DDGI is the diffuse GI provider. |
| `LightVolumes` | Baked volume GI | Yes | Remains a baked alternative for static scenes and large authored spaces. |
| `RadianceCascades` | Baked or semi-baked cascade GI | Yes | Remains a non-probe alternative for static or controlled scenes. |
| `SurfelGI` | Fully runtime compute GI | Initially exclusive | DDGI and Surfel GI should start as separate modes. Hybrid near-field blending can come later if justified. |
| `Restir` | Premium ray-traced GI | Initially exclusive | Later, ReSTIR ray-hit shading can sample DDGI for diffuse fallback or secondary diffuse lighting if useful. |
| `VoxelConeTracing` | Advanced voxel-based dynamic GI | Exclusive | Different scene representation, different artifact class, no need to force shared infrastructure with DDGI. |

The important architectural correction is this:

- diffuse GI mode selection can remain exclusive initially
- reflection probes and specular IBL must not remain hard-coupled to `LightProbesAndIbl`

That split is necessary if DDGI is supposed to sit beside, rather than replace, the current probe-based lighting path.

---

## 5. Recommended Architecture

### 5.1 New GI Mode And Pipeline Hooks

Recommended additions:

- new GI mode: `EGlobalIlluminationMode.DDGI`
- new pipeline helper: `UsesDDGI`
- new GI output texture: `DDGITextureName`
- new GI composite target: `DDGICompositeFBOName`
- new atlas textures and buffers for probe data

If the codebase prefers normalized enum casing consistent with `Restir`, using `Ddgi` as the enum member is acceptable. The public terminology, file prefixes, shader folders, and profiler labels should still use `DDGI`.

Recommended render-pipeline resource names:

```csharp
public bool UsesDDGI => _globalIlluminationMode == EGlobalIlluminationMode.DDGI;

public const string DDGITextureName = "DDGITexture";
public const string DDGICompositeFBOName = "DDGICompositeFBO";
public const string DDGIIrradianceAtlasTextureName = "DDGIIrradianceAtlas";
public const string DDGIVisibilityAtlasTextureName = "DDGIVisibilityAtlas";
public const string DDGIProbeStateBufferName = "DDGIProbeStateBuffer";
public const string DDGIRayBufferName = "DDGIRayBuffer";
public const string DDGIHitBufferName = "DDGIHitBuffer";
```

### 5.2 Runtime Owner: `DDGIVolumeComponent`

DDGI should be owned by a dedicated scene-level volume component, not by one component per probe.

Recommended first owner type:

- `DDGIVolumeComponent`

Recommended placement:

- `XRENGINE/Scene/Components/Lights/DDGIVolumeComponent.cs`

Why a volume owner instead of reusing `LightProbeComponent`:

- current light probes are scene captures with six render passes per update, cubemap assets, and influence shapes
- DDGI probes are GPU-managed samples in a regular grid or cascaded grids
- DDGI wants thousands of probe records with atlas-backed data, not thousands of scene nodes and camera captures
- DDGI sampling uses regular-grid visibility-aware blending, not Delaunay interpolation

Recommended first-version `DDGIVolumeComponent` fields:

- `HalfExtents` or `Bounds`
- `ProbeCounts`
- `RaysPerProbe`
- `UpdateBudgetPerFrame`
- `IrradianceTexelSize`
- `VisibilityTexelSize`
- `Hysteresis`
- `NormalBias` (must be tuned to scene scale, similar to shadow-map bias)
- `ViewBias` (must be tuned to scene scale)
- `RelocationEnabled`
- `ClassificationEnabled`
- `UseCameraRelativeCascades`
- `CascadeSettings`
- `UpdateMode` (FixedTime or FixedQuality)
- `UpdateBudgetMs` (for FixedTime mode, default ~1–2 ms)
- `UpdateRayBudget` (for FixedQuality mode, default ~1–2 Mrays)
- `BakedMode` (None, SlowUpdate, FullyBaked)
- `DebugDrawProbes`
- `UseSecondaryContext`

Recommended first-version ownership rule:

- start with author-bounded local volumes
- add camera-relative cascades only after bounded volumes are correct and profiled

That is more pragmatic for XRENGINE than jumping straight to fully scrolling world-scale clipmaps.

### 5.3 Data Layout And Resource Contract

DDGI should use atlas-packed probe data, not per-probe cubemaps.

Recommended persistent GPU resources:

| Resource | Purpose | Recommended first-pass format |
|---|---|---|
| Probe state buffer | Base probe positions, relocation offsets, flags, update frame, priority | Structured SSBO |
| Irradiance atlas | Octahedral diffuse irradiance per probe | `R11G11B10F` (article default; ~590 kB per cascade) |
| Visibility atlas | Distance moments (mean distance, mean-of-distance²) for Chebyshev visibility weighting | `RG16F` (~4 MB per cascade) |
| Ray buffer | Per-ray origin, direction, probe index, cascade index | Structured SSBO |
| Hit buffer or hit G-buffer | Trace results needed for hit shading | Structured SSBO or compact texture set |
| Sample output texture | Screen-space diffuse GI result prior to composite | `RGBA16F` |

**Per-cascade memory budget:** irradiance (~590 kB) + visibility (~4 MB) = **<5 MB per cascade**. Peak with all cascades and intermediates: **~20 MB**.

Note: if HDR precision demands it, the irradiance atlas may be promoted to `RGBA16F` at the cost of roughly doubling irradiance memory. The article ships with `R11G11B10F` and this should be the default.

Recommended probe atlas conventions:

- octahedral projection per probe, unfolded into a square tile in the atlas
- 1-pixel border on each tile to enable fast hardware bilinear interpolation across tile edges
- irradiance and visibility stored in separate 2D textures because they have different precision and filtering needs
- regular-grid lookup with visibility-aware trilinear blending

Recommended initial probe sizes (from article):

- **irradiance tile: 6×6 texels including 1-pixel border** (4×4 interior)
- **visibility tile: 16×16 texels including 1-pixel border** (14×14 interior)
- default ray budget: **192 rays per probe** (viable range: 100–300)
- higher quality tiers increase ray count and/or visibility resolution

The important engineering rule is this: all DDGI resources must be persistent and reused. No per-frame managed allocations, no per-probe scene allocations, and no mandatory CPU readbacks outside debugging.

### 5.4 Probe Placement And Sampling

DDGI should use regular grids or cascaded regular grids.

Recommended finest-cascade grid dimensions: **32 × 4 × 32 = 4,096 probes** around the camera (article default). Coarser cascades halve resolution exponentially in space and update time.

Do not reuse:

- `Lights3DCollection.LightProbes` Delaunay triangulation
- `LightProbeCell` tetrahedra
- `RenderLightProbeTetrahedra` editor diagnostics as if they described DDGI weighting

DDGI sampling should instead use:

- grid-space lookup of the enclosing cell
- **trilinear 8-probe neighborhood selection** (2×2×2 surrounding probes)
- **Chebyshev visibility weighting** — each probe stores distance moments (mean, mean²) in the visibility atlas; at sample time, a Chebyshev test (similar to Variance Shadow Maps) computes an upper bound on the probability that the shading point is occluded from the probe, producing a smooth visibility weight that rejects light leaks without hard cutoffs
- per-probe **normal-direction cosine weighting** to prefer probes in the hemisphere of the surface normal
- probe relocation offsets that stay bounded to **half a grid spacing along each axis** (the dual-grid constraint) while indexing still snaps to the canonical regular grid vertex

Effective per-sample cost: **16 bilinear texture fetches** (8 probes × 2 atlas lookups) that are nearly perfectly cache-coherent due to spatial locality, plus **at most 9 division operations** for the visibility and weighting math.

This sampling path can be called from **any shader stage**: deferred shading compute, forward rasterization, volumetric ray marching, texture-space shading, or ray-hit shading (enabling multi-bounce). The `sampleDDGI(Point3 X, Vector3 n, DDGIVolume volume)` interface should be a reusable include.

This is the fundamental difference between classical probe interpolation and DDGI.

### 5.5 Trace Backend Strategy

The DDGI design must not assume that only one ray-tracing backend exists.

XRENGINE's baseline matters:

- Windows-first
- OpenGL 4.6 is still the primary shipping rendering path
- Vulkan and higher-end ray-tracing paths are present but not the baseline for all users

Recommended DDGI trace backend contract:

- one DDGI trace pass produces a unified hit buffer contract
- backend selection chooses how that hit buffer is filled

Recommended backend order:

1. hardware ray tracing backend when available and mature enough
2. GPU BVH compute tracing backend using `VisualScene3D.BvhRaycasts` / `BvhRaycastDispatcher`
3. baked or infinite-latency DDGI update path for static scenes that still want DDGI's sampling and leak behavior

This lets DDGI become useful on the current OpenGL-first engine instead of being blocked on Vulkan RT becoming the universal baseline.

### 5.6 Hit Shading And Infinite-Bounce Behavior

The DDGI trace stage should not invent a second lighting model.

Recommended flow:

1. generate probe rays (100–300 per probe; 192 default)
2. trace them through the selected backend
3. shade ray hits using the engine's existing direct-lighting and material logic (including shadow maps or shadow rays)
4. include emissive and previous-frame DDGI contribution at hit points — this is the source of **infinite-bounce GI**, converging quickly over a few frames when the scene changes
5. write shaded radiance and hit distance into the update stage

That reuses the main renderer's lighting model and gives DDGI its expected multi-bounce convergence over time.

Optimization note from article: probe rays and their mini G-buffer can be **packed into the bottom half of the screen-space buffer used for glossy GI ray trace**, so both kinds of rays launch and shade in a single dispatch. This reduces overhead and achieves better GPU scheduling. Consider this when glossy RT is also active.

Recommended warm-start behavior:

- first frame: direct light, emissive, and optional sky or reflection-probe fallback only
- later frames: previous DDGI atlases also contribute when shading probe-ray hits

### 5.7 Update Pipeline

Recommended DDGI runtime stages:

1. **Trace**
   Generate or refresh a budgeted subset of probe rays and trace them.

2. **Shade Hits**
   Evaluate direct lighting, emissive, and previous DDGI contribution at the ray hits.

3. **Update Irradiance Atlas**
   For each probe texel, gather all ray hitpoints that affect it and blend with hysteresis. The gather is memory-coherent because each probe's rays map to a contiguous region of the ray buffer.

4. **Update Visibility Atlas**
   Accumulate distance moments (mean distance and mean-of-distance-squared) using the same hysteresis approach. These moments are consumed at sample time via Chebyshev's inequality for soft visibility weights.

5. **Probe Maintenance**
   Run relocation, classification, sleep or wake, and priority updates.

6. **Screen Sampling**
   Sample DDGI per pixel from the G-buffer into `DDGITextureName`.

7. **Composite**
   Blend DDGI into the existing light combine contract. The article's recommended composition with glossy is:
   ```glsl
   Radiance3 L = lerp(glossyLight,
                      lambertianReflectivity * sampleDDGI(X, n, ddgi),
                      fresnel);
   ```
   This naturally separates DDGI's diffuse contribution from specular and respects the Fresnel split.

**Dual update modes** (from article):

- **Fixed-time mode** — cap per-frame GPU time to a budget (e.g. 1–2 ms). Indirect light has world-space latency (~100 ms at 60 Hz 1080p on RTX 2080 Ti). On lower-end GPUs, latency increases but image quality when static is identical.
- **Fixed-quality mode** — trace a fixed ray count per frame (e.g. 1–2 Mrays). Guarantees quality but allows varying GPU time.

The engine should expose both modes and default to fixed-time for runtime and fixed-quality for baked/offline.

Recommended first-pass hysteresis range:

- start near the article's 97 percent default
- expose this as a quality-stability knob
- allow special-case faster convergence for explicitly invalidated or relocated probes

### 5.8 Stereo And VR Implications

DDGI is a good fit for XRENGINE's XR focus because:

- trace and atlas update work is not proportional to screen resolution or frame rate (steps 1–2 are resolution- and framerate-independent per the article)
- both eyes can share the same DDGI probe field
- the expensive work is amortized in world space, not paid twice per eye
- the screen sampling pass is coherent and cheap relative to full ray-traced GI
- world-space latency (not screen-space ghosting) is the tradeoff on lower-end hardware, which is less objectionable in VR than per-pixel noise

Recommended stereo rule:

- trace and atlas updates are shared once per frame across both eyes
- screen sampling runs per eye, writing into the same per-eye GI contract the pipeline already uses

---

## 6. Interaction With Current Light Probes

Current `LightProbeComponent` behavior should remain intact.

It still owns:

- environment capture
- irradiance cubemap processing for the existing static path
- prefiltered specular reflection data
- influence regions and blending behavior for the current probe system

DDGI should not replace that asset path.

Instead, the engine should gradually move toward this split:

- **Diffuse GI provider:** selected by `EGlobalIlluminationMode`
- **Specular IBL provider:** reflection probes, sky, SSR, or other reflection systems

That enables the desired combination:

- DDGI for diffuse bounce
- existing light probes or sky IBL for glossy and rough specular

Possible reuse points from the current probe stack:

- grid placement UX concepts from `LightProbeGridSpawnerComponent`
- placement bounds derived from model components
- probe debug rendering patterns
- editor preference patterns for showing probe diagnostics

Non-reuse points:

- cubemap capture
- Delaunay interpolation
- `IsLightProbePass`
- per-probe scene-node ownership

---

## 7. Recommended Implementation Phases

### Phase 0: Honest Scaffolding

Outcome: the engine exposes DDGI as a planned mode without pretending it works already.

- add `EGlobalIlluminationMode.DDGI`
- add `UsesDDGI` helpers in both default render pipelines
- add neutral resource creation and mode routing for DDGI
- add `DDGIVolumeComponent` scaffolding and editor-visible settings
- add unit-testing world toggles for bounded DDGI volumes
- document that DDGI is diffuse-only and experimental during bring-up

Acceptance criteria:

- selecting DDGI is a real code path, not a missing enum case
- the renderer logs honestly when DDGI is selected before the implementation is complete

### Phase 1: Volume Owner And Resource Contract

Outcome: the DDGI volume exists as a real runtime owner with persistent resources.

- allocate probe-state buffers, irradiance atlas, visibility atlas, and screen output texture
- define exact atlas indexing conventions and shader bindings
- expose resource names and profiler labels
- ensure steady-state DDGI creates no per-frame managed allocations

Acceptance criteria:

- DDGI resources are visible in resource logging and debug tooling
- bounded DDGI volumes can be instantiated in a scene and survive resize and stereo paths cleanly

### Phase 2: Probe-Ray Generation And Trace Backend

Outcome: the engine can trace DDGI probe rays through a selected backend.

- generate deterministic per-probe ray sets
- build a unified ray or hit buffer contract
- integrate the OpenGL GPU BVH path first through `BvhRaycastDispatcher`
- leave a clean seam for later hardware RT backends
- optionally schedule updates on `Engine.Rendering.SecondaryContext` when appropriate

Acceptance criteria:

- a DDGI debug view can show probe-ray dispatch counts and hit validity
- the trace stage works on the current OpenGL baseline without requiring ReSTIR hardware support

### Phase 3: Irradiance And Visibility Updates

Outcome: traced rays update DDGI probe data over time.

- shade probe-ray hits using existing lighting rules
- update irradiance atlas with hysteresis
- update visibility atlas with distance moments
- support explicit probe invalidation on large scene changes
- establish first-frame warm-start behavior

Acceptance criteria:

- probe atlases converge over several frames instead of flickering per frame
- basic leak rejection is measurable in controlled test scenes

### Phase 4: Screen Sampling And Composite

Outcome: DDGI visibly contributes to the final frame.

- reconstruct world position and normal from the G-buffer
- sample DDGI with regular-grid visibility-aware weighting
- output to `DDGITextureName`
- composite into the current light-combine path
- add raw irradiance, visibility, probe index, and final DDGI debug views

Acceptance criteria:

- enabling DDGI changes the rendered frame with visible diffuse GI
- DDGI sampling works in deferred and stereo rendering paths

### Phase 5: Relocation, Classification, And Stability

Outcome: DDGI behaves robustly around walls, dynamic geometry, and low-value probes.

- implement relocation offsets to move probes out of solid geometry
- add probe classification and sleeping for stable or irrelevant probes
- validate that moving geometry does not create catastrophic dark or bright leaks
- add profiler counters for updated probes, relocated probes, and sleeping probes

Acceptance criteria:

- the classic sealed-room leak test behaves materially better than current light probes
- probes intersecting geometry do not permanently poison nearby shading

### Phase 6: Cascades And Large-Scene Scaling

Outcome: DDGI scales beyond a single bounded volume.

- add camera-relative cascades or scrolling grids
- default finest cascade: **32 × 4 × 32 = 4,096 probes**; coarser cascades double spacing
- update near cascades more frequently than far cascades
- **fade out and then entirely disable visibility storage on very coarse cascades** to halve their memory and shading cost (same pattern as shadow-map cascades)
- add priority scheduling so time budgets remain stable
- validate large-scene memory and time budgets (target: peak ~20 MB for all cascades + intermediates)

Acceptance criteria:

- large scenes can span multiple DDGI coverage zones without forcing all probes to update every frame
- per-frame DDGI cost can be constrained by budget instead of exploding with scene size

### Phase 7: Baked / Infinite-Latency DDGI

Outcome: the same DDGI atlas and sampling infrastructure supports a fully baked mode for legacy or budget platforms.

The article explicitly positions baked DDGI as a scalability path: on platforms where ray tracing is too expensive, set the update latency to "infinity" and precompute the probe data. The GI will not capture dynamic lights or geometry, but still provides the leak-rejection quality and workflow benefits of DDGI sampling.

- serialize probe atlases (irradiance + visibility) as baked assets
- the runtime sampling path is identical to dynamic DDGI — no separate code
- expose a per-volume baked-vs-dynamic toggle
- optionally support a slow-update baked mode that re-traces a few probes per frame on budget GPUs

Acceptance criteria:

- a DDGI volume can be baked in-editor and loaded without any runtime tracing
- the baked path still avoids leaks due to the visibility data

### Phase 8: Hybrid Integrations

Outcome: DDGI fits naturally beside the rest of the engine's GI and reflection systems.

- allow DDGI diffuse plus existing reflection-probe or sky IBL specular
- evaluate whether ReSTIR ray-hit shading should sample DDGI for diffuse fallback or secondary shading
- evaluate ray-packing optimization: share a single dispatch with glossy RT rays for better GPU scheduling
- decide whether near-field detail should be augmented by AO or another short-range GI layer

Acceptance criteria:

- DDGI is no longer treated as an isolated experimental mode with no relationship to the rest of the lighting stack
- hybrid behavior is documented clearly for users and maintainers

---

## 8. Validation Plan

### 8.1 Visual Test Scenes

The first validation scenes should be simple and diagnostic, not content-pretty.

Recommended scenes:

- Cornell-box style color-bleed scene
- sealed-room versus open-room leak test
- moving dynamic-geometry stress scene with objects crossing probe locations
- day-night or color-changing key light transition scene
- reflective interior scene that uses DDGI diffuse plus existing specular IBL

### 8.2 Unit And Integration Tests

Mirror the existing Surfel GI testing style.

Recommended tests under `XREngine.UnitTests/Rendering/`:

- DDGI compute shader compile and link tests
- atlas indexing and border copy tests
- octahedral encode or decode math tests
- visibility weighting math tests
- hysteresis update math tests
- relocation clamp tests
- no-allocation steady-state smoke tests where practical

### 8.3 Profiling And Diagnostics

Add explicit counters for:

- probes allocated
- probes updated this frame
- probes asleep
- probes relocated
- rays traced
- trace time
- hit shading time
- irradiance update time
- visibility update time
- sampling time
- composite time

Recommended debug views:

- probe positions and relocation offsets
- irradiance atlas slices
- visibility atlas slices
- probe classification state
- per-pixel selected probe neighborhood
- final DDGI-only contribution

### 8.4 Budget Targets

Initial targets should be realistic and quality-tiered.

- bounded volume first version before clipmaps
- default probe update budget tuned for roughly 1 to 3 ms of GPU time on a mainstream desktop tier
- screen sampling cheap enough to remain attractive in stereo and VR
- zero mandatory CPU readbacks in the shipping path
- zero steady-state managed allocations on hot paths

---

## 9. Key Design Decisions

These are the decisions this plan recommends locking early:

1. DDGI is a new GI mode, not an extension of `LightProbeComponent`.
2. DDGI diffuse and current reflection-probe IBL should be able to coexist.
3. DDGI sampling uses regular-grid Chebyshev-visibility-aware weighting, not Delaunay interpolation.
4. The first useful implementation should work on the OpenGL baseline through the GPU BVH path, not wait for full hardware RT availability.
5. Bounded volumes come before scrolling or clipmapped cascades.
6. Probe relocation and visibility atlases are core DDGI features, not optional polish.
7. All DDGI runtime data should be persistent GPU resources with no steady-state managed allocations.
8. Irradiance atlas defaults to `R11G11B10F` per the original article; `RGBA16F` is an optional higher-precision override.
9. Baked DDGI (infinite-latency mode) uses the same atlas format and sampling path as dynamic DDGI — it is a core scalability tier, not an afterthought.
10. DDGI cannot prevent leaks from zero-thickness or single-sided walls; this is a known algorithm-level limitation that should be documented for users and art direction.

---

## 10. Files Of Interest

Existing files that should be touched:

- `XREngine.Data/Core/Enums/EGlobalIlluminationMode.cs`
- `XREngine.Data/Core/UserSettings.cs`
- `XRENGINE/Settings/GameStartupSettings.cs`
- `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.cs`
- `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs`
- `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.Textures.cs`
- `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs`
- `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.Textures.cs`
- `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs`
- `XRENGINE/Rendering/Pipelines/Commands/Features/GI/VPRC_SurfelGIPass.cs`
- `XRENGINE/Rendering/Compute/BvhRaycastDispatcher.cs`
- `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.SecondaryContext.cs`
- `Assets/UnitTestingWorldSettings.jsonc`

Recommended new files:

- `XRENGINE/Scene/Components/Lights/DDGIVolumeComponent.cs`
- `XRENGINE/Rendering/Pipelines/Commands/Features/GI/VPRC_DDGITracePass.cs`
- `XRENGINE/Rendering/Pipelines/Commands/Features/GI/VPRC_DDGIUpdatePass.cs`
- `XRENGINE/Rendering/Pipelines/Commands/Features/GI/VPRC_DDGICompositePass.cs`
- `XRENGINE/Rendering/Pipelines/Commands/Features/GI/VPRC_DDGIDebugVisualization.cs`
- `XREngine.UnitTests/Rendering/DdgiComputeIntegrationTests.cs`
- shader files under `Compute/DDGI/` and `Scene3D/DDGI*`

---

## 11. Open Questions

Questions that should be answered before implementation gets too deep:

1. Should the first author-facing DDGI owner be strictly bounded-volume only, or should a camera-relative cascade mode be exposed immediately as experimental?
2. Should DDGI updates default to the main render context first, or should the secondary context be used by default whenever available?
3. Should the first shipping DDGI path remain exclusive with Surfel GI, or should a near-field surfel plus far-field DDGI hybrid be part of the initial roadmap?
4. At what phase should scene-scale bias auto-tuning be tackled? The article notes that `NormalBias` and `ViewBias` need to be tuned to scene scale similarly to shadow-map depth bias. Manual per-volume tuning is acceptable initially, but auto-derivation from grid spacing would improve usability.
5. Should the ray-packing optimization (sharing a dispatch with glossy RT rays) be implemented as part of the first glossy-hybrid phase, or deferred to a dedicated optimization pass?

---

## 12. Recommended Next Step

The best immediate implementation step is Phase 0 plus Phase 1:

- add the DDGI mode and `DDGIVolumeComponent` scaffolding
- allocate persistent DDGI resources in both default pipelines
- add a neutral stub path with profiler labels and debug hooks
- wire a unit-testing world toggle so the first bounded DDGI validation scenes can be created without further pipeline surgery

That gets DDGI into the engine honestly and creates the seam needed for the real trace, update, and sample work.