# Advanced Flat Mirror Rendering Design

[<- Work docs](../README.md)

## Goal

Add a production-ready flat-plane mirror system that can render multiple visible mirrors with predictable quality and cost across both CPU-driven and GPU-driven rendering paths.

The target is planar reflection, not curved reflection, glossy probe reflection, screen-space reflection, or arbitrary portal rendering. Flat mirrors should integrate with the existing scene graph, `DefaultRenderPipeline`, OpenGL 4.6, VR stereo rendering, and the GPU indirect path without making the old CPU path a second-class citizen.

## Current Repo Signals

The repository already contains pieces that point toward this feature:

- `MirrorCaptureComponent` implements a traditional render-to-texture mirror with a mirrored camera, oblique clipping, and mirror-pass state.
- `AdvancedForwardMirrorComponent` already renders a mirror plane into stencil and computes a reflection matrix.
- `GPUViewSet` already models multiple views, including `GPUViewFlags.Mirror`, per-view constants, per-view visible lists, and a 64-view hard cap.
- `RenderCommandCollection` already configures a GPU view set for stereo, foveated views, and desktop VR mirroring.
- `Engine.Rendering.State.PushMirrorPass()` and `ReverseCulling` already model odd reflection passes.
- `docs/user-guide/rendering.md` explicitly describes mirrors and other secondary views as plugging into `CollectRenderedItems`.

The missing piece is not mirror math. The missing piece is an owned renderer feature that can discover visible mirror surfaces, schedule reflection views, render them into managed targets, and composite those targets correctly in forward, deferred, CPU, GPU, mono, and stereo paths.

## Non-Goals

- Infinite perfect recursion. Recursion must be budgeted and capped.
- Arbitrary portal/world traversal. This design can evolve toward portals, but mirror reflection is the v1 target.
- Screen-space reflection replacement. SSR may be used as a fallback or rough-material embellishment, but not as the primary mirror path.
- Curved mirrors or non-planar reflective meshes.
- Vulkan parity in the first implementation. The design should avoid blocking Vulkan, but OpenGL 4.6 remains the primary path.

### Explicitly NOT solved in v1

To prevent scope creep during implementation review, these are deliberately deferred and must not be added to v1 milestones without revisiting this document:

- **SSR fallback handoff.** Hybrid SSR + planar reflection blending is a future feature. v1 mirrors either render or are frozen/skipped.
- **Glossy importance-sampled reflections.** v1 roughness uses cheap mip sampling or separable blur on the reflection texture.
- **Precomputed planar reflection probes.** All reflections are rendered live in v1.
- **Water surfaces.** Water shares the planar reflection abstraction (see below) but adds caustics/refraction/wave perturbation that are out of scope.
- **Mirror-aware GI.** Reflected scenes do not contribute to probe baking, light propagation volumes, or GI updates in v1.
- **Cross-mirror temporal reprojection.** No motion-vector flow between a mirror and its reflection target in v1.

## Recommended Direction

Implement mirrors as a pipeline-owned secondary-view feature:

1. Collect visible **planar reflection providers** during main-view visibility collection.
2. Rank and budget those providers.
3. Create one reflection view per selected provider, with mirrored view/projection constants and an oblique clipping plane.
4. Render reflection views into a texture array or atlas.
5. Draw **planar reflection consumers** in the main pass using a material that samples the assigned reflection target.
6. Use stencil only for screen-space masking and recursion bookkeeping, not as the primary ownership model for reflection contents.

This should begin with a CPU-driven implementation that shares the same scheduling/resource model as the later GPU-driven path. The important architectural move is to make "mirror view" a first-class render view, even when the first renderer executes those views with CPU collection and ordinary draw calls.

### Provider / Consumer abstraction

Decouple "planar reflection produced for plane P" from "this surface samples that reflection". Two interfaces, both wired into the same scheduler and resource pool:

- `IPlanarReflectionProvider` — submits a `MirrorRenderRequest` describing a plane, importance, profile, and quality tier. `AdvancedFlatMirrorComponent` is the v1 implementation. Future: water, wet floor decals, glossy planar floor materials, portal stubs.
- `IPlanarReflectionConsumer` — any material that wants to sample a reflection target by **plane id** (not by component reference). Multiple consumers may share one provider's reflection (e.g. a tiled mirror wall). The scheduler resolves consumer → provider in a single pass.

This abstraction does not add v1 implementation cost. It is a naming and interface choice that prevents `AdvancedFlatMirrorComponent` from becoming the special case the rest of the engine has to know about, and is what makes the future water/portal work cheap.

## Forward vs Deferred Decision

This is two decisions, not one. They are independent and both must be made:

1. **Mirror composite path** — how the mirror *surface* is drawn into the main camera image.
2. **Reflected scene path** — how the *contents* of the reflection target are rendered.

### Decision 1: Mirror surface composite is forward

Mirror surfaces themselves should be drawn as forward materials in the main scene, regardless of how the reflected scene was rendered.

Reasons:

- A mirror surface is a special material with explicit sampling, Fresnel, tint, roughness blur, and edge fade behavior. This fits forward shading better than trying to encode mirror data into the GBuffer.
- Deferred mirror surfaces create awkward composition ordering: the mirror needs a resolved reflection color before light combine, but light combine is also the stage that resolves deferred scene lighting.
- Forward composite lets mirrors appear after deferred opaque lighting and before transparent/post-temporal passes, which is easier to reason about.
- Stencil masking is naturally local to drawing the mirror surface in the main framebuffer.

### Decision 2: Reflected scene uses a profile, not the active pipeline minus stuff

The reflected scene path is selected by an explicit `MirrorRenderProfile` enum on the request, not derived from the active camera's pipeline. This prevents silent regressions every time the main pipeline changes.

```csharp
public enum MirrorRenderProfile
{
    OpaqueOnly,        // sky + opaque forward, no lights beyond direct sun, no post
    OpaqueWithSky,     // OpaqueOnly + ambient/IBL contribution
    OpaqueTransparent, // OpaqueWithSky + transparent pass (no OIT in v1)
    Cinematic,         // closest to main pipeline; deferred opaque, direct light combine, opt-in bloom/exposure
}
```

Provider components choose a profile based on importance and material intent. The pipeline maps profile → concrete render command chain:

- `OpaqueOnly` and `OpaqueWithSky` always run as forward, even when the main camera is deferred. This avoids cross-wiring private GBuffer resources for low-importance mirrors.
- `OpaqueTransparent` runs as forward unless the main camera is deferred *and* the provider explicitly opts in to deferred reflected lighting.
- `Cinematic` mirrors the active camera's path with reduced post-processing.

This means the reflected scene path is no longer "the active pipeline minus stuff" but "the profile the mirror requested". Authoring cost is a single dropdown on the component.

Do not build a separate "mirror-only lighting model" unless profiling proves the profile presets are too expensive.

### Deferred-specific guidance

Deferred reflection views should render to their own reflection HDR target, using private GBuffer/depth resources or a compact mirror GBuffer set. They should not write into the main camera GBuffer.

For v1, use a reduced reflection chain:

- depth prepass if needed by the active pipeline
- deferred opaque or forward opaque reflection scene
- direct light combine
- optional sky/background
- optional transparent pass only when enabled on the mirror
- no bloom, TAA, TSR, DoF, motion blur, or exposure update in reflection views by default

The mirror texture should store HDR scene color in the same color space expected by the main pipeline before post-process.

### Forward-specific guidance

Forward reflection views are simpler:

- render sky/background and opaque forward objects into reflection HDR
- optionally render transparent objects into the same target
- skip most post-processing
- composite onto mirror surface in the main forward pass

Forward is also the best fallback path for mirrors inside unusual pipelines because it avoids cross-wiring GBuffer resources.

## Mirror Shader Contract

Mirroring flips winding and view direction. Several material features silently break under mirroring unless the shader is informed. The contract below is mandatory for any shader that may be drawn into a reflection view.

### What the renderer guarantees

- A `XRENGINE_MIRROR_PASS` macro is defined for any compilation targeting a reflection view.
- A view-constant flag `IsMirrorView` is exposed on the per-view UBO/SSBO.
- `Engine.Rendering.State.PushMirrorPass()` flips the front-face winding for the duration of reflection rendering.
- The clip plane (oblique near plane) is exposed as a uniform `MirrorClipPlaneWS`.

### What the shader must do under `XRENGINE_MIRROR_PASS`

- **Tangent-space normal maps:** flip the bitangent sign (or equivalently, flip the tangent's handedness bit) before constructing TBN. Otherwise normal-mapped surfaces look inside-out in reflections.
- **Parallax / POM:** invert view-direction handedness in tangent space, or disable parallax for the mirror pass.
- **Anisotropy direction:** mirror the anisotropy tangent across the reflection plane in world space, then transform to tangent space.
- **Skinned mesh normals:** ensure normal/tangent matrices use `transpose(inverse(modelView))` and not a cached cofactor that assumed positive determinant. Most engine skinning is fine; verify per-skin path.
- **Cull face / two-sided:** unchanged. The renderer flips winding centrally; shaders must not also flip.
- **gl_ClipDistance:** when oblique projection is replaced by a user clip plane (see *Oblique clipping* below), shaders write `gl_ClipDistance[0] = dot(MirrorClipPlaneWS, vec4(worldPos, 1))`.

### Mirror material variants (answer to the design question)

The engine already has the right pattern. [`ShadowCasterVariantFactory.CreateMaterialVariant`](../../../XREngine.Runtime.Rendering/Shaders/ShadowCasterVariantFactory.cs) and [`ForwardDepthNormalVariantFactory.CreateMaterialVariant`](../../../XREngine.Runtime.Rendering/Shaders/ForwardDepthNormalVariantFactory.cs) already produce per-pass material variants by swapping the fragment shader and pruning unused uniforms, with telemetry through `UberMaterialVariantStatus` / `UberShaderVariantTelemetry`.

**Recommendation: add `MirrorMaterialVariantFactory.CreateMaterialVariant(XRMaterial source, MirrorRenderProfile profile)`** that produces a stripped material per `MirrorRenderProfile`:

| Profile | Variant strips | Variant keeps |
|---|---|---|
| `OpaqueOnly` | parallax, detail maps, clearcoat, sheen, anisotropy, screen-space effects, vertex animation if cheap-to-skip | base color, normal (with bitangent flip), roughness, metallic, direct sun, fog |
| `OpaqueWithSky` | as above | + ambient/IBL, sky sampling |
| `OpaqueTransparent` | parallax, clearcoat, anisotropy | + transparent blend modes |
| `Cinematic` | nothing (use full uber variant) | full feature set |

The variant is selected at indirect-command build time per (material, profile) pair and cached. Cache key is `(materialHash, profile)`. This piggybacks on the existing variant cache and telemetry — no new infrastructure.

Expected wins on `OpaqueOnly`/`OpaqueWithSky` profiles: fewer texture samples, smaller register pressure, faster compile of the simplified variant, and — most importantly — fewer code paths that can subtly break under `XRENGINE_MIRROR_PASS` (because the stripped paths can't be hit).

This is also how curved-content shaders (skinning, vertex animation, decals) get a deterministic answer to the question "do I need to be mirror-correct?" — only if the variant for the chosen profile keeps that feature.

## CPU-Driven Path

The CPU path should be correct first and should establish the engine-facing API.

### Collection

During main-camera collect visible:

1. `AdvancedFlatMirrorComponent` exposes a `RenderInfo3D` for the mirror surface.
2. The mirror component registers itself with a per-frame `MirrorRenderScheduler` when visible.
3. The scheduler receives:
   - mirror component id
   - world plane
   - screen-space bounds estimate
   - distance from camera
   - area/importance
   - recursion depth
   - material quality settings

The existing `collectMirrors` flag should keep preventing uncontrolled recursive collection. The scheduler should explicitly decide when a mirror participates in a reflection view.

### View generation

For each selected mirror:

- Build the reflection matrix from the plane.
- Build mirrored camera world matrix or directly build mirrored view matrix.
- Copy the source camera projection.
- Apply behind-mirror clipping against the mirror plane (see *Oblique clipping* below).
- Set mirror pass state so culling reverses on odd reflection passes.
- Assign a reflection target layer or atlas rect.

The CPU path can use `XRViewport.CollectVisible(... cameraOverride, collectionVolumeOverride)` for reflection views. The collection volume should be the mirror camera frustum, optionally intersected with the reflected main camera frustum and mirror plane bounds.

#### Oblique clipping (correctness caveats)

Oblique near-plane projection is the textbook approach but has real costs the engine must account for:

- It destroys depth precision near the plane, which interacts badly with reverse-Z and the engine's TAA jitter.
- It makes the projection matrix non-standard, breaking shadow cascade selection heuristics that assume an unmodified projection.
- It changes the meaning of `gl_FragCoord.z`, which downstream effects (depth-based fog, contact shadows, SSAO sampling) may consume.

Two modes, selected per provider:

1. **Oblique projection (default)** — fastest, no shader cost, but apply a small guard band (offset the clip plane ~1mm behind the mirror in world space) to avoid Z-fighting at the seam, and disable TAA jitter for reflection views in v1.
2. **User clip plane fallback** — set a standard projection and let shaders write `gl_ClipDistance[0]`. Use this when the material variant already pays for clip-distance (e.g. cinematic profile). Cost: one extra interpolant per vertex; benefit: depth precision and TAA work correctly.

The `MirrorRenderProfile` chooses the mode: `OpaqueOnly` and `OpaqueWithSky` use oblique projection; `Cinematic` uses the user clip plane fallback. Document this explicitly so reverse-Z regressions are not blamed on the mirror feature later — this is the most common silent bug in planar reflection systems.

### Rendering

For each scheduled reflection view:

1. Bind the reflection FBO or atlas layer.
2. Clear color/depth/stencil for the view region.
3. Push mirror pass state.
4. Render the constrained reflection command chain.
5. Pop mirror pass state.

Then the main pass draws the mirror surface forward material with:

- reflection texture or atlas
- mirror target index/rect
- plane fade controls
- optional tint
- optional roughness mip/blur source
- optional normal map distortion

### Strengths

- Easy to debug.
- Reuses existing `RenderCommandCollection`, `XRViewport`, and pipeline infrastructure.
- Works when GPU indirect dispatch is disabled.
- Provides a reference path for GPU-driven validation.

### Weaknesses

- CPU collection and command submission scale linearly with selected mirrors.
- Multiple reflection views can multiply draw calls.
- Sorting and material batching are less efficient than the GPU path.

## GPU-Driven Path

The GPU path should extend the existing view-set model rather than creating a separate mirror renderer.

### View set extension

Replace the fixed local stack allocation currently sized for stereo/foveated/VR mirror views with a dynamic view-list builder. The builder should support:

- main mono/stereo views
- foveated helper views
- desktop VR mirror view
- flat mirror reflection views
- future portal/reflection probe views

`GPUViewDescriptor` already has the right shape for a first version: `ViewId`, `ParentViewId`, flags, render pass masks, output layer, view rect, and per-view visible offsets. Mirror views need additional metadata, either in a new mirror metadata SSBO or a widened descriptor:

- mirror component id
- mirror plane
- target atlas rect or texture array layer
- recursion depth
- stencil reference or mask policy
- quality tier

Avoid widening `GPUViewDescriptor` unless the metadata is needed by all view types. A dedicated `GPUMirrorViewMetadata` buffer is cleaner.

### GPU culling

For each active mirror view:

- Upload `GPUViewConstants` with reflected view/projection.
- Let `GPURenderCulling.comp` or BVH culling evaluate visibility against that view.
- Write visible command indices into the view's slice of `PerViewVisibleIndicesBuffer`.
- Build indirect commands for the chosen source view when rendering a specific reflection target.

The current indirect command builder uses `SourceViewId`. A mirror reflection render can set `SourceViewId` to the mirror view being rendered. This keeps the v1 GPU path simple: one reflection target render per selected mirror view, but each pass is GPU-culled and multi-draw indirect.

### True multi-view rendering

Rendering several mirrors in one multi-draw call is possible later, but should not be required for v1.

To draw several mirror views in one graphics pass, the engine would need one of these:

- layered rendering to a `Texture2DArray`, with vertex or geometry shader writing `gl_Layer`
- viewport arrays and per-view scissor/viewport selection
- separate indirect command regions per mirror target and material batch
- shaders that resolve view constants from an encoded base instance or draw metadata

That path is attractive, but it is a second milestone. The first GPU milestone should use GPU culling and indirect draws per mirror view because it keeps the feature compatible with the current batching architecture.

### GPU path strengths

- CPU cost is bounded mostly by mirror scheduling, not per-object collection.
- Culling scales better for many visible mirrors.
- Reuses existing GPUScene buffers and material batching.
- Can share infrastructure with future stereo/foveated/portal work.

### GPU path risks

- View count capacity and per-view visible buffers must be budgeted carefully.
- Shader uniform setup must bind mirrored view constants consistently in generated and material-authored shaders.
- Reflection targets and per-view draw counts need good diagnostics.
- Multi-view layered rendering will touch shader generation, OpenGL state, and Vulkan translation.

## Reflection Target Layout

Use a reflection target pool owned by `XRRenderPipelineInstance`.

### Backing resource: one Texture2DArray *per tier*

A single `Texture2DArray` cannot mix layer sizes — every layer must share format, dimensions, and sample count. The earlier idea of "start with one array, add an atlas later" is a trap: putting Full + Half + Quarter tiers into one array forces all three to allocate at Full size, defeating the entire point of tiers.

v1 layout:

| Tier | Backing resource | Suggested Size | Use |
|---|---|---:|---|
| Full | `Texture2DArray` (small layer count, e.g. 2) | main internal resolution clamped to max mirror size | hero mirror near camera |
| Half | `Texture2DArray` (e.g. 4) | 50 percent of internal resolution | default visible mirror |
| Quarter | `Texture2DArray` (e.g. 8) | 25 percent of internal resolution | small or distant mirror |
| Frozen | reuses last frame's layer | n/a | low-importance mirror |

Layer counts per tier are tunable and bounded by the recursion/budget policy below. The pool grows arrays in place when capacity is exceeded; identity-aware FBO recreation predicates apply per-array.

An atlas variant remains a future option for variable-resolution mirrors that don't fit any tier cleanly, but it requires mip-aware rect padding to avoid bleed across rect boundaries during blur and lod sampling. Defer until a concrete use case requires it.

Reflection resources should be cached with identity-aware FBO recreation predicates. Do not rely on size checks only.

## Stencil Policy

Stencil should be used for:

- mirror surface screen mask
- recursion depth guard
- optional debug visualization

Stencil should not be the only association between a reflection result and a mirror surface. The mirror material should receive an explicit mirror target index or atlas rect.

Important constraints:

- Most practical depth-stencil targets expose 8 stencil bits.
- Editor hover/selection already uses stencil bits in post-process.
- `MaxMirrorRecursionCount` currently allows values beyond what stencil can represent safely.

Recommended allocation:

| Bits | Use |
|---|---|
| 0-2 | mirror recursion depth or mirror mask |
| 3-7 | preserve existing editor/selection/complex-MSAA uses unless the active pipeline owns a different layout |

### Stencil bit registry is a Phase 1 prerequisite

A central `StencilBitLayout` static registry (or equivalent documented constant table) **must land before any mirror code claims stencil bits**. Without it, editor selection and complex-MSAA stencil marks will break during mirror development and the regressions will be hard to bisect. Treat this as a hard gate on Phase 1a, not a nice-to-have.

## Recursion Policy and View Budget

Mirror recursion must be explicit and conservative.

Recommended defaults:

- recursion disabled by default or capped to 1
- per-camera max reflection views per frame: 1 to 4
- per-mirror update rate scaling by importance
- hard global cap below `GPUViewSetLayout.AbsoluteMaxViewCount`

For recursive mirrors:

1. Main view schedules depth 0 mirrors.
2. Each reflection view may schedule depth 1 mirrors only if budget remains.
3. The scheduler rejects cycles by mirror id and view ancestry.
4. Lower recursion depths use lower resolution and skip transparency/post effects.

The existing `MaxMirrorRecursionCount` setting should become a high-level policy input, not a direct stencil bit count.

### View budget formula

The scheduler must reject mirrors **before** allocating any reflection target. With `GPUViewSet`'s 64-view hard cap, the budget arithmetic is:

$$
\text{views} = \text{mainViews} + \sum_{i=1}^{N_{\text{mirrors}}} \text{eyeCount}_i \cdot \text{recursionFactor}_i
$$

where `recursionFactor_i = 1 + (allowsRecursion ? expectedDepthMirrors : 0)`.

### View capacity reservation table

The scheduler reserves view slots from the 64-view cap before mirrors are added. Reservations are owned by other features and must be subtracted first:

| Reservation | Slots | Notes |
|---|---:|---|
| Main mono / stereo views | 1–2 | mono = 1, stereo = 2 |
| Foveated helper views | 0–2 | when foveated rendering active |
| Desktop VR mirror view | 0–1 | when running VR with a desktop window |
| Future portal/probe reservation | 1 | reserved for forward compatibility, do not allocate in v1 |
| **Reserved subtotal (worst case)** | **6** | |
| **Available for mirrors** | **≤ 58** | hard cap |

At stereo × recursion-depth-2 the practical mirror cap is ~14. The scheduler must reject below this number deterministically. Stable rejection priority: importance descending, then mirror id ascending (ties).

### Stable scheduling priority

Importance is computed from the **screen-space bounds of the mirror surface itself** (projected to the main camera), not from any analysis of the reflected scene contents. This avoids the chicken-and-egg of needing the reflection rendered to decide whether the reflection is worth rendering.

## Material Model

Create a dedicated mirror material path instead of using a generic textured quad.

Suggested properties:

- `ReflectionStrength`
- `TintColor`
- `Roughness`
- `RoughnessBlurMode`
- `NormalDistortionStrength`
- `EdgeFade`
- `DepthFade`
- `MaxUpdateRate`
- `ResolutionScale`
- `RenderTransparentObjects`
- `RenderParticles`
- `AllowRecursion`

Opaque perfect mirrors should render in an opaque forward mirror pass. Semi-transparent or dirty mirrors should render with transparent ordering after opaque reflections are available.

For rough mirrors, v1 can sample lower mips or use a cheap separable blur on the reflection texture. Full physically correct glossy planar reflection can wait.

## Pipeline Ordering

Recommended ordering inside `DefaultRenderPipeline`:

1. Main camera collect visible.
2. Mirror scheduler ranks visible mirrors.
3. Reflection target resources are allocated or reused.
4. Reflection views render before the main forward mirror composite.
5. Main deferred/forward opaque scene renders.
6. Mirror surfaces composite as a forward opaque/special pass.
7. Transparent scene renders.
8. Post-processing runs once for the final camera image.

Two viable orderings exist for step 4 and step 5:

| Ordering | Benefit | Cost |
|---|---|---|
| Reflection views before main scene | Mirror texture ready when mirror surface draws | Reflection work may run for mirrors later occluded by main scene |
| Main depth prepass before reflection views | Can reject occluded mirrors and estimate screen area accurately | Requires splitting mirror scheduling after depth |

Start with reflection views before main scene for simplicity. With v1's 1–4 mirror budget and screen-bounds importance (which is computable without main-scene depth), this ordering is correct and simple. Depth-prepass-informed scheduling becomes attractive only when the budget grows or full-screen mirrors are commonly occluded — defer until profiling justifies it.

## VR and Stereo

Stereo mirrors have two options:

1. Render one reflection per eye.
2. Render one shared cyclopean reflection.

Default should be one reflection per eye for near/large mirrors, because incorrect parallax is obvious in XR. Use shared/cyclopean reflection only for distant or low-priority mirrors.

GPU view scheduling must account for stereo multiplication:

- main stereo views: 2
- mirror views: `visibleMirrors * eyeCount`
- foveated helper views: optional additional views
- desktop VR mirror view: optional

The scheduler must reserve view capacity before adding mirrors. It should degrade resolution or update rate before exceeding the view cap.

## CPU/GPU Shared API

Introduce a small shared model:

```csharp
public readonly struct MirrorRenderRequest
{
    public int MirrorId { get; init; }
    public Plane Plane { get; init; }
    public Matrix4x4 MirrorWorldMatrix { get; init; }
    public BoundingBox ScreenOrWorldBounds { get; init; }
    public int RecursionDepth { get; init; }
    public float Importance { get; init; }
    public MirrorQualityTier QualityTier { get; init; }
    public MirrorRenderProfile Profile { get; init; }

    // Update-rate throttling state. Owned by the request struct so the scheduler
    // is stateless w.r.t. the component. Set by the provider from its last assignment.
    public ulong LastRenderedFrame { get; init; }
    public float MaxUpdateRateHz { get; init; }
}

public readonly struct MirrorViewAssignment
{
    public int MirrorId { get; init; }
    public uint ViewId { get; init; }
    public uint TargetTier { get; init; }   // selects which Texture2DArray
    public uint TargetLayer { get; init; }  // index into that array
    public Matrix4x4 View { get; init; }
    public Matrix4x4 Projection { get; init; }
    public Plane ClipPlane { get; init; }
    public ulong AssignedFrame { get; init; }
}
```

`LastRenderedFrame` and `MaxUpdateRateHz` are required in v1 even though Phase 3 (Quality and recursion) is where update-rate throttling is *implemented* — putting the fields in the struct from day one means Phase 3 is a scheduler change with no API churn.

The exact types should follow local engine conventions, but the split matters:

- request: component says "I might need a reflection"
- assignment: renderer says "this is what you get this frame"

Components should not own reflection FBOs. The pipeline instance should own the pool so resizing, HDR output, MSAA, and device lifecycle remain centralized.

## Diagnostics

Add rendering diagnostics before trying to tune quality:

- active mirror requests
- accepted/rejected mirrors with reason
- per-mirror target size/layer
- CPU collection time per reflection
- GPU cull count per reflection view
- indirect draw count per reflection view
- reflection render time
- skipped frame/update rate counters
- stencil layout debug overlay

The ImGui renderer diagnostics panel should expose a mirror section. Logs should use the existing rendering category and throttle noisy messages.

## Compatibility Matrix

| Feature | CPU v1 | GPU v1 | Later |
|---|---|---|---|
| Single flat mirror | Yes | Yes | Yes |
| Multiple visible mirrors | Yes, budgeted | Yes, budgeted | Better batching |
| Recursive mirrors | Limited | Limited | More robust scheduling |
| Deferred reflected scene | Yes, reduced chain | Yes, reduced chain | Full quality tiers |
| Forward reflected scene | Yes | Yes | Yes |
| Transparent reflection | Optional | Optional | Better ordering/OIT |
| Stereo VR mirrors | Per-eye or shared | Per-eye or shared | Single-pass/layered |
| Rough mirror blur | Mip/cheap blur | Mip/cheap blur | Importance-sampled blur |
| Vulkan | Design-compatible | Design-compatible | Dedicated implementation |

## Implementation Phases

Phase 1 is split into three smaller, independently shippable milestones. Each one ends with a working build and a measurable improvement; none of them tries to land the whole CPU path in one drop.

### Phase 1a: Single mirror, single profile, single tier

Prerequisite (hard gate, see *Stencil Policy*): central `StencilBitLayout` registry landed.

Deliverables:

- Rename or replace `AdvancedForwardMirrorComponent` with `AdvancedFlatMirrorComponent` implementing `IPlanarReflectionProvider`.
- Pipeline-owned mirror scheduler and reflection target pool (Half tier `Texture2DArray` only, layers = 1).
- `MirrorRenderRequest` / `MirrorViewAssignment` types as specified, including `LastRenderedFrame`/`MaxUpdateRateHz` fields (unused in 1a).
- `MirrorRenderProfile.OpaqueWithSky` only — forward reflected scene, no transparency, no post.
- `MirrorMaterialVariantFactory.CreateMaterialVariant` for the `OpaqueWithSky` profile.
- Forward composite of mirror surface with the new variant.
- No stereo, no recursion (depth = 0 hard).
- **Golden test scene** added to the unit-testing world: 1 hero mirror, 2 small mirrors, 1 mirror facing another mirror (recursion disabled, so the facing pair just shows depth-0 reflections of each other), 1 animated character. Verifies normals/winding/stencil/budget visually.

Exit criteria: golden test scene renders without normal-flip artifacts, stencil registry holds, no editor-selection regressions, no warnings introduced.

### Phase 1b: Profiles and tiers

- Add `OpaqueOnly`, `OpaqueTransparent`, `Cinematic` profiles with their respective material variants.
- Add Full and Quarter tier `Texture2DArray`s and tier selection by importance.
- Add Cinematic-only deferred reflected scene path.
- Add user-clip-plane fallback for `Cinematic` profile.
- Multiple visible mirrors (still mono, still depth-0).

### Phase 1c: Stereo

- Per-eye reflection scheduling for near/large mirrors.
- Shared cyclopean reflection for distant/low-priority mirrors.
- View capacity reservation enforcement against the 64-view cap.
- Stereo entries added to the golden test scene.

### Phase 2: GPU-view integration

- Replace fixed-size view descriptor construction with a dynamic view builder.
- Add mirror views to `GPUViewSet`.
- Render one mirror reflection target at a time using `SourceViewId`.
- Add per-view debug counts and validation.
- Keep CPU path as fallback and correctness oracle.

### Phase 3: Quality and recursion

- Add importance scoring, update-rate throttling, and resolution tiers.
- Add controlled recursion with ancestry rejection.
- Add roughness blur or mip-chain filtering.
- Add optional transparent reflection policy.

### Phase 4: Multi-view/layered optimization

- Render multiple mirror views into texture array layers in fewer graphics passes.
- Use layered rendering or viewport arrays where supported.
- Update shader generation to resolve per-view constants robustly.
- Keep fallback paths for hardware/API combinations that cannot support the optimized route.

## Resolved Design Questions

### Shadow maps in mirrored views

**Decision:** v1 reuses main-frame shadow maps as-is, with documented artifacts.

Verified in code: `DirectionalLightComponent.CascadeShadows` (`IncludeCascadeBoundsInLightSpace`) fits each cascade's ortho bounds by projecting the **camera frustum corners** into light space. The cascades are camera-frustum-fit, not light-space-aligned to the world. This means a mirrored frustum that sees regions outside the main frustum will sample cascades that don't cover those regions, producing wrong/missing shadows at grazing angles or behind the camera.

This is a *correctness* issue, not just quality. v1 ships with this limitation explicitly documented; the editor diagnostics overlay flags reflection regions outside main cascade coverage. A follow-up task adds light-space-aligned cascade fitting (or a separate cascade fit per active reflection view, budget permitting) once mirrors are stable.

### `MirrorCaptureComponent` migration

**Decision:** keep `MirrorCaptureComponent` as a thin wrapper that submits a `MirrorRenderRequest` with `Importance = User`, user-controlled `MaxUpdateRateHz`, and `Profile = Cinematic`. One code path through the new scheduler, two authoring surfaces (`AdvancedFlatMirrorComponent` for general use, `MirrorCaptureComponent` for hand-authored render-to-texture cases). No legacy second pipeline.

## Open Questions

- Should the mirror target pool live directly in `DefaultRenderPipeline` partials or in a reusable `PlanarReflectionManager` service used by pipelines?
- Should mirror reflection targets use HDR formats identical to `HDRSceneTex`, or a smaller format like `R11G11B10F` for bandwidth?
- How should mirror surfaces interact with editor selection stencil and complex-MSAA stencil marks?
- How much of the post-process chain should be opt-in for `Cinematic`-profile mirrors?

## Final Recommendation

Build the feature as a renderer-owned planar reflection system with a CPU-first implementation and a GPU-view path designed in from day one.

Use forward composition for mirror surfaces. Let reflection views reuse a reduced version of the active forward/deferred scene path, but keep post-processing mostly outside reflection views. Treat GPU-driven rendering as an acceleration of view culling and indirect submission, not as a requirement for correctness. This gives XRENGINE a clean v1 mirror feature that works today and has a credible route to high-performance multi-mirror rendering later.
