# Atmospheric Scattering Component Design

Last Updated: 2026-05-03
Status: Design proposal
Primary reference: [GPU Gems 2, Chapter 16: Accurate Atmospheric Scattering](https://developer.nvidia.com/gpugems/gpugems2/part-ii-shading-lighting-and-shadows/chapter-16-accurate-atmospheric-scattering)

## Recommendation

Add atmospheric scattering as a world-scoped component with a matching
camera/pipeline settings stage:

- `AtmosphericScatteringComponent` owns the physical atmosphere in the scene:
  planet center/radii, scattering coefficients, sun source, quality defaults,
  and optional sky-background rendering.
- `AtmosphericScatteringSettings : PostProcessSettings` owns per-camera
  enablement, debug mode, sample budgets, temporal filtering, and aerial
  perspective limits.
- `DefaultRenderPipeline` runs a separated atmosphere chain before exposure,
  similar to the current volumetric fog chain. The sky background is rendered
  through the normal `Background` pass, while aerial perspective is resolved
  from scene depth into an `RGBA16F` texture that `PostProcess.fs` composites.

The first implementation should use the GPU Gems/O'Neil single-scattering
model with exponential Rayleigh and Mie density, analytic optical-depth scale
approximation, and per-pixel phase evaluation. A later quality tier can add a
precomputed transmittance/inscatter LUT, but that should not block the v1
component.

## Goals

- Render a physically plausible sky for ground, flight, and space views.
- Add aerial perspective over scene geometry using the current depth buffer.
- Support cameras inside and outside the atmosphere without a horizon pop.
- Keep the active OpenGL 4.6 path first, with shader/resource structure that
  does not prevent Vulkan parity later.
- Keep render hot paths allocation-free: fixed-size registries, cached
  resources, no LINQ, no per-frame object creation.
- Preserve the existing `SkyboxComponent` modes while making the new component
  the long-term production atmosphere path.
- Expose practical artist controls without burying the physical baseline.

## Non-Goals

- Multiple overlapping planetary atmospheres in the first pass. Select one
  active atmosphere per camera; support multiple authorable components through
  priority and camera containment rules.
- Full multiple scattering, clouds, ozone spectral absorption, rainbows,
  fogbows, or weather. Leave these as follow-ups.
- Vulkan/DX12 parity in the first implementation. Keep resource names,
  formats, and pass structure backend-friendly.
- Replacing the existing dynamic procedural sky immediately. The new component
  should coexist until migration is deliberate.

## Source Algorithm Takeaways

GPU Gems 2 Chapter 16 implements a real-time version of Nishita-style
atmospheric scattering. The parts that matter for XRENGINE are:

- A view ray contributes scattering only across the segment that lies inside
  the atmosphere. The segment endpoints come from the camera, the shaded point,
  and ray/sphere intersections against the outer atmosphere.
- Atmospheric density falls off exponentially with altitude. This is the key
  difference from simple ground-locked sky approximations.
- Rayleigh scattering and Mie scattering need separate coefficients, scale
  heights, and phase behavior. Rayleigh is wavelength dependent and dominates
  the blue sky. Mie is aerosol-like, more even across RGB, and creates the
  bright forward lobe around the sun.
- Optical depth is the integrated density along a ray. Runtime quality is a
  tradeoff between sample count and cost.
- The phase function should be evaluated per pixel. Doing it per vertex causes
  visible tessellation artifacts near the sun glow.
- Cameras can be inside the atmosphere, at the boundary, or in space. The
  implementation must handle all three without a color discontinuity.

The original shader targeted old hardware and pushed much of the work to
vertex shaders. XRENGINE should adapt the math, not the old draw strategy:
fullscreen/background fragments and separated screen-space passes are a better
fit for the current renderer.

## Current Engine Reality

Relevant existing pieces:

- `SkyboxComponent` lives in
  `XREngine.Runtime.Rendering/Scene/Components/Misc/SkyboxComponent.cs`.
  It already renders through `EDefaultRenderPass.Background` with a fullscreen
  triangle, uses `ExcludeFromGpuIndirect`, and has a `DynamicProcedural` mode
  with approximate Rayleigh/Mie-like shading.
- `VolumetricFogSettings` and `VolumetricFogVolumeComponent` provide the best
  local pattern for world-scoped volumetric data plus camera settings:
  fixed arrays, registry copy, no dynamic render-path allocation.
- `DefaultRenderPipeline.CommandChain.cs` already runs a separated volumetric
  fog chain before `AppendExposureUpdate`, and `PostProcess.fs` composites a
  scatter/transmittance texture.
- `docs/architecture/rendering/default-render-pipeline-notes.md` records the
  invariants for FBO identity checks, texture sizing, and separated fog passes.

Implication: atmosphere should not become another large inline block inside
`PostProcess.fs`. It should follow the separated-pass model.

## Component Model

### `AtmosphericScatteringComponent`

Proposed location:

`XREngine.Runtime.Rendering/Scene/Components/Environment/AtmosphericScatteringComponent.cs`

The component derives from `XRComponent` and optionally implements
`IRenderable` for the sky background draw. Property setters must use
`SetField(...)`.

Core properties:

| Property | Default | Notes |
|---|---:|---|
| `Enabled` | `true` | Registers only while active, enabled, and in a render world. |
| `Priority` | `0` | Higher priority wins when more than one atmosphere can affect a camera. |
| `RenderSky` | `true` | Draws sky background in `EDefaultRenderPass.Background`. |
| `AerialPerspective` | `true` | Allows depth-based scene scattering. Per-camera settings can still disable it. |
| `GroundRadius` | `6371000.0` | World units; do not derive from transform scale. |
| `AtmosphereHeight` | `100000.0` | `OuterRadius = GroundRadius + AtmosphereHeight`. |
| `GroundLevelOffset` | `0.0` | Optional authoring helper for scenes where the component is placed at local ground origin. |
| `SunSource` | `PrimaryDirectionalLight` | Primary light, explicit light, or explicit direction. |
| `SunDirectionOverride` | `(0,1,0)` | Used when no light source is selected. Direction points from atmosphere toward the sun. |
| `SunIntensity` | `20.0` | HDR energy multiplier before exposure. |
| `SunColor` | white | Usually driven by the directional light color. |
| `RayleighScaleHeight` | `8000.0` | Exponential density falloff height. |
| `MieScaleHeight` | `1200.0` | Aerosol falloff height. |
| `RayleighScattering` | Earth-like RGB | Stored as per-channel coefficients. |
| `MieScattering` | neutral RGB | Stored as per-channel coefficients. |
| `MieAnisotropy` | `0.76` | Clamp to `[-0.99, 0.99]`; avoid singular phase values. |
| `ExposureScale` | `1.0` | Artistic multiplier after physical-ish scattering. |
| `GroundAlbedo` | `0.30` | Reserved for future ground bounce / multiple scattering approximation. |

Authoring rule: the component transform gives the atmosphere frame, but radii
are explicit numeric properties. This avoids hidden behavior when artists scale
the node for editor visibility.

### Registry

Mirror the volumetric fog registry shape:

- Store active components per `IRuntimeRenderWorld`.
- `CopyActive(world, cameraPosition, Span<AtmosphericScatteringComponent?>)`
  returns candidates sorted by:
  1. camera inside outer radius,
  2. highest `Priority`,
  3. nearest atmosphere shell.
- Initial shader binding uses only the first candidate.
- Do not allocate while copying or sorting. Use fixed spans and insertion sort
  for the small candidate count.

### Camera Settings

Proposed location:

`XREngine.Runtime.Rendering/Rendering/Camera/AtmosphericScatteringSettings.cs`

Properties:

| Property | Default | Notes |
|---|---:|---|
| `Enabled` | `true` | Master per-camera switch. |
| `RenderSky` | `true` | Lets scene captures or UI cameras skip sky. |
| `AerialPerspective` | `true` | Enables depth-based atmospheric composite. |
| `Quality` | `Balanced` | Maps to sample counts and optional temporal filtering. |
| `ViewSamples` | `8` | Main ray samples for sky and aerial perspective. |
| `OpticalDepthSamples` | `0` | `0` means use GPU Gems analytic scale approximation. Positive values enable reference nested integration. |
| `MaxDistance` | `200000.0` | Clamp for scene geometry aerial perspective. |
| `JitterStrength` | `0.5` | Temporal/noise jitter for screen-space aerial perspective. |
| `TemporalEnabled` | `true` | Stabilizes half-res aerial perspective. |
| `DebugMode` | `Off` | Output masks, optical depth, Rayleigh, Mie, transmittance. |

Keep constructor defaults and pipeline schema defaults aligned, with a unit
test similar to `VolumetricFogSettingsTests`.

## Render Architecture

### Pass Overview

Target ordering in `DefaultRenderPipeline`:

```text
Background pass:
  AtmosphericScatteringComponent sky draw, if active

Internal-resolution scene:
  deferred/forward/transparency
  bloom source capture
  motion blur / DoF / temporal accumulation
  post-temporal forward passes
  post-process resource caching
  anti-aliasing resource caching

Before exposure:
  atmosphere aerial perspective chain
  volumetric fog chain
  exposure update
  PostProcess composite
  FXAA/SMAA/TSR/final output
```

Composite order in `PostProcess.fs`:

```glsl
hdrSceneColor = hdrSceneColor * atmosphere.a + atmosphere.rgb;
hdrSceneColor = hdrSceneColor * volumetricFog.a + volumetricFog.rgb;
```

Atmosphere goes first because it represents long-distance planetary air.
Local volumetric fog should sit in front of that result.

### Sky Background

The component renders a fullscreen triangle in `EDefaultRenderPass.Background`,
following the existing skybox contract:

- shader path: `Build/CommonAssets/Shaders/Scene3D/Atmosphere/AtmosphereSky.fs`
- vertex shader: reuse `Skybox.vs` if its direction contract remains suitable,
  otherwise create `AtmosphereSky.vs`.
- material options:
  - `DepthTest.Enabled = Enabled`
  - `DepthTest.Function = Lequal`
  - `DepthTest.UpdateDepth = false`
  - `RequiredEngineUniforms = Camera`
  - `ExcludeFromGpuIndirect = true`
- sky pixels evaluate the camera ray against the outer atmosphere and integrate
  to the far atmosphere exit point.
- if the camera is outside the atmosphere and the ray misses the atmosphere,
  output black/transparent background contribution.

Do not route this through `SkyboxComponent.DynamicProcedural`; that mode can
stay as a stylized sky/weather component. The new component is the physically
based path.

### Aerial Perspective

Create a separated chain that writes scatter/transmittance for scene geometry:

```text
AtmosphereHalfDepth
  <- downsample DepthView

AtmosphereHalfScatter
  <- raymarch camera-to-depth segment through atmosphere

AtmosphereHalfTemporal
  <- optional reprojection/history clamp

AtmosphereColor
  <- full-resolution bilateral upscale
```

Initial formats:

| Texture | Format | Size | Meaning |
|---|---|---|---|
| `AtmosphereHalfDepth` | `R32F` | half internal | raw depth source for stable reconstruction |
| `AtmosphereHalfScatter` | `RGBA16F` | half internal | rgb = in-scatter, a = transmittance |
| `AtmosphereHalfTemporal` | `RGBA16F` | half internal | filtered scatter/transmittance |
| `AtmosphereHalfHistory` | `RGBA16F` | half internal | previous temporal output |
| `AtmosphereColor` | `RGBA16F` | full internal | final texture sampled by `PostProcess.fs` |

Sky/far-depth pixels output neutral `(0,0,0,1)` to avoid double-applying
atmosphere over the sky shader.

### Scene Surface Scattering

The first implementation applies aerial perspective as a screen-space
post-lighting composite. This handles terrain, opaque objects, transparent
resolved output, and most editor workflows with one pass.

Material-level surface scattering from GPU Gems can be a later feature for
special objects such as moons, far planets, or objects outside the atmosphere.
That should be exposed as a material option only after the sky and screen-space
path are stable.

## Shader Algorithm

Use the same conceptual inputs for sky and aerial perspective:

- `cameraPosition`
- `viewRay`
- `segmentStart`, `segmentEnd`
- `planetCenter`
- `innerRadius`, `outerRadius`
- `sunDirection`
- Rayleigh/Mie coefficients and scale heights

For each ray:

1. Intersect the ray with the outer atmosphere sphere.
2. Clamp the active segment to the camera/depth endpoint when inside the
   atmosphere.
3. Reject rays that hit the planet before the desired sky endpoint, unless the
   pass is shading a surface depth point.
4. March `ViewSamples` points along the segment.
5. At each sample:
   - compute height above ground,
   - evaluate exponential Rayleigh and Mie densities,
   - estimate optical depth toward the camera,
   - estimate optical depth toward the sun,
   - apply transmittance,
   - accumulate Rayleigh and Mie in-scattering separately.
6. Apply Rayleigh and Mie phase functions per pixel.
7. Return `vec4(scatterRgb, transmittance)` in linear HDR space.

Default optical-depth mode should be the GPU Gems analytic scale approximation,
not a nested runtime integral. Keep the nested integral path behind a debug or
reference quality mode so we can validate the approximation visually.

Important shader invariants:

- Clamp Mie anisotropy away from `-1` and `1`.
- Keep wavelength-dependent constants on CPU or in a small uniform struct; do
  not recompute `1 / pow(wavelength, 4)` per sample.
- Handle camera-inside and camera-outside paths explicitly enough that the
  atmosphere boundary has no visible color jump.
- Put phase evaluation in fragment shaders, not vertex shaders.
- Avoid shader branches inside the sample loop where simple masks are enough.

## Pipeline Resources

New constants in `DefaultRenderPipeline`:

```csharp
public const string AtmosphereColorTextureName = "AtmosphereColor";
public const string AtmosphereHalfDepthTextureName = "AtmosphereHalfDepth";
public const string AtmosphereHalfScatterTextureName = "AtmosphereHalfScatter";
public const string AtmosphereHalfTemporalTextureName = "AtmosphereHalfTemporal";
public const string AtmosphereHalfHistoryTextureName = "AtmosphereHalfHistory";
```

FBO names should mirror the volumetric fog naming pattern:

```csharp
AtmosphereHalfDepthQuadFBOName
AtmosphereHalfDepthFBOName
AtmosphereHalfScatterQuadFBOName
AtmosphereHalfScatterFBOName
AtmosphereReprojectQuadFBOName
AtmosphereReprojectFBOName
AtmosphereHistoryFBOName
AtmosphereUpscaleQuadFBOName
AtmosphereUpscaleFBOName
```

Resource rules:

- All FBO cache predicates must verify texture identity, not just size.
- Half-res textures use `GetDesiredFBOSizeHalfInternal()` and the existing
  internal-size resize helpers.
- `AtmosphereColor` uses internal resolution and is sampled by `PostProcess.fs`.
- Disabled/no-atmosphere path writes or binds neutral `(0,0,0,1)` so the final
  composite is stable.
- Reset temporal history on size changes, camera cuts, atmosphere parameter
  changes that shift the field heavily, and active atmosphere switches.

## Editor UX

Expose component properties under a `Atmospheric Scattering` category.

Useful editor actions:

- `Use Primary Directional Light`
- `Pick Sun Light From Scene`
- `Earth At Local Ground Origin`
- `Earth At Component Center`
- `Copy Sun Direction From Selected Light`

Debug views in `AtmosphericScatteringSettings.EDebugMode`:

| Mode | Output |
|---|---|
| `Off` | Normal scatter/transmittance |
| `ActiveMask` | green when an active atmosphere is selected |
| `RaySegment` | normalized marched segment length |
| `Altitude` | sample altitude/outer-shell visualization |
| `OpticalDepth` | total optical depth grayscale |
| `Transmittance` | transmittance rgb |
| `RayleighOnly` | Rayleigh in-scatter |
| `MieOnly` | Mie in-scatter |
| `SunVisibility` | sun path transmittance |
| `CameraInsideOutside` | camera classification |

The ImGui editor should surface the post-process stage through the existing
schema path, not a bespoke panel.

## Interaction With Existing Systems

### `SkyboxComponent`

Keep all current modes. Longer term:

- `DynamicProcedural` remains a stylized day/night/cloud sky.
- `AtmosphericScatteringComponent` becomes the production physical sky.
- Optional follow-up: add a migration helper that creates an atmosphere
  component from dynamic sky sun/moon settings.

### Directional Lights

The atmosphere should read from the selected sun directional light. The light
continues to drive scene direct lighting and shadows. Atmosphere should not
silently mutate light transforms every frame; that behavior belongs to
procedural time-of-day components.

### Global Ambient

Atmosphere can optionally sync ambient light after the core rendering path is
stable. Reuse the existing procedural sky ambient contract:

- derive ambient color/intensity from sun elevation and sky luminance,
- apply a floor for night,
- avoid per-frame allocations,
- only write world settings when values materially change.

### Volumetric Fog

Atmosphere and volumetric fog both produce `rgb + transmittance` textures.
Keep them separate:

- atmosphere: planetary, long-distance, usually one active component;
- volumetric fog: local volumes, shadowed, scene-authored boxes.

Composite atmosphere first, then local fog.

### Auto-Exposure And Bloom

Atmosphere must feed HDR scene color before exposure and final post-process.
Sun disc and Mie aureole can be very bright, so defaults need to be tuned
against current auto-exposure behavior. Bloom should see atmospheric highlights
after the sky/background draw and before final tonemapping.

### XR And Stereo

First pass may be mono-only if necessary, matching the current volumetric fog
staging. The design should still define stereo resource names early:

- sky background is naturally per-eye because it is a background render pass;
- aerial perspective needs `sampler2DArray` stereo variants for depth,
  scatter, temporal, and final atmosphere textures;
- history must be per camera/eye.

Do not claim VR support until OpenVR two-pass and single-pass stereo are both
validated.

## Performance Notes

Default target budgets on a midrange desktop GPU:

| Pass | Budget |
|---|---:|
| Sky background | <= 0.20 ms at 1080p |
| Half-res aerial scatter | <= 0.40 ms at 1080p |
| Temporal + upscale | <= 0.20 ms at 1080p |

Implementation constraints:

- Use fixed arrays/spans for active atmosphere data.
- Precompute coefficient vectors on CPU when properties change.
- Do not allocate in `SetUniforms`.
- Do not use LINQ or captured lambdas in render-path registry code.
- Prefer half-res aerial perspective with temporal stabilization over a full
  resolution heavy march.
- Keep debug/reference nested integration out of default quality.

## Implementation Phases

### Phase 0 - Baseline And Contracts

- Add this design doc to the docs index.
- Capture current `SkyboxComponent.DynamicProcedural` noon/sunset/night
  screenshots in the Unit Testing World for migration comparison.
- Add a small TODO or issue list if implementation starts later.

### Phase 1 - Component And Settings Shell

- Add `AtmosphericScatteringComponent`.
- Add registry with allocation-free active selection.
- Add `AtmosphericScatteringSettings` and schema entries.
- Add unit tests for defaults/schema parity and registry selection.
- Add Unit Testing World toggles for enabling the atmosphere.

### Phase 2 - Sky Background

- Add `AtmosphereSky.fs`.
- Render from the component in the `Background` pass.
- Implement camera-inside/camera-outside sphere segment handling.
- Add debug modes for active mask, altitude, optical depth, Rayleigh, and Mie.
- Validate no horizon pop when crossing the outer atmosphere.

### Phase 3 - Aerial Perspective Chain

- Add half-depth, half-scatter, temporal, history, and upscale textures/FBOs.
- Add `AppendAtmosphericScattering` before `AppendVolumetricFog`.
- Add `AtmosphereAerialPerspective.fs`, `AtmosphereReproject.fs`, and
  `AtmosphereUpscale.fs`.
- Composite `AtmosphereColor` in `PostProcess.fs` before `VolumetricFogColor`.
- Validate disabled/no-component path is neutral.

### Phase 4 - Editor Polish And Ambient Sync

- Add ImGui component controls and debug view exposure through the schema.
- Add optional ambient sync.
- Add helpers for Earth-at-local-ground and primary-light binding.
- Add docs under `docs/developer-guides/components/` once behavior is stable.

### Phase 5 - XR And Quality Expansion

- Add stereo texture-array variants.
- Add optional transmittance LUT quality mode.
- Add material hooks for objects outside the atmosphere.
- Validate Vulkan resource mapping when the Vulkan post-process path catches up.

## Validation Plan

Targeted tests:

- `AtmosphericScatteringSettingsTests.Defaults_MatchPipelineSchemaDefaults`
- registry tests for active, disabled, priority, and camera-inside selection
- shader contract tests for:
  - outer atmosphere ray/sphere intersection,
  - far-depth neutral output,
  - per-pixel phase function,
  - Mie anisotropy clamp,
  - no inline atmosphere block in `PostProcess.fs` except final composite.

Build/run validation:

- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
- Unit Testing World with the atmosphere toggle enabled.
- Ground camera, high-altitude camera, and space camera screenshots.
- Noon, sunset, and below-horizon sun cases.
- Volumetric fog enabled at the same time to verify composite order.
- GPU profiler capture for sky, half-scatter, temporal, and upscale passes.

Acceptance criteria:

- No visible sky discontinuity when camera crosses the outer atmosphere.
- No double atmosphere over sky/far-depth pixels.
- Geometry aerial perspective fades with distance and sun angle.
- Existing skybox modes still render.
- Disabled component or disabled camera setting is visually neutral.
- No new compiler warnings.

## Open Questions

- Should the first implementation expose physical SI units only, or also a
  "scaled planet" preset for small editor scenes?
- Should `SkyboxComponent.DynamicProcedural` eventually delegate its clear-sky
  term to `AtmosphericScatteringComponent`, or remain fully independent?
- Should atmospheric aerial perspective run before or after temporal
  accumulation long term? The initial design runs before exposure and after
  temporal scene accumulation, matching the current volumetric fog model.
- How bright should the sun disc be by default relative to bloom and
  auto-exposure?
- Do scene captures and light probes need atmosphere by default, or should they
  opt in per camera?

## References

- Sean O'Neil, [GPU Gems 2 Chapter 16: Accurate Atmospheric Scattering](https://developer.nvidia.com/gpugems/gpugems2/part-ii-shading-lighting-and-shadows/chapter-16-accurate-atmospheric-scattering).
- XRENGINE, [Procedural Skybox Ambient Lighting](../../../developer-guides/components/procedural-skybox-ambient.md).
- XRENGINE, [Default Render Pipeline Known Issues And Lessons Learned](../../architecture/rendering/default-render-pipeline-notes.md).
- XRENGINE, [Volumetric Fog Production Design](volumetric-fog-production-design.md).
