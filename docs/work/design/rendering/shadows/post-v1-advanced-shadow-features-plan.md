# Post-v1 Advanced Shadow Features Plan

> Status: **future design**
> Scope: advanced shadow features deliberately deferred from the v1 VSM/EVSM and dynamic atlas work.

## Goal

This document captures the shadow features that are valuable but too risky or broad for the v1 shadow filtering and atlas rollout:

- Variance-based PCSS / VSSM.
- Tile-aware MSAA resolve for atlas resources.
- Translucent, colored, stochastic, deep-shadow, or Fourier opacity shadows.
- Runtime per-fragment dynamic branching between encodings in hot receiver loops.

These features should build on the v1 contracts from the VSM/EVSM filtering plan and the dynamic shadow atlas plan. They should not block the default depth, VSM, EVSM, or atlas migration path.

## Non-Goals

- Do not change the v1 default shadow behavior.
- Do not require all materials to support every advanced shadow mode.
- Do not add per-fragment feature selection to the default hot path unless profiling proves it wins.
- Do not make translucent shadowing a replacement for opaque depth, VSM, or EVSM shadows.
- Do not ship these features before the core atlas, metadata, and receiver dispatcher are stable.

## Shared Preconditions

Before any feature in this document starts, the engine should already have:

- `EShadowMapEncoding` and centralized shadow resource selection.
- Atlas records with stable tile rects, gutters, clear sentinels, depth parameters, and filter parameters.
- Receiver-side dispatchers for depth, VSM, EVSM2, and EVSM4.
- Tile-aware sampling guards that clamp taps to inner rects.
- A debug view for atlas pages, tile ownership, channels, variance, and skip/fallback state.
- Shader validation coverage for shared shadow snippets and deferred receiver shaders.

The last item matters because several deferred shaders still duplicate local shadow logic rather than going through one shared wrapper. Shared snippet changes should always be validated against deferred shader consumers, not just editor diagnostics.

## Feature 1: Variance-Based PCSS / VSSM

### Motivation

Depth PCSS is robust but expensive because it performs a blocker search and then a variable-radius filter over binary comparisons. Variance-based soft shadows try to estimate blocker depth and penumbra size from filtered depth moments, reducing tap count for wide penumbrae.

The desired outcome is contact-hardening soft shadows for moment maps without falling back to depth-mode PCSS.

### Proposed Model

Add a moment-only depth filter mode after the v1 moment encodings are stable:

```csharp
public enum EShadowMomentFilterMode
{
    Chebyshev = 0,
    Evsm = 1,
    VariancePcss = 2,
}
```

The first implementation should target spot lights and atlas spot tiles only. Directional cascades and point faces should wait until the blocker estimate is stable for a single 2D projection.

Inputs:

- light radius or angular size,
- receiver depth in the same linear normalized space as the moment map,
- filtered first and second moments,
- min variance,
- light bleed reduction,
- max penumbra,
- atlas tile texel size.

### Open Questions

- Is a two-moment map enough for stable blocker estimation, or does production quality require extra moments or summed-area data?
- Should VSSM use summed-area tables for large filters, or rely on mip/blurred moment pyramids?
- How should VSSM interact with contact shadows: multiply after VSSM like other modes, or reduce contact-shadow strength as penumbra grows?

### Risks

- Moment light bleeding can be amplified by contact-hardening estimates.
- A blocker estimate derived from already-filtered moments can produce unstable penumbra widths near discontinuities.
- Cascade transitions can look wrong if penumbra size changes abruptly across cascades.

### Validation

- Compare against `Depth + ContactHardeningPcss` in a spot-light scene.
- Test thin blockers and overlapping blockers to expose light bleeding.
- Test moving receiver and moving blocker cases for penumbra stability.
- Profile receiver cost against depth PCSS at equivalent visual radius.

## Feature 2: Tile-Aware MSAA Resolve For Atlas Resources

### Motivation

Moment maps benefit from MSAA shadow rasterization because sub-pixel caster coverage can be resolved into smoother moments. A naive whole-page resolve is unsafe in an atlas: resolve kernels, derivative assumptions, or post-process passes can cross tile boundaries and leak neighboring shadows.

The desired outcome is MSAA raster quality for atlas tiles without breaking gutters, tile isolation, or per-encoding clear sentinels.

### Proposed Resource Shape

Each scheduled tile render can use a transient MSAA raster target:

```text
Tile render
  MSAA depth attachment
  optional MSAA color moment attachment
  tile-scoped resolve shader
  single-sample atlas tile output
```

The resolve pass must receive:

- atlas page target,
- inner tile rect,
- gutter rect,
- active encoding,
- clear sentinel,
- near/far depth parameters,
- EVSM exponent clamps,
- sample count.

The resolve shader writes only inside the tile rect. Gutters are either resolved from duplicated edge values or explicitly filled with the encoding clear/edge policy after resolve.

### Implementation Notes

- Start with non-atlas MSAA moment resources before atlas MSAA.
- For atlas resources, keep MSAA attachments transient and page-size independent where possible.
- Avoid storing one MSAA texture per atlas page unless profiling proves the memory cost is acceptable.
- Do not generate mips from MSAA data directly. Resolve first, then run tile-aware blur/mip passes.

### Risks

- MSAA resolves can become more expensive than the receiver-side savings.
- Per-tile transient allocation can become a render-thread allocation hazard if not pooled.
- Mixed sample counts per tile can complicate scheduling and resource reuse.

### Validation

- Atlas gutter leak scene with high-contrast neighboring tiles.
- Thin geometry and alpha-tested caster scenes.
- Forced mixed-LOD atlas scene, because small tiles stress resolve bounds.
- GPU memory and render-time capture with 1x, 2x, 4x, and 8x samples.

## Feature 3: Translucent, Colored, Stochastic, Deep-Shadow, And Fourier Opacity Shadows

### Motivation

Opaque shadow maps cannot represent colored glass, hair, foliage density, smoke, or volumetric attenuation. These materials need opacity or transmittance rather than a single nearest-depth blocker.

The desired outcome is a separate advanced shadow path for materials that need partial transmittance, without slowing normal opaque shadows.

### Candidate Techniques

| Technique | Best For | Storage | Notes |
|---|---|---|---|
| Colored transmittance map | simple stained glass | `RGBA8` or `RGBA16f` | One layer of color attenuation, cheap but not order-independent. |
| Stochastic alpha shadows | foliage, hair cards | depth or moment map plus noise | Good for alpha-tested content; needs temporal stability. |
| Deep opacity map | hair, smoke shells | layered opacity/depth | More accurate but memory-heavy. |
| Fourier opacity map | hair/fur-like density | Fourier coefficients | Compact for smooth opacity distributions, harder to debug. |
| Volumetric shadow grid | fog/smoke volumes | 3D froxel/volume | Better handled by volumetric lighting systems than regular shadow maps. |

### Proposed V1-Compatible Integration

Do not fold translucent shadows into `EShadowMapEncoding`. Instead, add a secondary optional shadow contribution:

```csharp
public enum EShadowTransmittanceMode
{
    None = 0,
    ColoredSingleLayer = 1,
    StochasticAlpha = 2,
    DeepOpacity = 3,
    FourierOpacity = 4,
}
```

The base opaque shadow visibility remains depth/VSM/EVSM. Transmittance multiplies or tints the final light contribution after opaque visibility.

Receiver order:

1. Sample opaque shadow visibility.
2. Sample contact shadow multiplier.
3. Sample translucent transmittance if the light has a transmittance record.
4. Apply volumetric/fog integration separately where relevant.

### Material And Caster Policy

Shadow caster material classification should produce explicit draw buckets:

- opaque depth/moment casters,
- alpha-tested opaque casters,
- stochastic alpha casters,
- colored translucent casters,
- volumetric/deep opacity casters.

Do not let a translucent material silently enter the opaque shadow pass unless it is explicitly configured as alpha-tested.

### Risks

- Colored and translucent shadows can quickly become a separate renderer inside the renderer.
- Deep opacity and Fourier opacity require specialized debugging tools.
- Stochastic shadows can shimmer badly in VR without stable blue-noise or temporal accumulation.
- Sorting and material bucketing can add CPU overhead to shadow submission.

### Validation

- Stained-glass color transmittance scene.
- Alpha foliage scene with camera motion.
- Hair-card or shell-density scene if assets exist.
- VR stereo stability check for stochastic patterns.
- Debug visualization for transmittance, opacity layers, and final tinted visibility.

## Feature 4: Runtime Per-Fragment Dynamic Encoding Branching

### Motivation

The v1 plan prefers shader variants or static feature selection so hot receiver loops compile down to one encoding path. A future renderer may want one receiver shader to handle mixed depth, VSM, EVSM2, EVSM4, and transmittance records through per-fragment metadata.

The desired outcome would be fewer material permutations and more flexible mixed-light rendering.

### Proposed Direction

Treat this as an optimization experiment, not an architectural default. Add a single dynamic-dispatch receiver path behind a renderer setting and compare it against static variants.

Candidate GLSL shape:

```glsl
float XRENGINE_SampleShadowDynamic(ShadowAtlasTile tile, vec3 shadowCoord)
{
    switch (tile.packed0.y) // encoding
    {
        case XRE_SHADOW_ENCODING_DEPTH:
            return XRENGINE_SampleDepthShadow(tile, shadowCoord);
        case XRE_SHADOW_ENCODING_VSM2:
            return XRENGINE_SampleVsmShadow(tile, shadowCoord);
        case XRE_SHADOW_ENCODING_EVSM2:
            return XRENGINE_SampleEvsm2Shadow(tile, shadowCoord);
        case XRE_SHADOW_ENCODING_EVSM4:
            return XRENGINE_SampleEvsm4Shadow(tile, shadowCoord);
        default:
            return 1.0;
    }
}
```

This path is acceptable only if the branch is coherent enough in real lighting workloads. Deferred per-light passes are likely coherent. Forward+ clustered shading with many mixed local lights may not be.

### Decision Criteria

Dynamic branching can graduate from experimental only if it:

- reduces shader variant count meaningfully,
- does not regress common forward and deferred scenes,
- keeps register pressure acceptable,
- keeps instruction cache behavior stable on target GPUs,
- remains readable and testable.

### Risks

- Divergent branches can make every fragment pay for multiple encodings.
- Register pressure can increase even when the branch is coherent.
- Debugging shader regressions becomes harder when one shader contains every path.
- Vulkan/DX12 backends may optimize switch-heavy paths differently from OpenGL.

### Validation

- Shader permutation count before/after.
- Forward+ scene with many mixed local shadow encodings.
- Deferred scene with one encoding per light pass.
- GPU timing on NVIDIA, AMD, and Intel where available.
- Shader disassembly or compiler statistics when backend tooling exposes it.

## Suggested Phase Order

### Phase A: Research And Prototypes

- Prototype VSSM for non-atlas spot lights.
- Prototype non-atlas MSAA moment resolve.
- Prototype dynamic encoding dispatch in a standalone receiver shader.
- Write visual/debug notes before committing to production paths.

### Phase B: Atlas-Safe Resolve Infrastructure

- Add pooled transient MSAA resources.
- Add tile-scoped resolve passes.
- Add resolve/gutter validation scenes.
- Gate atlas MSAA behind a renderer setting.

### Phase C: Moment Contact-Hardening

- Promote VSSM from prototype to spot atlas tiles.
- Add editor controls only after stable defaults exist.
- Extend to directional cascades only after cascade transition behavior is proven.

### Phase D: Transmittance Shadows

- Start with colored single-layer transmittance for spot lights.
- Add material classification and debug views.
- Defer deep/Fourier opacity until a concrete asset need exists.

### Phase E: Dynamic Dispatch Evaluation

- Compare static variants against dynamic branching across target scenes.
- Keep static variants as the fallback even if dynamic dispatch ships.

## Test Plan

Unit and source tests:

- VSSM helper math and clamp behavior.
- Tile-scoped resolve bounds and gutter writes.
- Transmittance mode serialization and light/material defaults.
- Shader source checks for dynamic dispatch bindings.
- Shader compile tests for OpenGL/Vulkan shader paths where supported.

Visual tests:

- Thin blockers with wide penumbrae.
- High-contrast neighboring atlas tiles.
- Alpha-tested foliage, stained glass, and smoke/hair proxy scenes.
- Mixed-encoding forward and deferred lighting scenes.
- VR stereo stability for stochastic and cascade-heavy cases.

Performance tests:

- Shadow tile render cost with MSAA sample counts.
- Resolve pass cost by tile size and encoding.
- Receiver cost for VSSM versus depth PCSS.
- Dynamic dispatch versus static variants.
- Memory pressure from transmittance and deep opacity resources.

## Relationship To v1 Work

The v1 shadow work should leave extension points for these features but should not implement them by accident. In practice that means:

- Keep `ShadowMapEncoding` focused on opaque map visibility.
- Keep transmittance as a separate optional record.
- Keep tile metadata extensible with filter and reserved fields.
- Keep shader dispatchers centralized so dynamic branching can be tested later.
- Keep atlas blur, mip, gutter, and clear logic tile-aware from the start.
- Keep static shader variants as the reliable default even if dynamic dispatch becomes available.

The useful rule of thumb: v1 earns trust by making depth, VSM, EVSM, and atlas shadows boring and predictable. These advanced features can come after that foundation is solid.