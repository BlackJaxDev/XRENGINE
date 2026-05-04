# Transparency and OIT Implementation Plan

Last Updated: 2026-03-11
Status: design
Scope: renderer-level transparency architecture for XRENGINE covering material classification, GPU-driven infrastructure, and six transparency techniques.

Related docs:

- [GPU-Based Rendering TODO](../todo/gpu-rendering.md)
- [Transparency Implementation TODO](../todo/transparency-and-oit-todo.md)

---

## 1. Executive Summary

XRENGINE currently treats all non-opaque content as a single "transparent" bucket with object-sorted alpha blending. This produces correct results only for simple, non-overlapping transparent surfaces. It breaks down for intersecting geometry, misclassifies masked cutout content as blended, and provides no path toward order-independent transparency.

This plan defines:

1. A **material transparency classification** that separates opaque, masked, blended, and advanced-OIT content.
2. A **GPU-driven transparency foundation** that keeps all per-frame sorting and classification on the GPU.
3. **Six transparency techniques** with clear roles, from cheap masked-edge improvement to exact per-pixel compositing.
4. A **phased delivery plan** that ships incremental value starting with the cheapest fixes.

### Recommended Product Direction

| Priority | Commitment |
|----------|------------|
| **Must** | Separate masked and blended materials into distinct render paths |
| **Must** | Require all transparency modes to work under GPU-driven rendering without CPU sorting |
| **Should** | Ship weighted blended OIT as the primary advanced transparency mode |
| **Should** | Ship alpha-to-coverage as the primary masked-edge quality mode |
| **Could** | Offer per-pixel linked lists or depth peeling as high-end exact modes |
| **Won't (yet)** | Ship stochastic transparency or per-triangle sorting until prerequisites are met |

---

## 2. Current State

### 2.1 Relevant Code

| Area | Files |
|------|-------|
| Render pipeline | `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs` |
| Draw commands | `XRENGINE/Rendering/Commands/RenderCommand3D.cs`, `RenderCommandCollection.cs` |
| GPU scene | `XRENGINE/Rendering/Commands/GPUScene.cs`, `GPURenderPassCollection*.cs` |
| Hybrid dispatch | `XRENGINE/Rendering/HybridRenderingManager.cs` |
| Materials | `XRENGINE/Rendering/API/Rendering/Objects/Materials/XRMaterial.cs` |
| Blend/depth | `XRENGINE/Models/Materials/Options/BlendMode.cs`, `DepthTest.cs` |
| Import | `XRENGINE/Core/ModelImporter.cs` |

### 2.2 Current Behavior

- `TransparentForward` pass renders after all opaque content.
- Transparent draws are sorted far-to-near at the object level (`RenderCommand3D.RenderDistance`).
- Blending is standard `SrcAlpha / OneMinusSrcAlpha`.
- Depth writes are now correctly disabled for blended materials (fixed earlier this session).
- Masked cutout materials are now routed to opaque-like passes when an explicit opacity map is present (fixed earlier this session).

### 2.3 Remaining Limitations

- Intersecting transparent meshes produce order-dependent artifacts.
- Large transparent submeshes are sorted by a single coarse depth value.
- No alpha-to-coverage path exists for masked materials under MSAA.
- No formal transparency classification enum — mode is inferred from scattered material flags.
- No GPU-driven transparent draw routing; transparency still relies on CPU-side command sorting.
- No intermediate buffers or resolve passes for advanced OIT techniques.

---

## 3. Architecture

### 3.1 Material Transparency Model

All transparency work builds on an explicit per-material classification.

```csharp
public enum ETransparencyMode
{
    Opaque,             // fully opaque
    Masked,             // binary cutout via alpha test
    AlphaBlend,         // standard src-alpha blending
    PremultipliedAlpha, // premultiplied-alpha blending
    Additive,           // additive blending (particles, glow)
    WeightedBlendedOit, // approximate OIT accumulation
    PerPixelLinkedList,  // exact per-pixel fragment storage
    DepthPeeling,       // exact multi-pass layer peeling
    Stochastic,         // stochastic sample masking + TAA
    AlphaToCoverage,    // MSAA coverage-based masked AA
    TriangleSorted      // per-triangle GPU index reorder
}
```

**Material properties:**

| Property | Type | Purpose |
|----------|------|---------|
| `TransparencyMode` | `ETransparencyMode` | Primary classification |
| `AlphaCutoff` | `float` | Threshold for `Masked` / `AlphaToCoverage` |
| `PremultipliedAlpha` | `bool` | Whether color is pre-multiplied |
| `TransparentSortPriority` | `int` | Manual sort bias |
| `TransparentReceivesFog` | `bool` | Fog participation |
| `TransparentWritesVelocity` | `bool` | Motion vector output |
| `TransparentRefractionEnabled` | `bool` | Refraction sampling |
| `TransparentTechniqueOverride` | `ETransparencyMode?` | Debug/per-material forcing |

**Import rules:**

- `TextureType.Opacity` → `Masked` unless metadata explicitly indicates soft blending.
- Diffuse-alpha alone does not imply `AlphaBlend` — import options or authoring metadata must opt in.
- Particles and VFX materials must explicitly select `AlphaBlend`, `PremultipliedAlpha`, or `Additive`.

### 3.2 Render-Pass Structure

The single `TransparentForward` pass evolves into a family of transparency stages:

```
Opaque Deferred
    ↓
Opaque Forward
    ↓
Masked Forward  ← new: depth-writing cutout with optional A2C
    ↓
Transparent Accumulation  ← new: OIT accumulate (weighted, linked list, etc.)
    ↓
Transparent Resolve  ← new: fullscreen composite over scene color
    ↓
Transparent Exact  ← new: expensive modes (depth peel, etc.) if enabled
    ↓
On-Top Forward
    ↓
Post-Render / UI
```

Pass identities the render graph should model internally:

- `MaskedForward` — masked cutout + optional alpha-to-coverage
- `TransparentAccumulation` — OIT accumulation (weighted blended, linked list writes)
- `TransparentResolve` — fullscreen resolve/composite
- `TransparentExact` — multi-pass or high-cost modes (depth peeling)

Current deferred approximation path:

- Alpha-aware deferred textured variants currently write effective opacity (`texture alpha * material opacity`) into `AlbedoOpacity.a`; the ordinary opaque deferred PBR variants still write material opacity only so albedo alpha channels do not accidentally drive opacity.
- Deferred-based opaque, masked, and alpha-to-coverage materials keep their deferred fragment variants instead of being normalized back to forward shading.
- A fullscreen `DeferredTransparencyBlur` reconstruction pass runs after opaque scene assembly and before forward transparency / OIT compositing so dithered deferred transparency is softened against the resolved opaque scene.
- Forward transparency remains the preferred path for richer blending, weighted OIT, and exact techniques.

### 3.3 Depth Policy

| Material class | Depth test | Depth write |
|----------------|-----------|-------------|
| Opaque | On | On |
| Masked cutout | On | On |
| Blended / OIT accumulation | On | **Off** |
| OIT resolve | Off (fullscreen) | Off |

### 3.4 Content Routing

Default routing rules that determine which pass a material enters:

| Content class | Default route |
|---------------|---------------|
| UI overlays | Existing UI/transparent path (no OIT) |
| Particles | Weighted blended OIT when enabled, else standard blend |
| Foliage / fabric / fences | Masked → `MaskedForward` with optional A2C |
| Glass / holograms / decals | Weighted blended OIT, upgrade to exact if quality demands |
| Intersecting translucents | Exact OIT when available, else weighted blended |

---

## 4. GPU-Driven Transparency

**Constraint:** Every transparency mode must work under the GPU-driven render path without CPU-side draw sorting at runtime. A transparency mode that requires per-frame CPU sorting diverges from the engine's architecture and is not acceptable for shipping.

### 4.1 Goals

- Visibility, classification, batching, and sorting are produced by GPU compute.
- CPU uploads static scene/material data and dispatches passes — it does not sort transparent draws per frame.
- The same `ETransparencyMode` classification drives both CPU fallback and GPU-driven paths.
- Approximate and exact OIT techniques coexist behind the same scene metadata.

### 4.2 Per-Draw Metadata

The GPU-driven path derives all transparency work from per-draw metadata uploaded once and updated incrementally:

**Static fields (CPU-uploaded):**

- draw id, material id, mesh/submesh id
- `ETransparencyMode`, blend family, alpha cutoff
- bounds center, bounds extents/radius
- sort priority
- flags: refraction, particle, double-sided, writes velocity, receives fog

**Per-view fields (compute-written each frame):**

- view-space depth key (center, nearest, farthest, or mode-specific)
- screen-space bounds or tile coverage
- approximate depth-complexity bin
- visibility bit
- target transparency pass id

### 4.3 GPU Pass Topology

```
1. Visibility + culling compute  ─→  visible draw list
2. Transparency classification   ─→  route draws to domains
3. Key generation / reset        ─→  mode-specific sort keys or buffer clears
4. Accumulation / sorted build   ─→  mode-specific raster or compute
5. Draw dispatch                 ─→  indirect multi-draw, meshlets, or fullscreen
6. Resolve / composite           ─→  final transparent contribution to scene color
```

Transparency is a GPU work graph rooted in scene visibility — not a late CPU sort step.

### 4.4 Sort Domains

Rather than one monolithic transparent bucket, the engine defines explicit sort domains:

| Domain | Description | Sort requirement |
|--------|-------------|-----------------|
| `Masked` | Cutout / A2C content | None (order-independent with depth writes) |
| `TransparentApproximate` | Weighted blended OIT | None for correctness; optional for batching |
| `TransparentExact` | Linked lists, depth peeling | Per-pixel resolve handles ordering |
| `TransparentExperimental` | Stochastic, triangle-sorted | Mode-specific |

### 4.5 GPU Sort Strategy

The engine should revive and formalize the dormant GPU sorting code (`GPURenderPassCollection.Sorting.cs`) into reusable primitives.

**Sort layers:**

1. Draw-level radix or bitonic sort for transparent draw keys
2. Optional tile/cluster sort for locality-sensitive modes
3. Optional meshlet/triangle sort for geometry-reordered modes

**Sort key packing:**

```
[ domain (3 bits) | priority (5 bits) | material bucket (8 bits) | depth (16 bits) ]
```

Not every technique uses all layers. Weighted blended OIT and alpha-to-coverage skip correctness-critical sorting entirely. The GPU-driven renderer owns the sort primitives so modes that need them never fall back to CPU.

### 4.6 GPU Material Batching

- **Accumulation-based OIT (order-independent):** Preserve material batching aggressively.
- **Sorted dispatch (order-dependent):** GPU sort within or across batches using explicit sort keys.
- **Fragment-structure-based (linked lists):** Batch raster submission for throughput; correctness comes from per-pixel resolve.

### 4.7 CPU Role

The CPU **may:**

- Upload static mesh, material, and instance metadata
- Allocate/resize buffers when resolution or scene limits change
- Choose the active transparency feature set per renderer/backend/profile
- Dispatch render-graph passes

The CPU **must not:**

- Sort transparent objects every frame for the main runtime path
- Rebuild triangle order every frame outside of explicit CPU fallback or offline tool mode
- Perform per-frame exact transparency compositing outside debug mode

### 4.8 Required GPU Infrastructure

Shared resources added once and reused across all techniques:

- Transparent draw metadata buffer
- Transparent visible-list buffer
- Sort-key and permutation buffers
- Mode-specific counters and overflow diagnostics buffers
- Indirect command compaction and permutation application pass
- Tile coverage / screen-space binning buffers
- Transparency diagnostics block in the GPU dispatch logger

### 4.9 Fallback Policy

CPU transparency paths may exist for debugging, validation, or unsupported backends. The design target for shipping is GPU-driven first.

---

## 5. Transparency Techniques

### 5.1 Weighted Blended OIT

**Role:** Primary advanced transparency mode.

**Algorithm:** Transparent fragments write weighted color and alpha to accumulation buffers; a fullscreen resolve pass reconstructs approximate transparent contribution.

$$C_{acc} += C_{src} \cdot \alpha \cdot w$$
$$A_{acc} += \alpha \cdot w$$
$$R *= (1 - \alpha)$$

**Engine changes:**

- `TransparentAccumTexture` (`RGBA16F`) and `TransparentRevealageTexture` (`R16F` or `R8`)
- Transparent accumulation pass with additive blend on accumulation target, multiplicative on revealage
- Fullscreen resolve pass compositing over scene color
- OIT fragment shader variant outputting weighted color + revealage
- Depth test on, depth write off during accumulation

**GPU-driven fit:** Best of all six. No GPU sort required for correctness. Culling compute produces a visible transparent list; indirect batches remain material-grouped; raster writes directly to accumulation targets; fullscreen resolve composites afterward.

**Strengths:** Much better than object sorting; moderate complexity; predictable memory; works well for particles, glass, and common translucents.

**Weaknesses:** Still approximate; stacked thick glass can be wrong; weight function is content-sensitive; revealage precision matters for dense scenes.

**Validation:** Intersecting glass panes, layered particles, translucent foliage over bright backgrounds, VR stereo consistency.

---

### 5.2 Per-Pixel Linked Lists

**Role:** Optional high-end exact mode.

**Algorithm:** During rasterization, atomically allocate a node from a global fragment pool, store (depth, color, alpha, next-pointer), and prepend to a per-pixel head pointer. During resolve, walk each pixel's list, sort by depth, and composite.

**Engine changes:**

- Per-pixel head-pointer image or buffer
- Fragment node storage buffer with atomic counter allocator
- Per-frame clear/reset
- Compute or fullscreen resolve pass for per-pixel sort + composite

**First-pass node payload:** depth, premultiplied color, alpha, next index. Optional later: material id, refraction, emission, normal/thickness.

**GPU-driven fit:** Natural. Correctness comes from fragment storage and per-pixel resolve, not CPU draw order. Indirect raster submits fragments into buffers; compute resolve sorts per-pixel on GPU.

**API constraints:** Requires storage buffers, atomics, and image/buffer writes in fragment path. Must verify OpenGL 4.6 and Vulkan backend parity before committing.

**Overflow policy:** Fixed max fragment count; detect overflow via debug heatmap; fall back to weighted blended resolve on overflow.

**Strengths:** Handles intersecting transparent geometry correctly; no object-sort dependency.

**Weaknesses:** High complexity; memory pool sizing is content-dependent; heavy atomics and bandwidth; harder to debug.

**Validation:** Worst-case particle overdraw, interpenetrating meshes, overflow stress test, backend parity.

---

### 5.3 Depth Peeling

**Role:** Correctness/reference mode (debug and offline capture).

**Algorithm:** Renders transparent layers in multiple passes. Each pass captures the next visible layer behind the previous one using depth comparison against the prior peel target.

**Variants:** Single peeling, dual peeling, bounded peeling with fallback to weighted blending after max layers.

**Engine changes:**

- Peel depth targets and layer color targets
- Repeated transparent draw passes driven by previous peel depth
- Layer compositing pass
- Configurable max peel count cap

**GPU-driven fit:** Fully compatible. Compute culling builds one visible list reused across peel iterations. Each peel resubmits the same indirect draws with a depth-state test against the previous peel. No CPU sort required; GPU cost grows with peel count.

**Recommended position:** Do not make this the default runtime mode. Implement only if a correctness/debug path is worth the maintenance cost.

**Strengths:** High per-layer correctness; easier to reason about than linked lists; useful as validation reference.

**Weaknesses:** Pass count scales poorly; expensive for many-layer scenes; poor choice for VR.

---

### 5.4 Stochastic Transparency

**Role:** Experimental future mode — evaluate only after TAA/temporal stability is mature.

**Algorithm:** Convert fractional alpha to randomized MSAA coverage using blue noise or temporal hash. Rely on temporal accumulation to reconstruct smooth apparent transparency over multiple frames.

**Engine changes:**

- Stochastic threshold generation (blue noise / hash)
- Integration with TAA/TSR history and motion vectors
- Optional MSAA integration for spatial sample quality

**Prerequisites:** Trustworthy motion vectors for transparent content; aggressive ghosting rejection in the temporal filter; stable camera jitter and reconstruction.

**GPU-driven fit:** Compatible. Compute culling builds visible list; raster uses noise-based acceptance in the transparent shader; temporal resolve is GPU-side. The hard part is temporal stability, not sorting.

**Strengths:** No explicit sorting; very high quality with good temporal reconstruction; shares ideas with alpha-to-coverage.

**Weaknesses:** Noisy without temporal accumulation; ghosting risk; hard to debug because artifacts span transparency + TAA systems.

---

### 5.5 Alpha-To-Coverage

**Role:** Primary masked-edge quality mode. Complements (does not compete with) weighted blended OIT.

**Algorithm:** Map alpha values to MSAA sample coverage masks instead of blending. Produces smooth masked edges when MSAA is active while preserving depth writes.

**Engine changes:**

- Explicit `AlphaToCoverage` material mode
- OpenGL and Vulkan backend state exposure
- Shader output preserving alpha for coverage conversion
- Fallback to hard `AlphaCutoff` when MSAA is disabled; optional temporal dithered alpha test as secondary fallback

**GPU-driven fit:** Cleanest of all six. Compute culling routes masked draws to the A2C domain; indirect batches render with MSAA coverage enabled; depth test and write remain active. Zero per-frame sort work.

**Strengths:** Excellent for foliage, fences, lace; preserves depth correctness; simple mental model; cheap.

**Weaknesses:** Not a general translucent solution; depends on MSAA sample count; can shimmer on very thin geometry.

**Validation:** Sponza foliage edges, chain-link fences, camera-motion shimmer under varying MSAA levels.

---

### 5.6 Per-Triangle Sorting

**Role:** Optional specialized fallback — not a general-purpose strategy.

**Algorithm:** Reorder triangles within a mesh back-to-front relative to the camera each frame. Improves self-overlap within one transparent mesh but does not solve inter-object ordering.

**Variants:**

- GPU compute sort of triangle indices per frame for flagged meshes
- Compute binning into coarse depth slices with reordered index stream
- Meshlet-level sort using meshlet-local keys

**Engine changes:**

- Mesh/submesh flag for triangle-sorted transparency
- Alternate sorted index buffer generation on GPU
- Camera-relative sort key per triangle (centroid depth or plane metric)
- Handling for skinned/deforming meshes (per-frame GPU key regen)

**GPU-driven fit:** Least natural. Must be treated as a geometry-preparation compute stage. All reordering happens in GPU buffers — never CPU index buffer rebuilds. Integration with indirect multi-draw is harder because the unit of ordering is triangle/meshlet rather than draw.

**Limitations:** Fails for intersecting triangles; fails for intersecting meshes; expensive for high-tri-count or animated geometry.

**Validation:** Simple shell/ribbon meshes, self-overlapping transparent single objects.

---

## 6. Cross-Cutting Systems

### 6.1 Render-Graph Integration

The pipeline should model transparency as graph stages, not a single pass index.

- Allocate intermediate transparency resources conditionally per active mode.
- Clear and recycle predictably.
- Express dependencies between accumulation and resolve.
- Expose debug and capture visibility for all transparency resources.

### 6.2 Shader Variant Management

Transparency introduces new variant axes. The engine should manage them systematically to prevent ad-hoc permutation growth.

**Required axes:**

- Opaque / Masked / Transparent
- Standard blend / Weighted OIT / Exact OIT
- Normal-mapped / not
- Specular / not
- Refraction / not

### 6.3 Backend Capability Declaration

Each technique declares its API requirements explicitly:

| Technique | Requirements |
|-----------|-------------|
| Weighted blended OIT | Widely supported (MRT, float textures) |
| Alpha-to-coverage | MSAA path with backend A2C support |
| Per-pixel linked lists | Storage buffers, atomics, image/buffer writes in fragment path |
| Depth peeling | Multiple render passes with depth comparison |
| Stochastic transparency | Strong temporal accumulation, sample controls |
| Per-triangle sorting | Compute with index buffer write access |

### 6.4 Asset Import and Authoring

Importers should stop inferring transparency solely from alpha channel presence.

- Import option `DiffuseAlphaMode`: `Opaque`, `Masked`, `Blended`, `Auto`
- Import option `OpacityMapMode`: `Masked`, `Blended`, `Auto`
- Material inspector override for inferred transparency mode
- Diagnostics listing materials whose imported mode conflicts with texture usage

### 6.5 Debug Views

Every transparency mode ships with debug visualization:

- Transparency mode overlay (color-coded by `ETransparencyMode`)
- Masked vs. blended classification overlay
- Transparent sort-order visualization
- Accumulation and revealage buffer views
- Per-pixel fragment count heatmap (exact OIT)
- Alpha-to-coverage mask preview (MSAA active)
- Triangle sort instability diagnostics

### 6.6 Profiling and Telemetry

| Metric | Purpose |
|--------|---------|
| Transparent draw calls by mode | Budget tracking |
| Screen-space transparent overdraw | Overdraw hotspots |
| Weighted OIT resolve cost | Resolve performance |
| Linked-list fragment count + overflow count | Memory pressure |
| Depth peel pass count per frame | Peel cost |
| Masked vs. blended material counts | Classification health |

---

## 7. Decision Matrix

| Technique | Quality | Runtime Cost | Memory | Complexity | Best For | Role |
|-----------|---------|:-------------|:-------|:-----------|----------|------|
| Weighted blended OIT | Medium-high | Medium | Medium | Medium | Glass, particles, general translucents | **Primary advanced mode** |
| Per-pixel linked lists | High | High | High | High | Exact transparent ordering | Optional high-end exact |
| Depth peeling | High (per layer) | High | Medium-high | Medium-high | Debug, capture, limited-layer scenes | Correctness/reference |
| Stochastic transparency | Medium-high w/ TAA | Medium | Low-medium | High | Engines with strong temporal recon | Experimental future |
| Alpha-to-coverage | High (masked) | Low | Low | Low-medium | Foliage, fences, lace, cutouts | **Primary masked-edge mode** |
| Per-triangle sorting | Low-medium | Medium-high | Low-medium | Medium | Isolated self-overlapping meshes | Specialized fallback |

---

## 8. Risks

| Risk | Mitigation |
|------|------------|
| Import heuristics silently choose wrong transparency mode | Explicit `ETransparencyMode`; import options; diagnostics overlay |
| Exact OIT modes become backend-specific, fragmenting the renderer | Explicit capability declarations; shared fallback to weighted blended |
| Stochastic transparency looks worse than standard blending | Gate behind temporal stability prerequisites; do not ship early |
| Weighted blended OIT becomes permanent default without content validation | Phase 4 evaluation gate comparing quality against exact mode |
| Alpha-to-coverage oversold as universal transparency fix | Document as masked-only; pair with weighted blended for translucents |
| GPU-driven transparency infrastructure is over-engineered before first technique ships | Phase 0.5 builds only the minimal shared scaffold; techniques pull additional infra as needed |

---

## 9. Open Questions

- Should `TransparencyMode` be serialized on `XRMaterial`, inferred from shader family, or both?
- Renderer-global transparency technique setting, per-viewport override, or both?
- Can some masked materials safely use the deferred-compatible path?
- Should particles default to weighted blended OIT once implemented?
- What minimum backend feature set is required to expose exact OIT in shipping builds?
- Should VR use a stricter subset of techniques initially due to stereo cost?

---

## 10. Near-Term Commitment

1. Formalize material transparency classification (`ETransparencyMode`).
2. Finish masked-vs-blended separation in import and authoring.
3. Implement alpha-to-coverage for masked materials.
4. Implement weighted blended OIT as the first general-purpose transparency upgrade.
5. Defer exact OIT until real content proves weighted blended OIT insufficient.

This path fixes current Sponza-class content problems, materially improves translucent rendering, and avoids overcommitting to expensive techniques before the renderer has a clean transparency architecture.