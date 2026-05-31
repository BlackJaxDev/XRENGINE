# Retinal Visibility Cache Rendering Design

Last Updated: 2026-05-29
Status: design proposal
Scope: advanced quad-view foveated VR rendering for opaque geometry, shared lighting, transparency fallback, and staged integration with XREngine's current OpenXR and GPU-driven renderer work.

## Related Docs

- [OpenXR VR Rendering](../../../architecture/rendering/openxr-vr-rendering.md)
- [OpenXR Future Work TODO](../../todo/rendering/vr/openxr-future-work-todo.md)
- [Engine Rendering Optimization Design](engine-optimization-and-avatar-optimizer-design.md)
- [GPU Meshlet Zero-Readback Rendering Design](gpu-meshlet-zero-readback-rendering-design.md)
- [Dynamic Indirect Material Bindings](dynamic-indirect-material-bindings.md)
- [Mesh Submission Strategies](../../../architecture/rendering/mesh-submission-strategies.md)
- [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md)
- [Transparency And OIT Implementation Plan](transparency-and-oit-implementation-plan.md)

## External References

- OpenXR quad-view config `XR_VIEW_CONFIGURATION_TYPE_PRIMARY_QUAD_VARJO` (from `XR_VARJO_quad_views`, promoted into OpenXR 1.1): <https://registry.khronos.org/OpenXR/specs/1.1/man/html/XR_VARJO_quad_views.html>
- OpenXR `XR_VARJO_foveated_rendering` view configuration sizing and dual-swapchain recommendation: <https://registry.khronos.org/OpenXR/specs/1.0/man/html/XrFoveatedViewConfigurationViewVARJO.html>
- Vulkan multiview: <https://docs.vulkan.org/refpages/latest/refpages/source/VK_KHR_multiview.html>
- Direct3D 12 variable rate shading: <https://microsoft.github.io/DirectX-Specs/d3d/VariableRateShading.html>
- Vulkan fragment shading rate sample: <https://docs.vulkan.org/samples/latest/samples/extensions/fragment_shading_rate/README.html>
- Visibility buffer rendering, Burns/Hunt: <https://jcgt.org/published/0002/02/04/>
- Decoupled Sampling for Graphics Pipelines: <https://people.csail.mit.edu/jrk/decoupledsampling/ds.pdf>
- Analytic texture gradients from barycentric partials (visibility-buffer material reconstruction): <https://momentsingraphics.de/ToyRenderer3RenderingBasics.html>
- Texture Level of Detail Strategies for Real-Time Ray Tracing (ray cones), Akenine-Moller et al.: <https://www.jcgt.org/published/0010/01/01/>

## 1. Summary

Retinal Visibility Cache (RVC) is a proposed production VR renderer for four-view foveated headsets. It renders visibility for each OpenXR view, but it does not shade the four views as four mostly independent images. Instead, each view writes compact visibility, the renderer converts visible samples into foveated "shadelets", and material plus stable lighting work is shared across the left eye, right eye, wide views, and foveated inset views whenever the underlying surface sample is close enough to reuse.

Opaque rendering is handled by:

```text
quad-view scene culling
  -> four visibility buffers
  -> foveated shadelet request generation
  -> material shadelet cache
  -> shared head-space light clusters
  -> reusable lighting cache
  -> per-view resolve with eye-specific specular correction
```

Transparent rendering remains a companion Forward+ path using the same light cluster data and foveation policy.

The design target is simple:

- Run visibility at the quality VR needs for stable stereo geometry.
- Run expensive material and lighting work at the frequency the retina needs.
- Share material evaluation, diffuse lighting, ambient/probe lighting, shadow visibility, and broad specular across views.
- Keep sharp specular, reflection, refraction, and screen-space effects view-specific.
- Avoid four full G-buffers.

In one sentence: RVC renders four visibility buffers, then tries very hard not to shade four full images.

## 2. Why This Exists

XREngine is Windows-first, OpenGL 4.6 is the current production backend, and Vulkan/DX12-class explicit APIs are the long-term path for advanced VR renderer features. The engine already has:

- OpenXR and OpenVR render paths.
- A three-phase OpenXR frame model with predicted and late poses.
- `RenderVRSinglePassStereo`.
- A disabled-by-default foveated `RenderCommandCollection` ViewSet path through `EnableVrFoveatedViewSet`.
- Forward+ local light buffers and debug visualization.
- GPU indirect, zero-readback, meshlet, material table, skinning, blendshape, and BVH work in progress.

Those pieces point toward a renderer where the GPU owns more frame work and the CPU no longer treats each eye as an isolated render pass. Quad-view foveation makes that pressure stronger. A four-view headset can require:

```text
left wide view
right wide view
left high-resolution inset view
right high-resolution inset view
```

The OpenXR quad-view configuration `XR_VIEW_CONFIGURATION_TYPE_PRIMARY_QUAD_VARJO` (defined by `XR_VARJO_quad_views` and promoted into OpenXR 1.1 under that name) formalizes this model. There is currently no vendor-neutral "stereo with foveated inset" view configuration token; the Varjo quad-view config is the authoritative path. View indices 0 and 1 are the regular left and right stereo (context/wide) views; indices 2 and 3 are the left and right foveated inset (focus) views. Each inset view shares its eye's pose with the corresponding wide view, may be higher density for the same or narrower field of view, and the runtime may composite the inset on top of the wide view and blend at the boundary. Therefore, the outer view should remain valid under the inner region, but the application can render that region at lower shading quality.

The naive implementation is "render four cameras." That is correct but wasteful. RVC treats the four views as four visibility queries into one shared scene-lighting system.

## 3. Problems With Conventional Paths

### 3.1 Classic Deferred

Deferred shading is attractive because opaque lighting is performed once per visible pixel, but quad-view VR turns G-buffer bandwidth into the central cost:

```text
four views * high sample count * depth + normal + albedo + roughness + metallic + velocity + material data
```

A deferred path also struggles with transparency, MSAA, alpha test details, and view-dependent effects. It can be made to work, especially for opaque-heavy PC VR, but four G-buffers are still four G-buffers.

### 3.2 Classic Forward+

Forward+ is flexible and fits XREngine's current material/shader shape better than a large deferred rewrite. It handles transparency and MSAA more naturally, and it can use multiview and variable rate shading. The drawback is duplicated material and lighting work:

```text
shade left wide
shade right wide
shade left inset
shade right inset
```

It is a good fallback and an excellent Stage 1 baseline. It is not the final architecture if the goal is to exploit quad-view foveation deeply.

### 3.3 Visibility Buffer Without Sharing

A visibility buffer replaces bulky G-buffer attributes with compact primitive and instance references. This is the right opaque foundation because it keeps visibility high quality while deferring material reconstruction until after visibility is known. On its own, though, it still does not guarantee reuse across eyes or foveation layers.

### 3.4 Pure Object-Space Shading Cache

Object-space or surface-space shading can reuse work across both eyes, but it is risky as the only renderer. It interacts badly with sharp specular, parallax, refraction, animated UVs, procedural materials, tiny triangles, deformation, and rapidly moving gaze. RVC borrows the cache idea but keeps per-view visibility authoritative.

## 4. Design Decision

Use a hybrid:

- Opaque geometry: Retinal Visibility Cache.
- Alpha test: visibility path when material can produce a deterministic coverage test without expensive shading; otherwise Forward+ fallback.
- Transparent geometry, particles, glass, water, refractive materials: foveated clustered Forward+ overlay.
- Post-processing: foveated TAA/reprojection, edge-aware upsampling, and OpenXR inset/outer composition support.

RVC is allowed to break current renderer APIs because XREngine has not shipped v1. The design should favor clean renderer architecture over compatibility with legacy deferred or forward-only assumptions.

## 5. Goals

- Support OpenXR four-view foveated rendering as a first-class render mode.
- Keep visibility high enough to avoid stereo mismatch, edge crawl, and geometry shimmer.
- Reduce material texture fetch duplication across both eyes and inset/outer layers.
- Reduce diffuse, ambient, shadow, and broad specular lighting duplication.
- Keep view-dependent lighting correct enough for glossy VR surfaces.
- Reuse the engine's GPU ViewSet, Forward+ light metadata, material table, and GPU-driven rendering investments where possible.
- Avoid per-frame CPU allocations and avoid GPU readbacks in the shipping path.
- Make quality scalable by foveation region, not just by render-target resolution.
- Keep a conservative Forward+ path as the correctness fallback.

## 6. Non-Goals

- Do not make RVC the first implementation slice.
- Do not force all transparency through the visibility cache.
- Do not require every shader to support compute-side material reconstruction immediately.
- Do not assume stereo sharing is always safe.
- Do not depend on one vendor runtime. OpenXR 1.1 quad-view support is the target; vendor extensions are capability paths.
- Do not remove the current Forward+ renderer before RVC is measurable and debuggable.

## 7. Terms

| Term | Meaning |
| --- | --- |
| Wide view | The lower-density full field-of-view view for one eye. |
| Inset view | The higher-density foveated view for one eye. |
| Quad-view | The set of left wide, right wide, left inset, and right inset views. |
| Visibility sample | A depth sample plus compact primitive, meshlet, instance, and material references. |
| Shadelet | A cached surface shading record produced from one or more visibility samples. |
| Shadelet key | A packed identity for deduplicating shade requests. |
| Shared lighting | View-independent or weakly view-dependent lighting stored once per shadelet. |
| Per-view resolve | The pass that converts shadelets back into final color for one view/layer. |
| Head-space cluster | A light-culling cluster defined in headset/head space rather than in one eye's view space. |
| Peripheral aggregate | A compressed lighting representation for many low-impact peripheral/far-field lights. |

## 8. Current XREngine Integration Points

The current `EnableVrFoveatedViewSet` path is a useful internal seed, but it is not yet true OpenXR quad-view presentation:

- `GPUViewSet` already supports `FullRes`, `Foveated`, `Mirror`, and `UsesSharedVisibility` flags.
- `RenderCommandCollection.ConfigureGpuViewSet` can add foveated child views when stereo rendering is active.
- The current foveated child views reuse the same camera constants and viewport dimensions as the parent eye, with foveation metadata in `FoveationA` and `FoveationB`.
- `GPUViewSetLayout.DefaultMaxViewCount` is already high enough for left/right, two insets, mirror, and future diagnostic views.
- OpenXR swapchain creation currently creates per-eye swapchains. True quad-view support must enumerate the selected OpenXR view configuration, allocate swapchains for all reported views, and carry per-view recommended sizes and FOVs through the frame lifecycle.

RVC should not bolt onto the current two-eye renderer as a special case. It should promote the concept of a frame view set:

```text
RenderFrameViewSet
  View[0] left wide
  View[1] right wide
  View[2] left inset
  View[3] right inset
  optional mirror/debug views
```

Every renderer path then consumes a `RenderFrameViewSet` instead of assuming one camera or exactly two eyes.

## 9. OpenXR Quad-View Contract

OpenXR-driven quad-view rendering has several hard requirements:

- The application must select the view configuration (`XR_VIEW_CONFIGURATION_TYPE_PRIMARY_QUAD_VARJO`) before `xrBeginSession`; it cannot switch from stereo to quad-view mid-session without ending and recreating the session.
- View count must be runtime-reported. Code must not hard-code 2.
- View indices 0 and 1 are left and right wide (context) views.
- View indices 2 and 3 are left and right inset (focus) views.
- The inset shares its eye's pose with the corresponding wide view. The runtime may composite the inset on top of the wide view and may blend at the inset boundary, so the inset is not guaranteed to be strictly contained in the wide projection in every runtime build; do not rely on geometric containment for correctness.
- The inset and wide view poses for the same eye must match, but the inset FOV can change at runtime.
- The app should keep the wide view valid under the inset region because the runtime may sample it during edge blending.
- Foveated and non-foveated `XrViewConfigurationView` sizes differ. Per the Varjo spec, applications should enumerate both a foveated-active and a non-foveated view set and maintain two sets of swapchains (or one oversized set with two viewport layouts), selecting the foveated set only when rendering gaze is available.

Implications for XREngine:

- Swapchain dimensions, viewport rectangles, and projection matrices are per view, not per eye.
- Foveal center and inset FOV must be updated from `xrLocateViews` each frame.
- Culling can share work, but visibility must remain per view.
- The outer view can reduce shading quality inside the inset, but cannot leave holes.
- Temporal history must track view identity and foveation region, not just eye identity.

## 10. Pipeline Overview

```text
Pass 0  GPU scene preparation
        instance, meshlet, LOD, skinning/blendshape visibility metadata

Pass 1  Quad-view visibility
        Depth[4] + VisibilityID[4] + optional velocity/material side data

Pass 2  Foveated shade request generation
        visibility samples -> deduplicated shadelet keys -> pixel-to-shadelet maps

Pass 3  Material shadelet cache
        reconstruct attributes, sample textures, decode material parameters

Pass 4  Shared lighting
        head-space light clusters, top-K exact lights, peripheral aggregates

Pass 5  Per-view resolve
        shadelet lookup, eye-specific specular, color output per OpenXR view

Pass 6  Transparent Forward+ overlay
        glass, water, particles, refractive and order-dependent materials

Pass 7  Foveated post and composition
        TAA/reprojection, upsampling, inset/outer edge behavior, mirror
```

The pass boundaries can be fused later. The first implementation should keep them explicit so counters, visualizers, and fallback paths are easy to validate.

## 11. Pass 0: GPU Scene Preparation

This pass prepares a shared scene submission for all views.

Responsibilities:

- Update GPU-visible instance records.
- Run or consume skinning and blendshape compute outputs.
- Select meshlet/LOD candidates.
- Produce per-view or multi-view visibility masks.
- Prepare shadow caster lists.
- Classify materials into RVC-compatible opaque, alpha-test, transparent, and fallback forward buckets.

LOD selection must be foveation-aware but stereo-stable:

| Region | LOD Policy |
| --- | --- |
| Inset/fovea | Highest necessary geometric LOD and texture residency. |
| Mid-field | Medium LOD with stable hysteresis. |
| Wide periphery | Lower LOD allowed, but shared across both eyes when possible. |
| Near UI/hands/controllers | Force high quality regardless of gaze region. |

LOD must not pop differently between eyes. Use a cyclopean/head-space metric for base LOD, then allow only conservative foveation refinements. Prefer continuous/cluster-based LOD (Nanite-style selection) where available, because discrete LOD swaps that fire on one eye before the other are visible as eye-dominance flicker. If only discrete LOD is available, bind LOD hysteresis to head-space distance and apply the same decision to both eyes of a stereo pair.

## 12. Pass 1: Quad-View Visibility

Each view writes depth and compact identity. No material shading, no lighting, and ideally no texture sampling except alpha-test coverage when needed.

Recommended outputs:

```text
Depth:        D32F or D24S8 per view/layer
Visibility:   R32_UINT minimum, RG32_UINT when IDs do not fit
Velocity:     optional compact velocity or previous-clip side buffer
Coverage:     optional alpha-test/coverage side data
```

Possible visibility packing:

```c
struct VisibilitySample
{
    uint PrimitiveOrMicroTriId;
    uint InstanceOrMeshletId;
};
```

If a 32-bit path is needed:

```text
bits  0..19  local primitive or micro-triangle id
bits 20..29  meshlet id within draw/instance
bits 30..31  material/visibility mode tag
```

The 64-bit/RG32 path is preferred for v1 correctness because XREngine supports large imported scenes and GPU-driven draw identifiers. Later compaction can be data-driven.

Visibility pass rules:

- Use array layers or per-view images according to backend and runtime support.
- Use multiview/instanced rendering when available to reduce CPU and command overhead.
- Use per-view scissors/viewport rectangles so inset views do not over-render.
- Preserve enough depth precision for reprojection and edge-aware resolve.
- Keep alpha-test support deterministic and cheap. Materials that need expensive alpha logic fall back to Forward+.
- Avoid heap allocation and CPU readback.

Backend notes:

- OpenGL 4.6 can prototype with layered FBOs, SSBOs, image load/store, and multi-draw indirect, but true multiview (`OVR_multiview`/`OVR_multiview2`) is vendor/extension-sensitive and patchy on desktop NVIDIA. Do not commit the multiview visibility path to the OpenGL prototype; emulate with layered FBOs only for correctness and treat Vulkan as the multiview target.
- Vulkan is the intended backend for true multiview, fragment shading rate, explicit barriers, and future async overlap.
- DX12 is a future peer for VRS and view instancing if/when a backend exists.

## 13. Pass 2: Foveated Shade Request Generation

This compute pass converts view visibility into shadelet work.

For each visible pixel/sample:

```text
region      = classifyFoveationRegion(view, pixel, gaze, depth, material)
rate        = shadingRateFor(region, material, velocity, edgeDistance)
shadeCoord  = quantize(pixel, rate)
key         = makeShadeletKey(visibility, shadeCoord, material, lodBucket)
shadeletId  = appendOrFindUnique(key)
PixelMap[view][pixel] = shadeletId
```

Suggested shading rates:

| Region | Default Shadelet Density | Notes |
| --- | ---: | --- |
| Foveal inset | 1 per pixel | Allow supersample only for high-contrast material classes. |
| Foveal guard band | 1 per pixel or 1 per 2x2 | Hides gaze latency and inset movement. |
| Mid-field | 1 per 2x2 | Full material, reduced expensive lighting. |
| Periphery | 1 per 4x4 or 1 per 8x8 | Aggregate lighting and simpler BRDF. |
| Near UI/controllers | 1 per pixel | Overrides foveation region. |

Where the GPU exposes Tier 2 variable rate shading (DX12 shading-rate image or `VK_KHR_fragment_shading_rate`), the periphery coarsening can be expressed directly as a per-tile shading-rate image instead of compute-side shadelet quantization. This is the recommended fast path for materials that are not yet ported to compute-side material reconstruction: it captures most of the peripheral shading win with no compute derivative work and lets Stage 3 ship before the full shadelet cache exists. Compute-side shadelet quantization remains the path for cross-view/stereo reuse and for backends without VRS.

The shadelet key must be conservative enough to prevent visible reuse errors:

```c
struct ShadeletKey
{
    uint ViewGroup;              // head-space stereo/quad group or strict per-view group
    uint InstanceId;
    uint PrimitiveId;
    uint MaterialId;
    uint QuantizedBaryUv;
    uint LodBucket;
    uint RoughnessBucket;        // gates stereo/broad-specular sharing (see Pass 4/5)
    uint DeformationVersion;     // skinning/blendshape/instance deform id; invalidates stale reuse
    uint RegionQuality;
};
```

Deduplication can be implemented in stages:

1. No dedupe: one shadelet per requested sample group.
2. Intra-view dedupe: coarse foveated blocks share one shadelet.
3. Intra-eye dedupe: wide and inset samples for the same eye share stable surface work.
4. Stereo dedupe: left/right samples share material and reusable lighting when barycentric, normal, roughness-bucket, and deformation-version agreement pass thresholds.
5. Temporal dedupe: persistent cache entries survive across frames when camera, gaze, material, and deformation allow it.

The shipping rule is opportunistic reuse. If a sample cannot prove that a shadelet is safe to reuse, it shades locally. Stereo dedupe (stage 4) is the most novel and most artifact-prone step and must be opt-in per material with an A/B validation harness; see Stage 5 in the implementation plan. Parallax-occlusion and virtual-displacement materials produce different surface hits per eye even at identical macro-triangle barycentrics, so they must either be excluded from stereo sharing or keyed on the displaced surface point rather than the macro-triangle barycentric.

## 14. Pass 3: Material Shadelet Cache

This pass reconstructs material inputs for each unique shadelet. Before reconstruction, run a material count/reorder step (count samples per material, sort shadelet work into per-material runs, shade coherently, scatter back). This is the standard visibility-buffer optimization and is required at scale to keep the bindless material access coherent; see the cross-domain extensions section.

Inputs:

- Visibility ID.
- Depth and screen position for a representative sample.
- Instance transform and previous transform.
- Meshlet/primitive metadata.
- Vertex/index buffers.
- Material table row.
- Bindless or descriptor-indexed textures.
- Foveation region and quality flags.

Outputs:

```c
struct MaterialShadelet
{
    float3 PositionWS;
    half3  NormalWS;
    half3  TangentWS;
    half3  BaseColor;
    half   Roughness;
    half   Metallic;
    half   AmbientOcclusion;
    half3  Emissive;
    uint   MaterialFlags;
};
```

Material evaluation responsibilities:

- Reconstruct barycentrics from screen position, depth, and primitive data.
- Fetch vertex attributes.
- Evaluate material constants.
- Sample base color, normal, roughness/metallic, emissive, and occlusion textures.
- Decode normal maps.
- Apply material LOD and texture residency policy.

Texture derivatives are the hardest material-cache issue. Compute shaders do not automatically provide pixel shader quad derivatives. The default, production-grade solution is analytic gradients, not neighbor sampling:

- Default: reconstruct UV gradients analytically from the barycentric partial derivatives of the visible triangle (screen-space ray-differential / barycentric-partials method). This produces correct `textureGrad` footprints for an isolated compute sample and does not depend on neighbor threads sharing a triangle or material.
- Reflections and peripheral coarse shadelets: estimate footprints with ray cones (Akenine-Moller et al.) or depth/normal footprint estimates.
- Quad-neighbor derivatives are only valid inside a 2x2 thread group that lies on the same triangle and material, which a coarse-shadelet pass cannot guarantee; do not rely on them as the primary source.
- Bias mips in the periphery as a tuning knob to suppress shimmer, not as a substitute for correct gradients.
- Force pixel shader fallback only for materials whose procedural texture graph genuinely requires implicit derivatives that cannot be expressed analytically.

This pass should integrate with the dynamic material binding work. RVC-compatible materials need generated material reconstruction code, not hand-maintained duplicate shader structs.

## 15. Pass 4: Shared Head-Space Lighting

Instead of building four independent Forward+ grids, RVC builds one headset-frame light structure.

Traditional Forward+:

```text
eye view-space tile/froxel -> light list
```

RVC:

```text
head-space or world-space cluster -> light list or aggregate
```

Each shadelet maps `PositionWS` into this shared cluster structure. The views still have different projections, but local light culling is not duplicated per eye/layer.

Cluster data:

```c
struct ClusterLightList
{
    uint Offset;
    uint Count;
};

struct ClusterLightAggregate
{
    half3 DiffuseSH0;
    half3 DiffuseSH1;
    half3 DiffuseSH2;
    half3 DiffuseSH3;
    half3 DominantDir;
    half3 DominantColor;
    half  Energy;
};
```

Recommended cluster output:

```text
foveal and near-field clusters:
  exact local light list

mid clusters:
  top N exact lights + optional aggregate

peripheral/far clusters:
  top K exact lights + aggregate of the rest
```

Shared lighting output:

```c
struct SharedLighting
{
    half3 DiffuseDirect;
    half3 DiffuseIndirect;
    half3 BroadSpecular;
    half  ShadowMask;
    half  Confidence;            // see cross-domain extensions: shading-cache confidence/age
    uint  Age;                   // frames since last full evaluation
    uint  Validity;
};
```

For many-light scenes, the shared lighting record may store a small spatiotemporal reservoir per shadelet instead of (or alongside) a fixed top-K list. Reservoir resampling provides a principled invalidation and MIS story and combines gracefully across the stereo pair; see the cross-domain extensions section. Reservoirs may be adopted at this pass or deferred to the temporal stage.

Reusable across eyes/layers:

- Texture fetches and material constants.
- Normal map decoding.
- Diffuse direct lighting.
- Ambient/probe lighting.
- Diffuse indirect lighting.
- Shadow visibility for most lights.
- Broad/rough specular approximation.

Per-view or per-pixel correction:

- Sharp specular.
- Fresnel.
- Reflection vectors.
- Screen-space reflections.
- Refraction/parallax.
- Clearcoat and anisotropic highlights.

Foveation affects lighting quality, not just pixel density:

The shared-versus-per-view specular split is driven by material roughness, not foveation region alone. As a practical default, broad specular may be shared across eyes/layers when surface roughness exceeds roughly 0.35; below that threshold (glossy, clearcoat, mirror-like) specular must be evaluated per view, at least in the fovea and guard band. The `RoughnessBucket` in the shadelet key gates this decision so glints and sharp highlights are never shared across the stereo pair.

| Region | Material | Lights | Shadows | Specular | Screen-Space Effects |
| --- | --- | --- | --- | --- | --- |
| Fovea | Full | Exact cluster list | High-quality filter | Per-view sharp | SSR/reflection allowed |
| Guard band | Full | Exact or top-N | Medium filter | Per-view sharp or mixed | Conservative |
| Mid-field | Full or minor simplification | Top-N + aggregate | Softer | Broad + cheap correction | Usually disabled |
| Periphery | Coarse shadelets | Top-K + aggregate | Stable/soft | Broad only or cheap correction | Disabled |

## 16. Pass 5: Per-View Resolve

The resolve pass maps each view's pixels back to shadelets:

```text
shadeletId = PixelMap[view][pixel]
material   = MaterialCache[shadeletId]
shared     = LightingCache[shadeletId]
viewDir    = normalize(EyePosition[view] - material.PositionWS)

finalColor =
    material.BaseColor * (shared.DiffuseDirect + shared.DiffuseIndirect)
  + shared.BroadSpecular
  + evaluateViewDependentSpecular(material, shared, viewDir, viewLightSubset)
  + material.Emissive
```

Resolve responsibilities:

- Perform eye-specific specular correction.
- Reconstruct per-pixel normals where coarse shadelets cross high-curvature surfaces.
- Preserve material IDs and motion data needed by temporal passes.
- Edge-aware upsample coarse shadelet results.
- Blend or reject shadelet reuse at depth/normal/material discontinuities.
- Generate per-view output for OpenXR composition.
- Keep wide-view shading valid under inset regions because the runtime may blend there.
- Shade disocclusion holes between the wide and inset views per view this frame. Never fill them from temporal history; stale history in a newly disoccluded region is visible as ghosting in VR.

Specular policy:

| Material Type | RVC Policy |
| --- | --- |
| Rough dielectric | Shared broad specular is usually enough outside fovea. |
| Glossy/clearcoat | Per-view specular in fovea and guard band. |
| Mirror-like | Forward/reflection-specific path or strict per-view shading. |
| Water/glass | Transparent Forward+ overlay. |
| Stylized unlit | Fully shareable unless shader is view-dependent. |

## 17. Pass 6: Transparency And Particles

Transparency stays in a companion clustered Forward+ path. This is a deliberate hybrid, not a temporary compromise.

Transparent categories:

| Category | Path |
| --- | --- |
| Alpha-test foliage | RVC visibility if alpha coverage is cheap and deterministic; otherwise Forward+. |
| Glass/water fovea | Per-pixel clustered Forward+. |
| Glass/water periphery | Coarse Forward+ shading plus temporal resolve. |
| Particles fovea | Weighted blended OIT or sorted where needed. |
| Particles periphery | Reduced density, coarse shading, weighted blended OIT. |
| Refraction | Per-view only. |

The transparent path should reuse:

- Head-space light clusters.
- Foveation region classification.
- Shadow budgets.
- RenderFrameViewSet view descriptors.
- Temporal jitter/reprojection metadata.

## 18. Pass 7: Foveated Post And Composition

Post-processing must become view-set aware.

Required behavior:

- Maintain temporal history per OpenXR view.
- Use foveation-aware jitter. Avoid jitter mismatch between wide and inset views.
- Use lower feedback in disoccluded or fast gaze-moving regions.
- Use stronger sharpening only where edge stability is good.
- Preserve UI/hands/controller clarity regardless of gaze.
- Keep wide-view content under the inset valid for runtime edge blending.
- Mirror composition should expose debug modes for wide, inset, shadelet density, and reuse rate.

The existing temporal accumulation pass should not assume exactly one desktop view or two equal-sized eyes once RVC work begins.

## 19. Memory Model

Let:

```text
V    = number of OpenXR views, usually 4
P_i  = pixel count for view i
G    = G-buffer bytes per sample
D    = depth bytes per sample
I    = visibility ID bytes per sample
S    = number of unique shadelets
M    = material shadelet bytes
L    = lighting shadelet bytes
C    = final color bytes per sample
```

Classic deferred roughly pays:

```text
sum(P_i * G) + depth + lighting reads/writes + final color
```

RVC pays:

```text
sum(P_i * (D + I)) + S * (M + L) + pixel-to-shadelet maps + final color
```

The win depends on:

```text
S << sum(P_i)
```

That inequality comes from:

- Coarse peripheral shadelets.
- Inset/wide shadelet reuse.
- Left/right material and stable-lighting reuse.
- Optional temporal reuse.
- Peripheral light aggregation.

A practical first target is not theoretical perfection. The first target is to beat four-view Forward+ in opaque-heavy scenes while matching its quality in the fovea. As a concrete, measurable gate, a typical scene with fovea at 1x1, mid-field at 2x2, periphery at 4x4 plus inset/wide and stereo reuse should land `S` in the range of roughly 20-35% of `sum(P_i)`. Stage 5 should adopt a measured `S < 0.5 * sum(P_i)` as an exit criterion rather than the vaguer "beats Forward+".

## 20. Resource Layout

Per view:

```text
DepthView[view]
VisibilityIdView[view]
OptionalVelocityView[view]
PixelToShadeletView[view]
ColorView[view]
```

Shared per frame:

```text
View descriptors/constants
Instance table
Meshlet/primitive table
Material table
Shadelet key table
Material shadelet cache
Lighting shadelet cache
Head-space cluster grid
Cluster light lists
Cluster aggregate list
Debug counters
```

Debug counters should include:

```text
visibility pixels per view
unique shadelets
shadelets by foveation region
intra-view reuse hits
inset/wide reuse hits
stereo reuse hits
temporal reuse hits
rejected reuse attempts
exact light evaluations
aggregate light evaluations
per-view specular corrections
fallback material count
transparent Forward+ draw count
```

All debug counters are written to GPU buffers and read back double-buffered, several frames late. They must never be read back synchronously inside the render loop, because that reintroduces the GPU stall the zero-readback design exists to avoid.

## 21. Gaze And Foveation Policy

Inputs:

- Runtime-reported inset FOV and pose from OpenXR.
- Eye gaze when available through runtime extension or engine input.
- User settings for fixed foveation fallback.
- Material and object overrides for UI, hands, controllers, and near-field interactables.

Policy:

- Smooth gaze enough to avoid high-frequency resource churn.
- Add a foveal guard band so late gaze movement does not reveal low-quality shading.
- Treat near UI and controller surfaces as foveal.
- Expand quality around high-contrast edges and fast motion.
- Decay temporal history faster when gaze crosses a region boundary.
- Keep foveation region decisions deterministic across both eyes unless a runtime view requires otherwise.

Proposed settings:

```text
OpenXrQuadViewMode = Off | RuntimePreferred | ForceStereoFallback
RvcOpaqueMode = Off | VisibilityOnlyDebug | MaterialCache | SharedLighting | Full
RvcFovealRadiusDegrees
RvcGuardBandDegrees
RvcMidFieldRadiusDegrees
RvcPeripheralMaxRate
RvcStereoReuseEnabled
RvcInsetWideReuseEnabled
RvcTemporalReuseEnabled
RvcPeripheralLightAggregationEnabled
RvcForceFullResNearDistanceMeters
RvcDebugOverlay
```

`RvcStereoReuseEnabled` defaults to off and is enabled per scene/per material only after the A/B harness validates that stereo reuse is artifact-free for that content. Materials may also carry a `MaxSharedRoughness` override that feeds the `RoughnessBucket` decision and an opt-out flag for parallax/displacement materials.

Existing `VrFoveationInnerRadius`, `VrFoveationOuterRadius`, `VrFoveationShadingRates`, and `VrFoveationFullResNearDistanceMeters` can inform early prototypes, but true quad-view should move toward runtime FOV/gaze-space units rather than only normalized viewport radii.

## 22. Scheduling And Synchronization

RVC must fit the OpenXR frame lifecycle:

```text
Prepare frame:
  xrWaitFrame
  xrBeginFrame
  locate predicted views
  publish RenderFrameViewSet

CollectVisible:
  build GPU work using predicted view set
  prepare view-dependent command buffers

Render:
  locate late views where allowed
  refresh final view constants
  execute visibility/material/lighting/resolve
  submit OpenXR layers
```

Important rules:

- No CPU readback in the render loop.
- Gaze/inset changes can update constants late, but must not require CPU-side command rebuilds where avoidable.
- GPU barriers between visibility, shade request generation, material cache, lighting cache, and resolve must be explicit in Vulkan.
- The eight passes, their resource lifetimes, aliasing, and barriers should be expressed through the frame graph described in the pipeline strategy section rather than hand-ordered. The barrier rules below assume that frame graph exists.
- OpenGL prototype must isolate barriers and image/SSBO memory semantics behind render-pass helpers.
- Async compute is future work. Do not require it for the first implementation.

Potential overlap later:

```text
graphics queue:  visibility
compute queue:   shade requests/material/lighting for previous visibility slice
graphics queue:  resolve/transparent/post
```

This is Vulkan/DX12 territory. OpenGL should stay simpler.

## 23. Backend Plan

### 23.1 OpenGL 4.6 Prototype

OpenGL is useful for:

- Proving visibility buffer formats.
- Proving shadelet request generation.
- Proving material-table reconstruction.
- Proving debug visualizers.
- Providing a fallback non-quad path on current production renderer.

OpenGL limitations:

- Multiview extension support is not guaranteed.
- Layered rendering and per-view viewport behavior may require conservative code paths.
- Explicit synchronization is harder to reason about.
- Runtime-owned OpenXR swapchains may make four-view array layouts awkward.

OpenGL should implement RVC slices only when they do not distort the clean Vulkan architecture.

### 23.2 Vulkan Target

Vulkan is the target for full RVC:

- Core multiview in Vulkan 1.1.
- Explicit render pass/dynamic rendering control.
- Descriptor indexing.
- Fragment shading rate where supported.
- Better fit for async compute and GPU-driven frame graphs.
- Cleaner integration with OpenXR Vulkan swapchain images.

### 23.3 DX12 Future

DX12 is not a current backend, but the design should remain compatible:

- View instancing.
- VRS Tier 2 shading-rate image.
- `ExecuteIndirect`.
- Mesh shaders.
- Descriptor heaps.

Do not bake Vulkan-only concepts into public engine APIs.

## 24. Implementation Stages

### Stage 0: View-Set Foundation

Goal: make render code view-count agnostic.

- Replace assumptions of exactly two VR views in renderer internals.
- Introduce `RenderFrameViewSet` as the runtime-owned view list.
- Preserve current stereo behavior.
- Add debug display for active views, sizes, FOVs, and parent/inset relationships.
- Add tests that quad-view descriptors do not exceed `GPUViewSetLayout` capacities.

### Stage 1: Quad-View Forward+ Baseline

Goal: correctness path before invention.

- Enumerate OpenXR view configurations and select `XR_VIEW_CONFIGURATION_TYPE_PRIMARY_QUAD_VARJO` when supported and enabled.
- Maintain both a foveated-active and a non-foveated `XrViewConfigurationView` set and allocate two sets of swapchains (or one oversized set with two viewport layouts), per the Varjo spec; pick the foveated set only when rendering gaze is available.
- Allocate swapchains for all reported views.
- Render all four views through Forward+.
- Use existing foveation settings only as quality hints; the runtime view config owns actual view count and dimensions.
- Keep stereo fallback green.
- Validate desktop mirror and editor preview.

Acceptance:

- Four views submit correctly on supporting runtime/hardware.
- Stereo fallback still works.
- GPU timings and pixel counts are logged per view.

### Stage 2: Opaque Visibility Buffer

Goal: replace opaque Forward+ shading with visibility + material + lighting + resolve, without sharing yet.

- Add visibility render target(s).
- Encode primitive/instance/material identity.
- Reconstruct material for visible samples.
- Resolve one view at a time.
- Keep Forward+ transparency.
- Add visualizers for visibility ID, primitive ID, material ID, depth, and reconstruction error.

Acceptance:

- One desktop view and stereo views match Forward+ within tolerance on opaque test scenes.
- Unsupported materials fall back visibly and correctly.

### Stage 3: Foveated Shadelets

Goal: decouple visibility rate from shading rate.

- Build shadelet keys from visibility.
- Add pixel-to-shadelet maps.
- Support 1x1, 2x2, 4x4, and 8x8 regions.
- Add a VRS-only fast path (DX12 shading-rate image / `VK_KHR_fragment_shading_rate`) for materials not yet ported to the compute material cache, so most of the peripheral win lands before the full shadelet cache exists.
- Add edge-aware rejection at depth/normal/material boundaries.
- Add shadelet density overlay.

Acceptance:

- Periphery shades fewer unique samples.
- Fovea remains visually equivalent to per-pixel resolve.
- No obvious block artifacts at depth/material discontinuities.

### Stage 4: Shared Head-Space Light Clusters

Goal: stop rebuilding light lists per view.

- Build a shared head-space cluster grid.
- Map shadelets to clusters.
- Reuse the existing Forward+ light metadata where possible.
- Add foveation-specific light budgets.
- Keep old Forward+ tile grid as fallback/debug comparison.

Acceptance:

- Opaque lighting matches per-view Forward+ for exact-light mode.
- Per-view light culling CPU/GPU cost drops for quad-view scenes.

### Stage 5: Inset/Wide And Stereo Shadelet Reuse

Goal: deduplicate material and stable lighting across views.

- Share shadelets between wide and inset views for the same eye.
- Share material shadelets between eyes when primitive, barycentric, material, normal, roughness-bucket, deformation-version, and LOD thresholds match.
- Exclude parallax-occlusion/virtual-displacement materials from stereo sharing, or key them on the displaced surface point.
- Share stable lighting between eyes.
- Keep per-view specular correction; never share specular below the roughness threshold.
- Default `RvcStereoReuseEnabled` to off and gate it per scene/material.
- Build an A/B validation harness (RVC stereo-reuse vs per-view shading) as a first-class deliverable of this stage.
- Add counters for reuse hits and rejected attempts.

Acceptance:

- Measured `S < 0.5 * sum(P_i)` on opaque-heavy test scenes.
- Reuse is measurable in real scenes.
- A/B harness shows no perceptible difference versus per-view shading on the validated content set.
- Specular highlights remain eye-correct in fovea.
- Disocclusions and one-eye-only surfaces shade locally.

### Stage 6: Peripheral Light Aggregation

Goal: handle many-light worst cases.

- Generate top-K exact lights per cluster.
- Compress the remaining low-impact lights into an aggregate.
- Use aggregate in mid/peripheral shadelets.
- Add debug overlays for exact vs aggregate lighting.

Acceptance:

- Many-small-light scenes do not spike periphery lighting cost.
- Aggregate lighting energy remains stable during head motion.

### Stage 7: Temporal Cache And Late Foveal Update

Goal: exploit frame-to-frame stability without hurting gaze latency.

- Add persistent shadelet cache entries for static/diffuse-friendly surfaces.
- Add invalidation for material parameter changes/animation, deformation, LOD changes, shadow caster set changes, and gaze region changes.
- Investigate late foveal inset refresh for Vulkan.

Acceptance:

- Static scenes show cache hit rate without ghosting.
- Gaze movement does not expose stale low-quality shading in the fovea.

## 25. Inventive Surface And Novelty Boundary

Most of RVC is a synthesis of proven techniques: the visibility buffer (Burns/Hunt), decoupled sampling (Ragan-Kelley et al.), clustered/Forward+ lighting, foveation and variable rate shading, and analytic visibility-buffer derivatives. Combining these is engineering, not invention; Nanite-class visibility rendering, real-time GI, and foveation already coexist in shipping engines.

The genuinely inventive surface is narrow and concentrated in three ideas. Validation effort, the A/B harness, and any future tech report should focus here:

1. The shadelet as a first-class cross-view deduplication unit, keyed by `{primitive, quantized barycentric, roughness bucket, deformation version}` and shared across all four OpenXR views. In-frame decoupled sampling and texture-space shading reuse exist, but reusing lit surface records across stereo eyes and foveal insets through one explicit cache keyed this way is the novel contribution.
2. Head-space (cyclopean) light clustering feeding four asymmetric-FOV views, with a per-cluster top-K-exact plus aggregate split. Culling once for stereo is established; promoting the cluster grid to a head-anchored 3D structure that all four views index is an uncommon generalization.
3. Organizing the entire renderer around retinal need rather than per-camera frames. This is an architecture/framing invention more than an algorithm, and it is the bet that justifies the rest.

Everything outside these three items should be treated as established art and implemented with off-the-shelf approaches rather than reinvented.

## 26. Cross-Domain Extensions

These techniques come from offline rendering, real-time GI, GPU compute, and ML rather than from VR specifically. Each replaces a hand-rolled, artifact-prone RVC mechanism with a more principled, better-tested one.

### 26.1 Reyes-Style Decoupled Shading Grids

The shadelet cache is conceptually a real-time Reyes shading cache. The Reyes literature already solved shading rate independent of visibility rate with stable derivatives via micropolygon grids and grid-space derivatives. Borrow grid quantization and grid-neighbor derivative computation instead of inventing shadelet-block neighbor logic from scratch. This reinforces the analytic-derivative path in Pass 3.

### 26.2 Shading Cache Confidence And Age

Add an explicit per-shadelet `Confidence` and `Age` field, as film GI irradiance caches do. This lets the resolve blend stale-but-cheap peripheral shadelets with fresh foveal ones smoothly, and it drives temporal invalidation more gracefully than a hard valid/invalid bit. These fields feed Stage 7.

### 26.3 Reservoir-Based Shared Lighting (ReSTIR)

The Stage 7 temporal cache is a weaker form of spatiotemporal reservoir resampling. Store a small reservoir per shadelet, especially for many-light scenes. This makes peripheral light aggregation (Pass 4) both cheaper and higher quality and provides a principled invalidation and MIS story. This is likely the largest quality-per-effort win available and should be evaluated before hand-rolling temporal accumulation.

### 26.4 Stereo Reuse As Reservoir Combination

Reframe stereo deduplication from a correctness-risky equality test into reservoir combination across the eye pair. Combining reservoirs degrades gracefully when the eyes disagree, directly mitigating the biggest artifact risk in Stage 5. Raw lit-record copy remains the fast path only when agreement thresholds are comfortably met.

### 26.5 Material Sort/Bin Before Shading

Before the material cache pass, run a compute pass that counts pixels/samples per material, reorders the visibility/shadelet work into per-material runs, shades coherently, then scatters back. This is the standard visibility-buffer optimization (material count plus reorder) and converts a divergent compute material pass into coherent per-material dispatches with cache-friendly bindless access. This is mandatory at scale, not optional, and belongs in Stage 3.

### 26.6 World-Space Hash-Grid Temporal Store

Back the temporal shadelet cache with a world-space spatial hash (position plus normal plus roughness), in the style of a world-space hash radiance cache, rather than relying on primitive plus barycentric identity. Primitive/barycentric identity breaks under LOD and topology change; a spatial hash persists shadelet identity across frames far more robustly. This becomes the storage backend for Stage 7.

### 26.7 Learned Peripheral Reconstruction

Replace the hand-tuned edge-aware upsample in Pass 5 with a DLSS/FSR-class or small learned foveal reconstruction from coarse shadelets plus depth/normal. This is exactly the regime where learned reconstruction wins, and VR vendors already ship the building blocks. Keep the hand-tuned path as the portable fallback.

### 26.8 Gaze/Saccade Prediction

Foveal latency is the dominant artifact source. Borrow Kalman or learned saccade prediction from the eye-tracking HCI literature to widen the guard band only in the predicted saccade direction rather than uniformly. This is cheaper and lower latency than a uniformly large guard band and feeds the foveation policy in the gaze section.

### 26.9 Checkerboard/Quad Rotation In The Periphery

Rotate hardware-quad/checkerboard sample patterns across frames in the periphery, fused with the temporal cache, for effective rate reduction beyond static VRS at near-zero cost.

Priority recommendation: fold in material sort/bin (26.5), reservoir-based shared lighting (26.3/26.4), and the world-space hash-grid temporal store (26.6) first. Each replaces a hand-rolled, artifact-prone RVC piece with a more principled, better-tested mechanism.

## 27. Pipeline Strategy And Frame Graph

RVC should be built as a dedicated render pipeline, but staged as a parallel sibling, not a fork of the current pipeline, and not before the foundations exist.

Rationale:

- RVC violates core assumptions of the current Forward+/DefaultRenderPipeline that cannot be expressed as a toggle: shading is decoupled from rasterization, lighting is head-space rather than view-space, the frame is N views rather than one or two, and resolve is a separate compute stage. Expressing all of this as flags on the existing pipeline produces branchy, untestable code.
- A premature full split is also wrong: the early stages need the existing renderer as a pixel-for-pixel correctness oracle.

Recommended structure:

- Stage 0-1: no new pipeline. Add `RenderFrameViewSet` and quad-view Forward+ inside the current pipeline. This is shared plumbing every future path needs.
- Stage 2 onward: introduce a distinct `RvcRenderPipeline` as a sibling to `DefaultRenderPipeline`, selectable by setting, sharing scene/cull/material-table/light-buffer services but owning its own pass graph. Keep Forward+ as the in-pipeline transparency overlay and fallback, not a separate pipeline.
- Do not fork the default pipeline, and do not build the RVC pipeline before the view-set foundation and visibility-buffer baseline exist.

Frame graph requirement:

- Make the pass graph data-driven via a render/frame graph with explicit resource lifetimes, aliasing, and barriers. RVC has eight passes with non-trivial dependencies and aliasing; a frame graph is effectively mandatory for the Vulkan target and makes the OpenGL prototype's barrier handling tractable.
- Build the frame graph before Stage 2. Retrofitting it later is painful, and the explicit barriers in the scheduling section assume it.

## 28. Validation Plan

Unit and contract tests:

- View-set packing supports 1, 2, 4, and mirror/debug view counts.
- OpenXR view enumeration does not assume two views.
- Visibility ID packing round-trips primitive, meshlet, instance, and material IDs.
- Shadelet key hashing/deduplication is deterministic.
- Material shadelet layout matches generated shader layout.
- Cluster aggregate math conserves expected light energy for simple fixtures.
- Reservoir combination is unbiased on simple many-light fixtures.
- World-space hash-grid lookup is stable under LOD/topology change fixtures.

GPU/debug validation:

- Side-by-side Forward+ vs RVC opaque output.
- Reconstruction error heatmap for position, normal, UV, and material.
- Shadelet density overlay.
- Reuse overlay by source: intra-view, inset/wide, stereo, temporal.
- Per-view timing and pixel count logs.
- Light cluster occupancy and aggregate contribution overlays.
- Specular correction debug mode.
- Material-bin occupancy and shading coherence overlay.
- Shadelet confidence/age overlay.

Runtime validation:

- Stereo OpenXR runtime without quad-view support.
- Quad-view runtime with fixed inset.
- Quad-view runtime with dynamic eye-tracked inset.
- OpenVR fallback remains unchanged.
- Desktop mirror and editor preview remain usable.
- Missed deadline counters do not regress in fallback modes.

Performance targets:

- Stage 1 establishes baseline four-view Forward+ cost.
- Stage 3 should reduce opaque material evaluations in mid/periphery.
- Stage 5 should show material/stable-light reuse across eyes/layers.
- Stage 6 should cap many-light peripheral cost.

## 29. Risks And Mitigations

| Risk | Mitigation |
| --- | --- |
| Sharp specular looks wrong when shared | Cache only broad specular; evaluate sharp specular per view in fovea/guard band, gated by roughness bucket. |
| Compute material evaluation lacks derivatives | Default to analytic barycentric-partial gradients; ray cones for reflections/periphery; shader fallback last. |
| Disocclusion breaks stereo reuse | Keep visibility per view and make sharing opportunistic; shade disocclusion holes per view this frame. |
| Inset FOV/gaze changes cause churn | Smooth gaze, use guard bands, predict saccades, and keep wide view valid under inset. |
| Tiny triangles defeat shadelet reuse | Detect micro-primitive density and fall back to per-pixel or meshlet-level policy. |
| Deformation invalidates cache | Include skinning/blendshape versioning and instance deformation IDs in cache keys. |
| Alpha test becomes expensive in visibility pass | Allow only deterministic cheap alpha test; fallback otherwise. |
| Transparency quality diverges | Keep transparent Forward+ path, foveated but per view. |
| OpenGL prototype constrains architecture | Treat Vulkan as the full target; use OpenGL only for safe slices. |
| Debugging becomes opaque | Build visualizers and counters as part of each stage. |
| Memory spikes from shadelet maps | Budget per-view maps explicitly; support compact formats and fallback caps. |
| Runtime support is inconsistent | Quad-view is opt-in; stereo Forward+ remains the fallback. |
| Divergent material shading on visibility buffer | Sort/bin shadelets by material before shading for coherent dispatches. |
| Primitive/barycentric identity breaks under LOD change | Back the temporal store with a world-space spatial hash. |
| Hand-rolled temporal accumulation is biased/noisy | Use spatiotemporal reservoirs with a principled MIS/invalidation story. |
| Flag-based bolt-on becomes untestable | Build RVC as a sibling pipeline behind a frame graph, not a toggle on the default pipeline. |

## 30. Open Questions

- Should `RenderFrameViewSet` live in runtime rendering abstractions or in the OpenXR layer with a renderer-facing adapter?
- Should shadelet caches be keyed by primitive barycentrics, UVs, world-space position, or a hybrid?
- What is the first material class allowed into RVC: unlit, opaque PBR, or generated material-table shaders only?
- How much of the existing Forward+ tile infrastructure can be reused for head-space clusters?
- Should peripheral aggregates use low-order SH, dominant-direction lobes, or both?
- What is the right debug image format for `PixelToShadelet` on OpenGL?
- Can late foveal inset updates be scheduled without violating OpenXR frame timing on SteamVR and Oculus runtimes?
- How should TAA history be shared, if at all, between wide and inset views?
- What parts of RVC should be exposed as editor quality settings versus backend-only renderer policy?
- Should the shared lighting cache adopt reservoirs from Stage 4, or only at Stage 7?
- Is a world-space hash-grid the right temporal store, or should it coexist with primitive/barycentric keys as a hybrid?
- Which frame-graph implementation (engine-owned vs existing abstraction) should back the RVC pipeline?
- Where should learned peripheral reconstruction sit relative to the portable hand-tuned upsample?

## 31. Recommended First Branch

Create a focused branch for Stage 0 and Stage 1:

```text
rendering-rvc-quad-view-foundation
```

Initial deliverables:

- Runtime view-count agnostic OpenXR plumbing.
- Quad-view capability detection and setting-gated session selection.
- Four-view Forward+ baseline.
- Per-view timing/pixel-count diagnostics.
- Rendering architecture docs updated with the final stage plan.

Only after that baseline is correct should the visibility cache work begin.

## 32. Final Architecture Target


RVC should become the high-end opaque VR renderer:

```text
Opaque:
  quad-view multiview visibility buffer
  foveated shadelet generation
  material shadelet cache
  shared head-space clustered/aggregate lighting
  per-view specular resolve

Transparent:
  foveated clustered Forward+ overlay

Post:
  foveated TAA/reprojection
  inset/wide composition support
  mirror/debug visualization
```

Forward+ remains the fallback and the transparent companion. Deferred remains useful for non-VR experiments, but it should not be the advanced quad-view path.

The design's core bet is that VR rendering cost should be organized around retinal need and surface reuse, not around the historical assumption that every camera owns a full independent shaded frame.
