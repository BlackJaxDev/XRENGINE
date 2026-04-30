# VSM And EVSM Shadow Filtering TODO

> Status: **active phased TODO**
> Scope: shadow map encodings, resource creation, shaders, editor controls, validation.

## Target Outcome

Add Variance Shadow Map (VSM) and Exponential Variance Shadow Map (EVSM) options for directional, spot, and point lights while keeping the current depth-comparison filters:

- hard shadows
- fixed-radius Poisson filtering
- Vogel-disk filtering
- contact-hardening PCSS
- screen-space/contact shadow multiplication
- directional cascades

The v1 architecture separates "what the shadow map stores" from "how the receiver filters it." Depth, VSM, EVSM2, and EVSM4 are map encodings. PCF, Vogel, and PCSS are depth-filtering modes. Moment filtering uses moment-specific settings.

## Non-Negotiable Design Rules

- [ ] `Depth` remains the default behavior until a light explicitly opts into moment maps.
- [ ] `EShadowMapEncoding` is separate from depth filter mode.
- [ ] Moment maps encode linear normalized depth, not raw projected `gl_FragCoord.z`.
- [ ] Point-light moment maps encode radial normalized depth.
- [ ] The encoder, clear value, receiver comparison, and reversed-Z/depth-direction constant always agree.
- [ ] Moment map clears use an unoccluded sentinel, never zero.
- [ ] EVSM4 uses signed floating-point formats; unsigned formats are forbidden.
- [ ] Format selection probes render-target and linear-filter capability before allocation.
- [ ] Unsupported formats demote deterministically to a supported encoding, logging once per light.
- [ ] Moment controls on `XRBase`-derived light components use `SetField(...)`.
- [ ] No per-frame allocations are introduced in render, collect-visible, or light-binding paths.
- [ ] Cascades blend post-filter visibility values, never raw depth or moment vectors.
- [ ] Atlas gutters use the same encoding-specific unoccluded clear sentinel.
- [ ] Contact shadows stay independent and multiply on top of both depth and moment visibility.

## Public API Shape

Add the encoding enum:

```csharp
public enum EShadowMapEncoding
{
    Depth = 0,
    Variance2 = 1,
    ExponentialVariance2 = 2,
    ExponentialVariance4 = 3,
}
```

Keep or rename the depth-filter enum:

```csharp
public enum ESoftShadowMode
{
    Hard = 0,
    FixedPoisson = 1,
    ContactHardeningPcss = 2,
    VogelDisk = 3,
}
```

Pre-v1 cleanup option:

- [ ] Rename `ESoftShadowMode` to `EShadowDepthFilterMode`.
- [ ] Rename `SoftShadowMode` to `DepthShadowFilterMode`.
- [ ] Present both encoding and filtering under an editor group named `Shadow Filtering`.

Add moment settings to `LightComponent`:

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

Introduce a common resource wrapper or equivalent record:

```csharp
public enum EShadowProjectionLayout
{
    Texture2D = 0,
    Texture2DArray = 1,
    TextureCube = 2,
    Texture2DArrayCubeFaces = 3,
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
    public int LayerCount { get; }
    public int MsaaSamples { get; }
}
```

## Encoding Defaults

| Encoding | Channels | Default format | Notes |
|---|---:|---|---|
| `Depth` | 1 | `R16f` color path or legacy depth texture | Manual compare in unified path. |
| `Variance2` | 2 | `RG16f` | First and second moments of linear normalized depth. |
| `ExponentialVariance2` | 2 | `RG16f` or `RG32f` | Positive exponential warped moments. |
| `ExponentialVariance4` | 4 | `RGBA16f` or `RGBA32f` | Positive and negative exponential warped moments. |

## Clear Sentinel TODOs

- [x] `Depth`: clear to `1.0` under normal depth direction, or `0.0` under reversed-Z.
- [x] `Variance2`: clear to `(1, 1)` under normal depth direction.
- [x] `ExponentialVariance2`: clear to `(exp(cPositive), exp(cPositive)^2)` under normal depth direction.
- [x] `ExponentialVariance4`: clear to `(exp(cPositive), exp(cPositive)^2, -exp(-cNegative), exp(-cNegative)^2)` under normal depth direction.
- [x] Compute clear values from active exponents and depth direction during request/resource setup.
- [ ] Use the same sentinel for untouched atlas texels and gutters.

## Phase 0: Cross-Plan Decisions And Consumer Audit

**Goal:** resolve shadow-resource ownership and atlas coupling before implementation reaches shader consumers.

### Tasks

- [x] Audit every consumer of existing shadow bindings:
  - [x] `LightComponent`
  - [x] `DirectionalLightComponent`
  - [x] `DirectionalLightComponent.CascadeShadows`
  - [x] `SpotLightComponent`
  - [x] `PointLightComponent`
  - [x] `ShadowSampling.glsl`
  - [x] `ForwardLighting.glsl`
  - [x] deferred directional, spot, and point lighting shaders
  - [x] volumetric fog
  - [x] SSGI / probe GI
  - [x] water and translucency
  - [x] decals
  - [x] GPU particles
  - [x] custom material bindings using legacy shadow names
- [x] Decide whether v1 uses the unified color path for `Depth`.
- [x] Decide whether `ShadowMapResource` replaces ambiguous per-light properties immediately.
- [x] Resolve atlas coupling with `dynamic-shadow-atlas-lod-plan.md`:
  - [x] per-encoding page budgets,
  - [x] memory cap behavior,
  - [x] encoding-demotion versus LOD-demotion order,
  - [x] ownership of encoding flips,
  - [x] gutter clear sentinel handling,
  - [x] tile-aware blur ownership,
  - [x] tile-aware MSAA resolve scope.
- [x] Define a central C# and GLSL depth-direction constant.
- [x] Decide EVSM exponent clamp defaults for `RG16f`, `RG32f`, `RGBA16f`, and `RGBA32f`.
- [x] Mark VSSM or variance-based PCSS as out of scope for v1.

Phase 0 decisions and the migration table are captured in [Shadow Resource Migration Audit](../design/shadow-resource-migration-audit.md).

### Exit Criteria

- [x] Every current shadow consumer has a migration or exclusion entry.
- [x] Atlas and moment-map plans agree on encoding, clear, blur, mip, and budget policy.
- [x] Default `Depth` behavior has an explicit compatibility target.

### Validation

- [x] `rg "ShadowMap|ShadowDepth|sampler.*Shadow|PointLightShadowMaps|SpotLightShadowMaps|CascadedShadow|ShadowSampling" XRENGINE Build XREngine.Editor docs` scoped with generated/dependency exclusions; unbounded scan timed out as noted in the audit.
- [x] Shader binding list reviewed for legacy names that will need a dispatcher.

## Phase 1: Shared API, Settings, And Resource Factory

**Goal:** add the public encoding/resource model without changing visible default rendering.

### Tasks

- [x] Add `EShadowMapEncoding`.
- [x] Add moment settings to `LightComponent` using `SetField(...)`.
- [x] Keep all default values equivalent to current `Depth` mode.
- [x] Add format selection helpers for depth, VSM, EVSM2, and EVSM4.
- [x] Add device capability probes for render-target and linear-filter support.
- [x] Add one-time-per-light demotion logging when an encoding is unsupported.
- [x] Add clear sentinel calculation helpers.
- [x] Add EVSM exponent clamps by selected format.
- [x] Add `ShadowMapResource` or equivalent wrapper.
- [x] Add `ShadowMapResourceFactory.Create(...)`.
- [x] Preserve legacy properties only as compatibility shims if needed.
- [x] Add editor grouping for `Shadow Map Encoding`, `Depth Filter`, and `Moment Filter`.
- [x] Hide or disable moment controls when encoding is `Depth`.
- [x] Disable depth-filter controls for moment encodings with a tooltip explaining that moment visibility replaces depth filtering.

### Exit Criteria

- [ ] With default settings, existing depth shadows render as before.
- [x] Unsupported moment encodings fall back deterministically to `Depth`.
- [x] No light component property setter bypasses `SetField(...)`.
- [x] Resource format decisions are centralized.

### Validation

- [x] Unit tests for enum/default values.
- [x] Unit tests for `SetField(...)`-backed moment settings.
- [x] Unit tests for format selection, demotion, exponent clamps, and clear sentinel calculation.
- [x] Build editor project.

Validation note: `Build-Editor` succeeds. Focused `dotnet test` discovery is blocked by unrelated existing UnitTests compile errors in audio/timing/VR test files, so the Phase 1 tests are added but not yet executable through the full test assembly.

## Phase 2: Spot Light VSM/EVSM Slice

**Goal:** ship the smallest useful end-to-end moment-shadow path.

### Tasks

- [x] Extend spot shadow resource creation to use selected encoding:
  - [x] `Depth` -> `R16f`
  - [x] `Variance2` -> `RG16f`
  - [x] `ExponentialVariance2` -> `RG16f` or `RG32f`
  - [x] `ExponentialVariance4` -> `RGBA16f` or `RGBA32f`
- [x] Add `Build/CommonAssets/Shaders/Snippets/ShadowMomentEncoding.glsl`.
- [x] Add VSM/EVSM moment writer helpers.
- [x] Add masked-caster permutation that omits or clamps derivative-derived variance.
- [x] Write moment depth from the same normalized projected depth that receivers compare.
- [x] Add `sampler2D` VSM and EVSM sampling helpers.
- [x] Add `XRENGINE_ChebyshevUpperBound(...)`.
- [x] Update `DeferredLightingSpot.fs`.
- [x] Update `ForwardLighting.glsl` spot path.
- [x] Apply contact shadow multiplication after map visibility.
- [ ] Add per-resource separable blur for non-atlas moment spot maps.
- [ ] Implement moment debug viewer for `sampler2D` resources:
  - [ ] `M1`
  - [ ] `M2`
  - [ ] variance
  - [ ] EVSM warped channels
  - [ ] bleed mask
  - [ ] active clear sentinel
- [x] Add shader declarations/source tests for spot moment helpers.

### Exit Criteria

- [x] Spot lights support `Depth`, `Variance2`, `ExponentialVariance2`, and `ExponentialVariance4`.
- [x] Spot depth mode still supports existing hard, Poisson, Vogel, and PCSS filters.
- [x] Spot moment modes ignore depth filter for map visibility and use moment settings.
- [ ] Debug viewer can explain light bleeding and invalid clears.

### Validation

- [x] Unit tests for spot resource format selection and clear values.
- [x] Build editor project after spot moment path changes.

Validation note: `dotnet build .\XREngine.Editor\XREngine.Editor.csproj /property:GenerateFullPaths=true /consoleloggerparameters:ErrorsOnly` succeeds. Focused `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~ShadowMapMomentPhase2Tests --no-restore /property:GenerateFullPaths=true /consoleloggerparameters:ErrorsOnly` is blocked before execution by unrelated existing UnitTests compile errors in audio/timing/VR test files.

- [ ] Visual comparison:
  - [ ] `Depth + PCSS`
  - [ ] VSM
  - [ ] EVSM2
  - [ ] EVSM4
- [ ] Long-range spot light scene.
- [ ] Masked/cutout caster scene.
- [ ] Profiler capture for shadow render, blur, and receiver cost.

## Phase 3: Point Light VSM/EVSM

**Goal:** add moment encodings to point shadows while preserving radial depth semantics.

### Tasks

- [ ] Extend point shadow resources to selected encoding.
- [ ] Update `PointLightShadowDepth.fs` or successor shader to write depth, VSM, EVSM2, or EVSM4.
- [ ] Encode radial depth: `length(worldPos - lightPosition) / lightRadius`.
- [ ] Keep fixed-function depth cubemap only for raster visibility.
- [ ] Ensure geometry-shader and six-pass fallback paths use the same moment encoding.
- [ ] For geometry-shader path, omit or clamp derivative-derived second-moment term near cube face discontinuities.
- [ ] Add `samplerCube` VSM and EVSM sampling helpers.
- [ ] Update deferred point-light receiver.
- [ ] Update forward point-light receiver.
- [ ] Validate masked caster variants write correct radial moments.

### Exit Criteria

- [ ] Point light VSM/EVSM samples radial moment depth consistently.
- [ ] Geometry-shader and six-pass paths match within expected filtering differences.
- [ ] Face seams are no worse than depth mode.

### Validation

- [ ] Point light near shadow casters.
- [ ] Moving receiver crossing cube face boundaries.
- [ ] Masked point-shadow caster.
- [ ] EVSM overflow/clamp test for point lights.

## Phase 4: Directional Single-Map VSM/EVSM

**Goal:** add moment encodings to non-cascaded directional shadows.

### Tasks

- [ ] Add color sampling texture to directional single-shadow resources.
- [ ] Keep depth attachment for rasterization and early rejection.
- [ ] Write linear normalized directional shadow depth or moments.
- [ ] Update deferred directional fallback sampling.
- [ ] Update forward directional fallback sampling.
- [ ] Update volumetric fog directional sampling if it consumes the primary directional binding.
- [ ] Confirm unified color `Depth` path quality against legacy hardware depth path.

### Exit Criteria

- [ ] Directional single-map mode supports all encodings.
- [ ] Depth mode quality remains acceptable with manual compare.
- [ ] Known directional consumers either use the dispatcher or are explicitly excluded.

### Validation

- [ ] Directional single-light scene.
- [ ] Volumetric fog scene if applicable.
- [ ] Depth manual-compare reference against previous behavior.

## Phase 5: Directional Cascaded VSM/EVSM

**Goal:** support moment-filtered cascades without invalid cascade blending.

### Tasks

- [ ] Add color moment array resources for cascades.
- [ ] Keep per-layer depth attachments for rasterization.
- [ ] Add `sampler2DArray` VSM and EVSM helpers.
- [ ] Extend cascade receiver dispatcher for all encodings.
- [ ] Keep per-cascade bias and receiver offset uniform behavior.
- [ ] Enforce per-cascade visibility computation before blend.
- [ ] Add a shader/source test that fails if moment vectors are blended across cascades before Chebyshev.
- [ ] Require stable cascade texel snapping before enabling directional moment maps.
- [ ] Validate cascade debug colors and blend widths.

### Exit Criteria

- [ ] Cascaded directional shadows support all encodings.
- [ ] Cascade transition bands blend visibility scalars only.
- [ ] Moment maps do not amplify cascade jitter beyond acceptable tolerance.

### Validation

- [ ] Directional cascaded scene with camera movement.
- [ ] Cascade transition-band scene.
- [ ] Mixed depth and moment encoding preset checks.

## Phase 6: Blur, Mip Filtering, And MSAA

**Goal:** make moment shadows useful at large radii without leaking across resources or atlas tiles.

### Tasks

- [ ] Keep Phase 2 per-resource blur for non-atlas resources until atlas blur is ready.
- [ ] Add tile-aware separable blur once atlas tile rects are available.
- [ ] Clamp blur samples to tile inner rects.
- [ ] Add optional mip generation only where gutters are valid.
- [ ] Add `ShadowMomentMipBias` support in receiver sampling.
- [ ] Expose blur and mip controls in editor.
- [ ] Add optional MSAA shadow rasterization for non-atlas resources first.
- [ ] Add single-sample moment resolve from MSAA depth source.
- [ ] Keep tile-aware MSAA resolve out of v1 atlas unless separately validated.

### Exit Criteria

- [ ] Moment blur does not sample neighboring atlas tiles.
- [ ] Mipmapped moment shadows are disabled where gutters are unsafe.
- [ ] MSAA resolve is opt-in and format-capability checked.

### Validation

- [ ] Wide soft spot shadow with blur.
- [ ] Atlas gutter leak scene once atlas integration exists.
- [ ] MSAA moment resolve comparison on non-atlas spot or directional resource.

## Phase 7: Atlas Integration

**Goal:** make moment encodings first-class atlas formats.

### Tasks

- [ ] Map `EShadowMapEncoding` to atlas group:
  - [ ] depth atlas,
  - [ ] VSM atlas,
  - [ ] EVSM2 atlas,
  - [ ] EVSM4 atlas.
- [ ] Encode clear sentinel per request and use it for tile clears and gutters.
- [ ] Make encoding part of allocation compatibility.
- [ ] Retire and reissue allocations on runtime encoding changes.
- [ ] Implement agreed demotion policy when encoding budget is exhausted.
- [ ] Publish moment filter parameters through `ShadowAtlasTile.filterParams`.
- [ ] Verify fallback metadata short-circuits before sampling missing moment tiles.
- [ ] Add atlas debug views for moment channels.

### Exit Criteria

- [ ] Atlas can host depth and moment tiles without resource-format ambiguity.
- [ ] Runtime encoding flips do not sample stale incompatible data.
- [ ] Moment filtering respects atlas tile boundaries.

### Validation

- [ ] Mixed encoding atlas scene.
- [ ] Encoding flip at runtime.
- [ ] Oversubscribed moment atlas scene exercising demotion and fallback.

## Phase 8: Other Shadow Consumers And Legacy Cleanup

**Goal:** prevent old shadow binding assumptions from surviving outside the dispatcher.

### Tasks

- [ ] Convert or explicitly exclude volumetric fog directional sampling.
- [ ] Convert or explicitly exclude SSGI / probe GI directional shadowing.
- [ ] Convert or explicitly exclude water and translucency shadow sampling.
- [ ] Convert or explicitly exclude decals using shadow matrices.
- [ ] Convert or explicitly exclude GPU particle lighting.
- [ ] Add a build-time or test-time check that legacy binding names are not used outside approved compatibility shims.
- [ ] Remove compatibility shims once common materials and debug views use the dispatcher.
- [ ] Update relevant docs and editor help text for user-visible settings.

### Exit Criteria

- [ ] No accidental direct dependency on old per-light shadow texture bindings remains.
- [ ] Compatibility shims have owners and removal criteria.
- [ ] User-facing setting docs are current.

### Validation

- [ ] Shader source scan for legacy binding names.
- [ ] Visual smoke test of each converted consumer.
- [ ] Editor inspector smoke test.

## Shader Helper TODOs

### Encoding Helpers

- [ ] Add `XRENGINE_EncodeVsmMoments(float depth, float minVariance)`.
- [ ] Add `XRENGINE_EncodeEvsm2Moments(float depth, float exponent, float minVariance)`.
- [ ] Add `XRENGINE_EncodeEvsm4Moments(float depth, float positiveExponent, float negativeExponent, float minVariance)`.
- [ ] Clamp exponents based on selected format.
- [ ] Add a derivative-free or derivative-clamped path for cube faces, atlas tiles, cascades, and masked casters.

### Sampling Helpers

- [ ] Add `XRENGINE_ChebyshevUpperBound(...)`.
- [ ] Add EVSM2 visibility helper.
- [ ] Add EVSM4 visibility helper using min of positive and negative visibility estimates.
- [ ] Add `sampler2D`, `sampler2DArray`, and `samplerCube` dispatchers.
- [ ] Keep depth compare helpers for hard, Poisson, Vogel, and PCSS.
- [ ] Apply contact shadows after map visibility.

## Validation Matrix

### Unit Tests

- [ ] enum/default values
- [ ] `LightComponent` moment settings use `SetField(...)`
- [ ] format selection per encoding and light type
- [ ] format capability demotion
- [ ] EVSM exponent clamps
- [ ] clear sentinel calculation
- [ ] central depth-direction consistency
- [ ] shader source contains moment encoding helpers
- [ ] shader source contains receiver dispatchers
- [ ] cascade receiver does not blend raw moment vectors
- [ ] default `Depth` mode remains unchanged

### Visual Tests

- [ ] one directional light with cascades
- [ ] one long-range spot light
- [ ] one point light near shadow casters
- [ ] masked foliage or cutout material
- [ ] moving receiver
- [ ] moving light
- [ ] cascade transition bands
- [ ] atlas gutter leak scene after atlas integration

Compare these presets:

- [ ] `Depth + Hard`
- [ ] `Depth + FixedPoisson`
- [ ] `Depth + VogelDisk`
- [ ] `Depth + ContactHardeningPcss`
- [ ] `Variance2`
- [ ] `ExponentialVariance2`
- [ ] `ExponentialVariance4`

### Performance Tests

- [ ] `Lights3DCollection.RenderShadowMaps`
- [ ] moment blur pass cost
- [ ] deferred light passes
- [ ] forward material draws with local lights
- [ ] `GLMeshRenderer.Render.SetMaterialUniforms`
- [ ] shader sampler/binding count
- [ ] memory use by encoding

## Risk Checklist

- [ ] Light bleeding is mitigated with bleed reduction, min variance, EVSM presets, and depth fallback.
- [ ] EVSM overflow is mitigated with format-specific exponent clamps and optional 32-bit formats.
- [ ] Atlas filtering bleed is mitigated with gutters, inner-rect clamps, and tile-aware blur/mips.
- [ ] Point-light seams are mitigated with radial depth consistency and face-boundary validation.
- [ ] Reversed-Z drift is mitigated with one central depth-direction constant and tests.
- [ ] Shader permutation growth is monitored through the existing uber-feature tooling.
- [ ] Derivative-derived moment variance is disabled or clamped where derivatives are unreliable.

## Out Of Scope For v1

- [ ] Variance-based PCSS / VSSM.
- [ ] Tile-aware MSAA resolve for atlas resources.
- [ ] Translucent, colored, stochastic, deep-shadow, or Fourier opacity shadows.
- [ ] Runtime per-fragment dynamic branching between encodings in hot receiver loops.

These post-v1 features are tracked in [Post-v1 Advanced Shadow Features Plan](../design/post-v1-advanced-shadow-features-plan.md).

## First Implementation Slice

Start with Phases 0-2:

1. Add the shared API and resource factory while preserving default depth behavior.
2. Implement VSM and EVSM2 for spot lights only.
3. Wire deferred and forward spot receivers.
4. Ship the moment debug viewer with the spot slice.

Spot lights exercise the full moment-map API without directional cascade blending or point cubemap seams. Once spot shadows are stable, point lights, directional single maps, cascades, and atlas integration can reuse the same encoding, sampling, and validation model.
