# VSM And EVSM Shadow Filtering Plan

> Status: **active design**

## Goal

Add variance shadow map (VSM) and exponential variance shadow map (EVSM) options for directional, spot, and point lights, while preserving the current depth-compare options:

- hard shadows
- fixed-radius Poisson filtering
- Vogel-disk filtering
- contact-hardening PCSS
- screen-space/contact shadow multiplication
- directional cascades

The desired v1 shape is a clean shadow architecture where "what a shadow map stores" is separate from "how a receiver filters it."

## Motivation

The current shadow system is depth-comparison first. It stores or samples one normalized depth value and then filters by taking many manual taps around the receiver.

That approach is robust and familiar, but it has costs:

- wide soft shadows require many texture taps
- PCSS needs a blocker-search pass plus a filter pass
- large local lights can become expensive in forward and deferred paths
- directional cascades need careful bias tuning because each sample is a binary comparison

VSM and EVSM provide a different quality/performance tradeoff. They store depth moments so the receiver can estimate visibility using a small number of filtered texture samples. They also work naturally with hardware linear filtering, mipmaps, and separable blur. The tradeoff is possible light bleeding, especially for plain VSM.

## Current State Summary

Relevant current code paths:

- `XRENGINE/Scene/Components/Lights/Types/LightComponent.cs`
  - owns shared shadow settings, `SoftShadowMode`, bias, filter radius, PCSS, contact shadows, and debug mode.
- `XRENGINE/Scene/Components/Lights/Types/DirectionalLightComponent.cs`
  - creates the single directional shadow map.
- `XRENGINE/Scene/Components/Lights/Types/DirectionalLightComponent.CascadeShadows.cs`
  - creates and renders cascaded directional shadow array resources.
- `XRENGINE/Scene/Components/Lights/Types/SpotLightComponent.cs`
  - creates a depth attachment plus an `R16f` color `ShadowMap` texture.
- `XRENGINE/Scene/Components/Lights/Types/PointLightComponent.cs`
  - creates a depth cubemap plus an `R16f` color cubemap `ShadowMap`, and stores normalized radial distance.
- `Build/CommonAssets/Shaders/Snippets/ShadowSampling.glsl`
  - owns shared PCF, Vogel, PCSS/contact-hardening, cubemap, and contact-shadow sampling helpers.
- `Build/CommonAssets/Shaders/Snippets/ForwardLighting.glsl`
  - binds the primary directional shadow, up to four point shadows, and up to four spot shadows.
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightingDir.fs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightingSpot.fs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightingPoint.fs`
  - implement deferred receiver shadow sampling per light type.

Point and spot shadows already sample regular color textures instead of hardware depth-comparison samplers. Directional shadows currently use depth textures for single and cascaded maps. VSM/EVSM should move all sampling paths toward regular color shadow textures while keeping depth attachments for rasterization and early depth rejection.

## Design Principles

1. Keep depth-compare shadows as the default baseline.
2. Treat VSM/EVSM as map encodings, not as aliases for `ESoftShadowMode`.
3. Allow all three light types to choose the same high-level options.
4. Keep contact shadows independent. They remain a short-range multiplier on top of map visibility.
5. Keep point-light moment depth radial, matching the current point shadow color map.
6. Prefer explicit moment parameters over hidden constants.
7. Avoid per-frame allocations in render, collect-visible, and light-binding paths.
8. The receiver depth and the encoded moment depth must live in the **same monotonic scalar space**. If the engine ever flips to reversed-Z, the encoder, the clear value, and the Chebyshev early-out must flip together, not independently.
9. Moment map clears are not zero. They must clear to a "fully unoccluded" sentinel for the chosen encoding (see *Clear Values* below). Atlas gutters use the same sentinel.

## Proposed Public Model

Add a shadow map encoding enum:

```csharp
public enum EShadowMapEncoding
{
    Depth = 0,
    Variance2 = 1,
    ExponentialVariance2 = 2,
    ExponentialVariance4 = 3,
}
```

Recommended meanings:

| Encoding | Stored channels | Format default | Notes |
|---|---:|---|---|
| `Depth` | 1 | `R16f` or depth texture depending on path | Current manual compare path. |
| `Variance2` | 2 | `RG16f` | Stores first and second moments of linear normalized depth. |
| `ExponentialVariance2` | 2 | `RG16f` or `RG32f` | Stores positive exponential warped first and second moments. |
| `ExponentialVariance4` | 4 | `RGBA16f` or `RGBA32f` | Stores positive and negative exponential warped moments. Best EVSM quality. |

Keep `ESoftShadowMode` for depth comparison:

```csharp
public enum ESoftShadowMode
{
    Hard = 0,
    FixedPoisson = 1,
    ContactHardeningPcss = 2,
    VogelDisk = 3,
}
```

Add moment-specific settings to `LightComponent`:

```csharp
public EShadowMapEncoding ShadowMapEncoding { get; set; }
public float ShadowMomentMinVariance { get; set; }
public float ShadowMomentLightBleedReduction { get; set; }
public float ShadowMomentPositiveExponent { get; set; }
public float ShadowMomentNegativeExponent { get; set; }
public int ShadowMomentBlurRadiusTexels { get; set; }
public int ShadowMomentBlurPasses { get; set; }
public float ShadowMomentMipBias { get; set; }
public bool ShadowMomentUseMipmaps { get; set; }
```

Depth mode uses `SoftShadowMode`. Moment modes ignore `SoftShadowMode` for the main shadow-map visibility and use the moment settings instead. Contact shadows still apply in both cases.

Because the product is pre-v1, a cleaner breaking rename is acceptable if desired:

- rename `ESoftShadowMode` to `EShadowDepthFilterMode`
- rename `SoftShadowMode` to `DepthShadowFilterMode`
- expose a grouped editor surface named "Shadow Filtering"

## Moment Map Storage

### Memory and budget impact

Moment maps are larger than the current `R16f` shadow color textures:

| Encoding | Bytes/texel (16-bit) | Bytes/texel (32-bit) | Multiplier vs `R16f` |
|---|---:|---:|---:|
| `Depth` (`R16f`) | 2 | 4 | 1x |
| `Variance2` | 4 | 8 | 2x |
| `ExponentialVariance2` | 4 | 8 | 2x to 4x |
| `ExponentialVariance4` | 8 | 16 | 4x to 8x |

This directly reduces the page count or tile resolution available to the dynamic shadow atlas. The atlas plan groups pages by sampling format already; this plan must coordinate with it on:

- per-encoding atlas page budgets,
- per-light fallback rules when a requested encoding has no remaining capacity (demote to a smaller encoding before demoting to a smaller LOD, or vice versa - the policy must be explicit),
- ownership of reallocation when a light flips encoding at runtime (atlas manager retires the old allocation and reissues a new request keyed to the new encoding).

See `dynamic-shadow-atlas-lod-plan.md` *Atlas Shape* for the page grouping that must absorb these encodings.

### Format and filtering capability

Not every backend supports linear filtering on every floating-point format. Vulkan and DX12 backends in particular may report `RGBA32F` linear-filter support as optional. Before selecting a format:

1. Probe the runtime device for `linear filter` and `render target` capability on the chosen format.
2. Fall back in this order: `RGBA32F` -> `RGBA16F`; `RG32F` -> `RG16F`; if 16-bit float linear filtering is unavailable, the encoding is unusable on that backend and the resource factory must demote the light to `Depth`.
3. Log the demotion once per light, not per frame.

EVSM4 specifically requires a **signed** float format because the negative-exponent channel stores negative values. Unsigned-norm or unsigned-int formats are forbidden for EVSM4 channels.

### Clear values

Moment maps must be cleared to a "fully unoccluded" sentinel so untouched texels and atlas gutters never produce false occlusion under linear filtering or blur:

- `Depth` (`R16f` color path): clear to 1.0 (or 0.0 under reversed-Z).
- `Variance2`: clear to `(1, 1)` so `M2 - M1*M1 = 0` and Chebyshev returns fully lit.
- `ExponentialVariance2`: clear to `(exp(c+ * 1), exp(c+ * 1)^2)`.
- `ExponentialVariance4`: clear to `(exp(c+), exp(c+)^2, -exp(-c- * 1), exp(-c- * 1)^2)` using the active exponents.

The clear value is a function of the active exponents and depth direction. The shadow renderer must emit clears computed at request-submission time, not from a hardcoded constant.

### MSAA shadow rasterization

The largest practical VSM quality win is rendering depth with MSAA and resolving moments to single-sampled. Recommended path:

1. Rasterize depth into an MSAA depth attachment.
2. In a resolve pass, compute moments per source sample, average across samples, and write the resolved moment vector to the sampling texture.
3. Skip MSAA in the atlas path until tile-aware MSAA resolve is validated, since MSAA resolve across tile boundaries reintroduces leak risk.

MSAA support is opt-in, gated by a `ShadowMomentMsaaSamples` setting (default 1).

### Directional lights

Single directional shadows should render:

- a depth attachment for rasterization
- a color moment texture used for sampling

Cascaded directional shadows should render:

- a color `XRTexture2DArray` containing one layer per cascade for sampling
- a matching depth `XRTexture2DArray` or per-layer depth render target for rasterization

The existing `CascadedShadowMapTexture` property should either:

- remain depth-only and gain a new `CascadedShadowMomentTexture`, or
- be replaced by a `ShadowMapResource` wrapper that exposes `SamplingTexture`, `DepthTexture`, `Encoding`, and per-layer FBOs.

The second option is cleaner for v1 because it avoids callers guessing what a property contains.

### Spot lights

Spot lights already render a depth attachment and an `R16f` color `ShadowMap`. Extend that color target to the selected encoding:

- `Depth`: `R16f`
- `Variance2`: `RG16f`
- `ExponentialVariance2`: `RG16f` or `RG32f`
- `ExponentialVariance4`: `RGBA16f` or `RGBA32f`

The moment depth should be the same normalized projected depth that receiver shaders compare today.

### Point lights

Point lights should continue to use radial depth for the sampling texture:

```text
momentDepth = length(worldPos - lightPosition) / lightRadius
```

The fixed-function depth cubemap remains projection-depth based and exists only for correct raster visibility per face. The sampling cubemap stores radial distance moments.

For geometry-shader point shadow rendering, the fragment shader already receives world-space `FragPos`. That path can write VSM/EVSM moments with the same radial depth source currently used by `PointLightShadowDepth.fs`.

## Shader Encoding

### Receiver-derivative bias caveat

The canonical VSM encoder uses `dFdx`/`dFdy` to add a receiver-plane second-moment term. Screen-space derivatives are invalid when adjacent fragments cross discontinuities:

- **Cubemap face boundaries** in the geometry-shader-amplified point shadow path. Adjacent fragments may belong to different cube faces and produce huge bogus derivatives.
- **Atlas tile boundaries** once the dynamic atlas is in use. Two adjacent fragments can target different lights or tiles.
- **Cascade array layer seams** in the directional cascade path.
- **Alpha-tested / `discard`-bearing casters**, where neighboring quad fragments may be killed and the surviving fragment's derivatives become unreliable.

Mitigations:

- For the GS point-shadow path, drop the derivative term and rely on `ShadowMomentMinVariance` only.
- For atlas-packed renders, use the per-tile viewport so each draw is bounded; clamp the derivative-derived term to a configurable max.
- For masked materials, use a separate moment-writer permutation that omits the derivative term.

### Permutation strategy

Encoding x light type x forward/deferred x cascade-count produces a real permutation explosion. The plan is:

- Treat `EShadowMapEncoding` as a **static feature flag** consumed by the uber-shader feature system, not a dynamic uniform branch in inner loops. This matches existing uber-feature defaults patterns.
- The receiver dispatcher (`XRENGINE_SampleShadowMapVisibility*`) is a thin static-`#if` switch over the bound encoding constant, so each material variant compiles down to one path.
- Per-light dynamic encoding switching is supported by binding a different shader variant, not by branching at runtime.

Add a shared shadow moment writer snippet, for example:

- `Build/CommonAssets/Shaders/Snippets/ShadowMomentEncoding.glsl`

Suggested helpers:

```glsl
vec2 XRENGINE_EncodeVsmMoments(float depth, float minVariance)
{
    float dx = dFdx(depth);
    float dy = dFdy(depth);
    float moment2 = depth * depth + 0.25 * (dx * dx + dy * dy);
    return vec2(depth, max(moment2, depth * depth + minVariance));
}

vec2 XRENGINE_EncodeEvsm2Moments(float depth, float exponent, float minVariance)
{
    float warped = exp(exponent * depth);
    return XRENGINE_EncodeVsmMoments(warped, minVariance);
}

vec4 XRENGINE_EncodeEvsm4Moments(float depth, float positiveExponent, float negativeExponent, float minVariance)
{
    float positive = exp(positiveExponent * depth);
    float negative = -exp(-negativeExponent * depth);
    vec2 p = XRENGINE_EncodeVsmMoments(positive, minVariance);
    vec2 n = XRENGINE_EncodeVsmMoments(negative, minVariance);
    return vec4(p, n);
}
```

The actual implementation should clamp exponents to ranges safe for the selected texture format. `RGBA16f` EVSM needs much smaller exponents than `RGBA32f`.

## Shader Sampling

### Cascade blending rule

For cascaded directional shadows, **never blend moment vectors across cascades and then run Chebyshev**. Variance is non-linear in the moments, and blended moments produce wrong visibility. The receiver must:

1. Compute Chebyshev/EVSM visibility per cascade independently.
2. Blend the resulting *visibility scalars* in the cascade transition band.

This applies to `Depth` mode too if any path linearly blends pre-compare depth values; for Depth, blend the post-compare visibility instead.

### Loss of hardware depth-compare

Moving directional Depth mode to a color sampling texture (`R16f`) for uniformity drops implicit hardware PCF. Two acceptable paths:

1. **Unified color path (preferred for v1):** all encodings, including `Depth`, sample a color texture and run manual compare. This makes the atlas, the dispatcher, and the resource factory simpler. Manual PCF must match or beat the previous quality bar; validate with the `Depth + Fixed Poisson` and `Depth + PCSS` reference scenes.
2. **Dual path:** keep `sampler2DShadow` / `sampler2DArrayShadow` for Depth-mode directional and only color textures for moment encodings. Costs an extra binding slot and an extra dispatcher branch.

Decision: take path 1 for v1 unless visual validation regresses.

Extend `ShadowSampling.glsl` with moment samplers for:

- `sampler2D`
- `sampler2DArray`
- `samplerCube`

Core VSM visibility:

```glsl
float XRENGINE_ChebyshevUpperBound(vec2 moments, float receiverDepth, float minVariance, float bleedReduction)
{
    if (receiverDepth <= moments.x)
        return 1.0;

    float variance = max(moments.y - moments.x * moments.x, minVariance);
    float d = receiverDepth - moments.x;
    float p = variance / (variance + d * d);
    return clamp((p - bleedReduction) / max(1.0 - bleedReduction, 0.0001), 0.0, 1.0);
}
```

EVSM2 samples positive warped moments only. EVSM4 samples positive and negative warped moments and returns the minimum visibility estimate from both.

Depth mode keeps the existing manual compare path:

- `XRENGINE_SampleShadowMapFiltered`
- `XRENGINE_SampleShadowMapArrayFiltered`
- `XRENGINE_SampleShadowCubeFiltered`

Moment modes use a new dispatcher:

```glsl
float XRENGINE_SampleShadowMapVisibility2D(..., int encoding, ...)
float XRENGINE_SampleShadowMapVisibilityArray(..., int encoding, ...)
float XRENGINE_SampleShadowMapVisibilityCube(..., int encoding, ...)
```

These dispatchers should keep the receiver shader compact and prevent forward and deferred paths from drifting.

## Filtering And Blur

VSM/EVSM should support three filter layers:

1. Hardware linear filtering on the sampling texture.
2. Optional separable blur after shadow rendering.
3. Optional mip selection for large penumbrae or atlas LOD sampling.

Recommended first implementation:

- use linear filtering for moment textures
- add a separable blur pass only for moment maps
- skip mipmapped moment shadows initially unless the texture has proper gutters or is not atlas-packed

When the dynamic atlas lands, blur must respect tile boundaries. A compute blur that receives tile rects is safer than blind full-texture blur.

## Bias Rules

### Depth space and warping inputs

The value fed into the EVSM exponential warp must be a **linearized, [0,1]-normalized** depth, not raw post-projection `gl_FragCoord.z`. Non-linear projected depth concentrates exponent dynamic range near the near plane, which interacts badly with the format-driven exponent clamps. Pipeline:

1. Compute the linear normalized depth `d_lin` from view-space Z and the shadow camera near/far (or radial distance for point lights).
2. Apply the warp `exp(c+ * d_lin)` and `-exp(-c- * d_lin)`.
3. Encode moments from the warped values.

The receiver must perform the same linearization on its reconstructed shadow-space depth before the warp/compare. The `worldToShadow` matrix and per-light near/far are already required for this.

### Receiver bias

Depth-comparison modes keep current bias behavior.

Moment modes need less receiver bias, but not zero bias:

- keep `ShadowMinBias` / `ShadowMaxBias` as the receiver-depth offset source
- keep per-cascade directional bias values
- add `ShadowMomentMinVariance` to control acne/light-bleed tradeoff
- use lower default bias in VSM/EVSM presets than PCF/PCSS presets

For point lights, retain the existing radial relative-bias guard in spirit. The compare depth should still account for:

- cubemap face texel angular span
- user bias
- radial texture precision
- reconstructed receiver position precision

The moment variance floor can reduce acne, but it should not replace receiver bias entirely.

## Runtime Resource Model

Introduce a common resource object:

```csharp
public enum EShadowProjectionLayout
{
    Texture2D = 0,        // single 2D map (single-cascade directional, spot)
    Texture2DArray = 1,   // cascaded directional, atlas page layer
    TextureCube = 2,      // legacy cube point shadows
    Texture2DArrayCubeFaces = 3, // point shadow expressed as 6 array layers
}

public sealed class ShadowMapResource
{
    public EShadowMapEncoding Encoding { get; }
    public EShadowProjectionLayout Layout { get; }
    public XRTexture SamplingTexture { get; }
    public XRTexture? RasterDepthTexture { get; }
    public XRFrameBuffer[] FrameBuffers { get; }
    public uint Width { get; }
    public uint Height { get; }
    public int LayerCount { get; }   // cascade count, cube face count, or 1
    public int MsaaSamples { get; }
}
```

`Layout` removes the ambiguity between "`LayerCount = 6` because cube" and "`LayerCount = 6` because cascades."

Light components should request resources through a factory:

```csharp
ShadowMapResource ShadowMapResourceFactory.Create(
    ELightShadowProjection projection,
    EShadowMapEncoding encoding,
    uint width,
    uint height,
    int layerCount);
```

This avoids scattering format decisions across `DirectionalLightComponent`, `SpotLightComponent`, and `PointLightComponent`.

## Editor Surface

The ImGui light editors should expose:

- `Shadow Map Encoding`
  - Depth
  - VSM
  - EVSM 2-channel
  - EVSM 4-channel
- `Depth Filter`
  - Hard
  - Fixed Poisson
  - Vogel Disk
  - Contact Hardening PCSS
- `Moment Filter`
  - min variance
  - light bleed reduction
  - positive exponent
  - negative exponent
  - blur radius
  - blur passes
  - mip bias

When encoding is `Depth`, moment controls should be hidden or disabled. When encoding is VSM/EVSM, depth filter controls should remain visible but disabled with a tooltip explaining that moment filtering replaces manual depth filtering.

### Moment debug viewer

Add a render inspector panel (or a debug mode on the existing shadow debug overlay) that can visualize, per shadow map:

- raw `M1` channel
- raw `M2` channel
- variance (`M2 - M1*M1`)
- warped channels for EVSM2/EVSM4 individually
- a "bleed mask" that highlights texels where Chebyshev returned `< 1` but the receiver was in front of the stored mean (light-leak indicator)
- the active clear sentinel, so untouched gutter texels are obvious

This ships in Phase 2 with spot lights, not in a polish phase. VSM debugging without a viewer is impractical.

## Implementation Plan

### Phase 1: Shared API and resource factory

1. Add `EShadowMapEncoding`.
2. Add moment settings to `LightComponent` using `SetField(...)`.
3. Add format selection helpers for depth, VSM, EVSM2, and EVSM4.
4. Add a `ShadowMapResource` wrapper or equivalent resource record.
5. Keep current behavior when `ShadowMapEncoding == Depth`.

### Phase 2: Spot light VSM/EVSM

Spot lights are the smallest slice because they already render to a color `ShadowMap` texture.

1. Extend `SpotLightComponent.GetShadowMapMaterial(...)` to choose color format by encoding.
2. Add a moment-writing fragment shader (with a masked-caster permutation that drops the derivative term).
3. Add `sampler2D` VSM/EVSM sampling helpers.
4. Update `DeferredLightingSpot.fs` and `ForwardLighting.glsl`.
5. Add per-resource (non-atlas) separable blur for moment maps so spot VSM ships *useful*. Tile-aware blur is reworked in Phase 6 when the atlas lands.
6. Implement the moment debug viewer for `sampler2D` encodings.
7. Add format-capability probing and the demotion-to-Depth fallback path.
8. Add unit tests that verify shader declarations, spot resource format selection, and clear-value computation per encoding.

### Phase 3: Point light VSM/EVSM

1. Extend `PointLightShadowDepth.fs` to write depth, VSM, EVSM2, or EVSM4.
2. Ensure geometry-shader and six-pass fallback paths use the same encoding.
3. Add `samplerCube` moment sampling helpers.
4. Update deferred and forward point-light receivers.
5. Validate masked shadow-caster variants still write correct radial moments.

### Phase 4: Directional single-map VSM/EVSM

1. Add a color sampling texture to directional single shadow resources.
2. Keep a depth attachment for rasterization.
3. Update deferred and forward directional fallback sampling.
4. Validate volumetric fog directional shadow sampling if it consumes the same primary directional bindings.

### Phase 5: Directional cascaded VSM/EVSM

1. Add color moment array resources for cascades.
2. Keep per-layer depth attachments for rasterization.
3. Extend cascade receiver sampling for VSM/EVSM.
4. Keep per-cascade bias and receiver offset uniform behavior.
5. Enforce the per-cascade-then-blend-visibility rule in the receiver dispatcher; add a unit test that fails if a build linearly blends moment vectors across cascades.
6. Require stable cascade texel snapping before enabling VSM/EVSM directional. Moment maps amplify sub-texel jitter; treat snapping as a precondition, not a polish item.
7. Validate cascade blending and debug cascade colors.

### Phase 6: Moment blur and mip filtering

1. Replace the per-resource Phase 2 blur with a tile-aware separable blur once the atlas lands.
2. Add optional mip generation only where gutters are safe.
3. Expose blur and mip settings in the editor.
4. Add optional MSAA shadow rasterization with single-sample moment resolve, gated on non-atlas resources first.

### Phase 7: Other shadow consumers

Moving the receiver to a dispatcher means every consumer of the existing shadow bindings must be updated or explicitly excluded. Enumerate and convert:

1. Volumetric fog directional shadow sampling.
2. SSGI / probe GI directional shadowing.
3. Water and translucency that sample the primary directional shadow.
4. Decals that project via shadow matrices.
5. GPU particle lighting paths.
6. Any remaining custom user materials sampling the legacy bindings - flag a build-time check that the legacy binding name is not used outside the dispatcher.

## Validation Plan

### Unit tests

Add focused tests in `XREngine.UnitTests/Rendering/` for:

- enum/default values
- `LightComponent` moment settings using `SetField(...)`
- spot, point, directional, and cascade format selection
- shader source contains VSM/EVSM helpers
- forward and deferred receivers dispatch on `ShadowMapEncoding`
- default `Depth` mode remains unchanged

### Visual tests

Use the Unit Testing World with:

- one directional light with cascades
- one long-range spot light
- one point light near shadow casters
- masked foliage or cutout material
- moving receiver and moving light cases

Compare:

- Depth + PCSS
- VSM
- EVSM2
- EVSM4

### Performance tests

Capture profiler timings around:

- `Lights3DCollection.RenderShadowMaps`
- `GLMeshRenderer.Render.SetMaterialUniforms`
- deferred light passes
- forward material draws with local lights

Expected VSM/EVSM wins should appear mostly on receiver shading when current PCF/PCSS tap counts are high. Shadow rendering may cost more because moment maps write color and may blur.

## Risks

### Light bleeding

Plain VSM can leak light through thin or overlapping blockers.

Mitigation:

- expose light-bleed reduction
- prefer EVSM for production presets
- keep depth PCF/PCSS as the robust fallback

### EVSM overflow or precision loss

EVSM exponents can overflow half-float formats.

Mitigation:

- clamp exponent defaults by selected format
- offer `RGBA32f` as a high-quality option
- log or clamp invalid user settings

### Atlas filtering bleed

Moment maps need filtering, but atlas neighbors must not leak.

Mitigation:

- add gutters around tiles
- clamp sample UVs to inner tile rect
- make blur and mip generation tile-aware

### Point-light seam differences

Moment-filtered cubemap faces can expose seams differently than current point shadow filtering.

Mitigation:

- keep radial depth as the sampled value
- use consistent face edge filtering or move point shadows to the 2D atlas face layout planned in `dynamic-shadow-atlas-lod-plan.md`

### Reversed-Z drift

If any subsystem flips depth direction independently of the shadow path, Chebyshev's early-out and the clear sentinel will silently invert and produce "all lit" or "all shadowed" results.

Mitigation:

- centralize the depth-direction constant in one shader header and one C# constant
- add a unit test that asserts the encoder, the clear value, and the receiver dispatcher all read from the same constant

## Open Items And Cross-Plan Coupling

These must be resolved jointly with `dynamic-shadow-atlas-lod-plan.md` before Phase 4 (directional single map) lands, and ideally before Phase 2 ships:

1. **Per-encoding atlas budgets.** The atlas plan groups pages by sampling format. This plan must specify default page counts per encoding and the demotion policy (encoding-down vs LOD-down) when budget is exhausted.
2. **Encoding flip ownership.** When a light changes `ShadowMapEncoding` at runtime, the atlas manager retires the old `ShadowRequestKey` allocation and the light reissues a new request keyed to the new encoding. No in-place reformat.
3. **Gutter clear value.** Atlas gutters must be cleared with the encoding-specific unoccluded sentinel, not zero, when blur or mips are active.
4. **VSSM (variance-based PCSS) scope.** Out of scope for v1 and tracked in [Post-v1 Advanced Shadow Features Plan](post-v1-advanced-shadow-features-plan.md). The depth-mode PCSS path remains the only contact-hardening option until VSSM is explicitly scheduled.
5. **Tile-aware MSAA resolve.** Out of scope for v1 atlas and tracked in [Post-v1 Advanced Shadow Features Plan](post-v1-advanced-shadow-features-plan.md). MSAA shadow rasterization is enabled only on non-atlas resources until the resolve respects tile boundaries.

## Recommended Initial Scope

Start with spot lights:

1. Add `EShadowMapEncoding` and moment settings.
2. Implement VSM and EVSM2 for spot shadows only.
3. Wire deferred spot sampling and forward spot sampling.
4. Validate against the existing depth PCSS spot path.

That slice exercises the full API shape while avoiding directional cascades and point cubemap seams until the core moment math is proven.
