# Volumetric Fog Production Design

Last updated: 2026-05-26
Status: production design, OpenGL mono implementation present, production polish and XR parity pending

This document replaces the old phase-by-phase volumetric fog refactor TODO. It
keeps the completed separated-pass work as the baseline, folds the remaining
validation tasks into a production target, and defines the lighting extensions
that should come next.

## Goals

Volumetric fog should provide local, authorable participating media that can
render clean god rays, smoke banks, dust pockets, and stylized mist without
turning the main post-process shader into a raymarching sink.

The production version should:

- keep the current separated half-resolution temporal architecture;
- preserve the current default visual output unless an author opts into new
  controls;
- support stereo/XR correctly, especially the tested OpenVR path;
- stay friendly to OpenGL 4.6 today while keeping the resource model portable
  to Vulkan and DX12;
- avoid per-frame CPU allocations and avoid avoidable shader work when a ray
  misses all fog volumes;
- make shadow, density, phase, and history failures diagnosable through debug
  modes and tests.

## Current Baseline

The current implementation already has the core shape we want for v1:

- `VolumetricFogVolumeComponent` registers bounded local-space OBB volumes.
- `VolumetricFogSettings` uploads up to `MaxVolumeCount = 4` active volumes.
- Volume density supports color, density, edge fade, animated procedural noise,
  light contribution, and single-lobe Henyey-Greenstein anisotropy.
- The active render pipeline runs:
  `half-depth downsample -> half-res scatter -> half-res temporal reprojection -> full-res bilateral upscale -> PostProcess composite`.
- `VolumetricFogScatter.fs` outputs `rgb = in-scattered radiance` and
  `a = transmittance`.
- `PostProcess.fs` composites with
  `hdrSceneColor = hdrSceneColor * VolumetricFogColor.a + VolumetricFogColor.rgb`.
- The scatter shader samples the primary directional light and its shadow map
  resources, with low volumetric-specific bias.
- Temporal reprojection keeps its own half-resolution history and has a public
  invalidation hook: `DefaultRenderPipeline.InvalidateVolumetricFogHistory(camera)`.

The production design keeps this baseline. It does not resurrect the old inline
raymarch in `PostProcess.fs`.

## Pipeline Design

The production chain remains screen-space and separated:

1. Render opaque/forward/transparency/late forward scene content and depth.
2. Run atmosphere first when present so local fog composites in front of
   aerial perspective.
3. Downsample raw depth into `VolumetricFogHalfDepth`.
4. Raymarch local volumes into `VolumetricFogHalfScatter`.
5. Reproject and clamp history into `VolumetricFogHalfTemporal`.
6. Bilaterally upscale to full internal resolution as `VolumetricFogColor`.
7. Composite in `PostProcess.fs` before exposure/final output.
8. Commit the volumetric history state for the next frame.

Neutral output is always `(0, 0, 0, 1)`. Disabled settings, no active volumes,
missed volume rays, and invalid history must converge to that value so the
post-process composite is a no-op.

`DefaultRenderPipeline` is the production path. `DefaultRenderPipeline2` has a
matching fog surface today; while both paths exist, volumetric changes should
land symmetrically or explicitly document why V2 is excluded.

## Volume Authoring Model

`VolumetricFogVolumeComponent` remains the authoring unit for v1. Volumes are
local boxes transformed by the component transform. This is easy to edit, debug,
cull, and reason about in the current scene graph.

Required production controls:

- `HalfExtents`: local OBB dimensions.
- `ScatteringColor`: per-volume scattering/albedo tint.
- `Density`: extinction/scattering density scale.
- `NoiseScale`, `NoiseOffset`, `NoiseVelocity`, `NoiseThreshold`,
  `NoiseAmount`: procedural density shaping.
- `EdgeFade`: local-space fade distance at box faces and ray entry/exit.
- `LightContribution`: direct-light multiplier.
- `Anisotropy`: primary HG lobe, defaulting to current behavior.
- `Priority`: selection order when more than four active volumes exist.
- `VolumeEnabled`: explicit author toggle.

Additional production lighting controls should be added in a default-preserving
way:

- `SecondaryAnisotropy`: second HG lobe directionality.
- `SecondaryPhaseWeight`: blend amount for the second lobe. Default `0`.
- `PowderIntensity`: artistic edge/density brightening. Default `0`.
- `PowderDensityScale`: response curve for powder brightening.
- `MultiScatteringApproximation`: cheap ambient fill for dense media.
  Default `0`.

The current `VolumetricFogLightParams` vector can carry
`LightContribution`, `Anisotropy`, `SecondaryAnisotropy`, and
`SecondaryPhaseWeight`. Powder and approximate multi-scattering should use a new
packed vector such as `VolumetricFogOpticalParams` instead of overloading
unrelated noise or color fields.

## Scattering Model

The v1 integrator stays single-scattering with Beer-Lambert transmittance:

```glsl
accumulatedScattering += stepScattering * currentStep * transmittance;
transmittance *= exp(-stepExtinction * currentStep);
```

This gives predictable energy flow, clear shafting, and a simple composite
contract. The renderer should not pretend this is full path-traced
participating media. The design instead adds controlled approximations where
they buy visible quality.

### Single-Lobe HG

The current single-lobe Henyey-Greenstein phase remains the default:

```glsl
float phase = PhaseHenyeyGreenstein(cosTheta, anisotropy);
```

This is cheap, physically recognizable, and already exposed through
`Anisotropy`.

### Dual-Lobe HG

Dual-lobe HG should be the first lighting extension because it is cheap and
fits the existing shader:

```glsl
float primary = PhaseHenyeyGreenstein(cosTheta, primaryAnisotropy);
float secondary = PhaseHenyeyGreenstein(cosTheta, secondaryAnisotropy);
float phase = mix(primary, secondary, saturate(secondaryPhaseWeight));
```

Recommended authoring ranges:

- primary lobe: `0.2` to `0.85` for forward-scattering shafts;
- secondary lobe: `-0.3` to `0.2` for soft side/back fill;
- secondary weight: `0.0` to `0.4` for normal fog, higher only for stylized
  smoke or clouds.

Defaults must preserve current output: `SecondaryPhaseWeight = 0`.

### Powder Brightening

Powder brightening is an artistic control, not a physical phase function. It
should be opt-in and meant for dense puffs, cloudlike volumes, smoke, and magic
mist. It should not be part of the default world fog look.

The shader should derive a local powder signal from density and the existing
edge/ray fade terms, then boost direct lighting before extinction integration:

```glsl
float localPowder = 1.0f - exp(-density * PowderDensityScale);
float boundaryExposure = 1.0f - saturate(edgeMask * rayEdgeMask);
float powder = localPowder * boundaryExposure * PowderIntensity;
lighting *= 1.0f + powder;
```

This is intentionally simple. It gives artists a way to recover bright,
powdery edges without adding light-space raymarching or a froxel cache.

### Multiple Scattering Approximation

Real multiple scattering should wait for a froxel/3D light integration path. For
the current screen-space path, expose only a cheap approximation:

- derive a low-frequency fill from `GlobalAmbient`, the volume color, and
  accumulated optical depth;
- make it opt-in with `MultiScatteringApproximation`;
- clamp it so shadowed fog never turns into flat glowing fog;
- keep it independent from shadow-map visibility so direct-light shadows still
  read clearly.

This is a taste control, not a physically exact solution.

## Shadows

Current fog receives geometry shadows from the primary directional light. That
is the right v1 production scope.

Production requirements:

- keep volumetric shadow bias separate from surface receiver bias;
- prefer a simple shadow sample path when temporal reprojection and bilateral
  upscale hide aliasing well enough;
- profile shadow sampling inside the scatter loop before adding more samples;
- migrate to atlas/record-based directional shadow metadata when the broader
  shadow-resource migration reaches fog;
- keep legacy directional resources until the atlas bridge is validated.

Self-shadowing, where fog blocks light travelling through itself before that
light reaches a fog sample, is not a v1 requirement. It is valuable, but it
requires either extra light-space raymarching or a froxelized light transport
cache. The screen-space path should not grow that cost by default.

## Global Illumination Integration

As written, volumetric fog is safe with every GI mode because it does not depend
on any specific GI resource. That is not enough for production quality: fog
should also receive sensible indirect light from whichever GI mode the user
selects.

The production rule is:

> Volumetric fog consumes a renderer-owned indirect-light contract, not a
> specific GI implementation.

Do not make `VolumetricFogScatter.fs` directly know about light probes, ReSTIR,
surfel buffers, radiance cascades, light volumes, voxel cone tracing, DDGI, or
LPV. Each GI path should either publish a common low-frequency irradiance input
for fog or explicitly report that it has no volumetric receiver support yet.

The common contract should answer this question:

```glsl
vec3 SampleVolumetricIndirectLighting(vec3 samplePosWS, vec3 viewToCamera, float density);
```

The first implementation can be a uniform fallback:

- `None`: black or author-controlled ambient floor.
- `LightProbesAndIbl`: interpolated probe/IBL diffuse irradiance, preferably
  the low-order SH/L0 term rather than specular prefilter data.
- unsupported dynamic GI modes: current `GlobalAmbient` fallback with an honest
  debug flag.

Higher-quality providers can then slot in without changing the fog integrator:

- `LightVolumes`: sample the selected light-volume irradiance at `samplePosWS`.
- `RadianceCascades`: sample the cascaded radiance volume at `samplePosWS`.
- `VoxelConeTracing`: sample diffuse voxel-cone GI or a low-frequency resolved
  voxel irradiance volume.
- `SurfelGI`: either sample a surfel-grid irradiance structure if exposed, or
  use fallback ambient until surfel GI publishes a stable volumetric receiver.
- `Restir`: use fallback ambient for v1; true volumetric ReSTIR is future work.
- future `DDGI`: sample probe-volume irradiance at `samplePosWS`.
- future `LPV`: sample propagated LPV irradiance at `samplePosWS`.

The fog shader should combine indirect lighting separately from primary direct
lighting:

```glsl
vec3 directLighting = EvaluatePrimaryDirectionalLighting(...);
vec3 indirectLighting = SampleVolumetricIndirectLighting(samplePosWS, viewToCamera, density);
vec3 lighting = directLighting + indirectLighting * IndirectContribution;
```

This keeps GI selection orthogonal to volumetric controls. Dual-lobe phase,
powder brightening, shadow sampling, and approximate multi-scattering remain
fog features; the selected GI path only supplies low-frequency incoming
radiance.

Ambient occlusion modes are not GI providers. Surface AO should affect the
surface color before fog composite, but screen-space AO should not darken fog
samples directly unless a later volumetric-occlusion feature explicitly opts in.

Validation should include a GI matrix:

- all current `EGlobalIlluminationMode` values render without missing-resource
  warnings or shader-link failures;
- modes with no volumetric provider fall back visibly and log/debug honestly;
- modes with spatial irradiance providers change fog lighting when their GI
  data changes;
- `None` remains neutral except for explicit author ambient settings.

## XR And Stereo

Mono-only fog is not production complete. The final v1 path must support stereo
by running the entire chain per eye:

- per-eye half-depth, half-scatter, half-temporal, history, and upscaled color;
- per-eye camera matrices and history invalidation;
- no reuse of one eye's history or fog color for the other eye;
- validation on the OpenVR path before calling the feature production-ready.

The authoring model remains shared across eyes; only view-dependent resources
and history are eye-specific.

## Resource And Lifetime Rules

All fog textures and FBOs should continue to use the same sizing helpers and
recreation predicates as the current implementation:

- half-resolution resources use `GetDesiredFBOSizeHalfInternal()`;
- full-resolution color uses internal-resolution sizing;
- transient quad FBOs use transient lifetime;
- persistent output/history FBOs validate attachment identity as well as size;
- history resets on first frame, resize, camera cut, AA resource invalidation,
  and render pipeline rebuild.

The texture contract is stable:

- `VolumetricFogHalfDepth`: raw half-resolution depth.
- `VolumetricFogHalfScatter`: current raw scatter/transmittance.
- `VolumetricFogHalfTemporal`: reprojected and clamped half-resolution result.
- `VolumetricFogHalfHistory`: previous half-resolution temporal result.
- `VolumetricFogColor`: full-resolution composited input for `PostProcess.fs`.

## CPU-Side Selection And Upload

The current registry and upload path are acceptable for v1, but production
polish should make selection cheaper and more predictable:

- cull fog volumes against the active camera frustum before upload;
- keep the fixed maximum of four uploaded volumes until a froxel path exists;
- when more than four volumes are visible, choose the highest-priority visible
  volumes deterministically;
- avoid per-frame allocations in registry copy, priority sorting, and uniform
  packing;
- keep all `XRBase` mutations on `SetField(...)`.

If richer scenes need many overlapping volumes, that is a signal to build the
future froxel path rather than inflate the screen-space uniform arrays.

## Debugging And Validation

The existing debug modes are valuable and should remain part of the production
contract. At minimum, keep diagnostics for:

- volume hit/miss;
- average shadow factor;
- optical depth;
- phase function;
- raw scatter;
- density, noise, and edge terms;
- shadow path state;
- surface shadow factor and cascade selection;
- reconstructed world position and cascade matrix sanity;
- march distance.

Additional debug modes should be added with each new lighting feature:

- dual-lobe phase blend;
- powder term;
- approximate multi-scattering contribution.

Validation should include:

- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`;
- unit tests that keep `VolumetricFogSettings` defaults aligned with pipeline
  schema defaults;
- a unit-testing-world smoke test with `InitializeVolumetricFog = true`;
- visual captures for disabled, single-volume, multi-volume, shadowed,
  no-shadow, camera-cut, and resize cases;
- OpenVR stereo validation;
- GPU profiler captures before and after any shadow or lighting extension.

## Production Work Plan

These tasks replace the old phase TODO. They are ordered by production risk and
visible payoff.

### 1. Production Polish

- Add per-camera/frustum culling before uploading active volumes.
- Re-audit priority selection when more than four volumes are active.
- Run a hot-path allocation audit over the fog registry, upload, and pass
  submission paths.
- Validate MSAA composite behavior.
- Resweep `StepSize`, `JitterStrength`, `MaxDistance`, and demo volume noise
  once temporal filtering is stable.
- Add the default/schema unit test and unit-testing-world smoke test.
- Update stable component and render-pipeline docs.

### 2. XR Parity

- Convert the fog resource chain to per-eye resources in stereo.
- Keep history and previous matrices per eye.
- Validate OpenVR eye disparity, camera cuts, and resize behavior.
- Only then mark local volumetric fog production-ready for XR.

### 3. Cheap Lighting Extensions

- Add dual-lobe HG with default-preserving fields and shader packing.
- Add debug visualization for primary/secondary phase blend.
- Add optional powder brightening for cloudlike volumes.
- Add optional approximate multiple-scattering fill.
- Validate that all new controls default to current output.

### 4. GI Receiver Integration

- Add a common volumetric indirect-light contract owned by the render pipeline.
- Start with neutral/ambient fallback for all modes.
- Wire `LightProbesAndIbl`, `LightVolumes`, and `RadianceCascades` first,
  because they naturally expose low-frequency spatial irradiance.
- Explicitly mark `SurfelGI`, `VoxelConeTracing`, and `Restir` as fallback-only
  until each path publishes a stable volumetric irradiance provider.
- Add debug visualization for the indirect lighting term.
- Add validation scenes that switch every current `EGlobalIlluminationMode`
  while fog is enabled.

### 5. Shadow Integration

- Profile shadow sampling cost inside `EvaluatePrimaryDirectionalShadow`.
- Add a volumetric-simple shadow mode if filtered shadow taps dominate the
  scatter pass.
- Migrate fog to the shadow atlas/record bridge once directional cascade
  metadata is available.
- Keep legacy shadow bindings until the atlas path has visual parity.

### 6. Documentation And Closeout

- Move durable user-facing behavior into `docs/features` or
  `docs/architecture` once production validation is complete.
- Keep this design as the rationale and implementation map.
- Retire the old TODO stub once all external links point here.

## Future Work

The following items are valuable, but they should not be bolted onto the current
screen-space path as incremental complexity:

- Froxel/3D volume texture integration with pre-integrated light transport.
- True volumetric self-shadowing through light-space volume integration.
- Real multiple scattering, either approximated in froxels or via a dedicated
  low-frequency scattering cache.
- Per-volume per-light contribution beyond the primary directional light.
- Local light injection for point and spot lights.
- True volumetric ReSTIR / path-traced participating-media GI.
- Colored translucent shadows.
- Participating-media interaction with transparent surfaces and OIT.
- Cloud-specific volume primitives that share the scattering model but use
  different density generation and LOD.
- Vulkan/DX12 parity for the fog pass graph after the OpenGL v1 path is stable.

## Non-Goals

- Do not reintroduce volumetric raymarching directly into `PostProcess.fs`.
- Do not add true self-shadowing to the screen-space path.
- Do not raise `MaxVolumeCount` as a substitute for a froxel architecture.
- Do not make powder brightening or multiple-scattering approximation visible by
  default.
- Do not remove legacy directional shadow resources until the atlas bridge has a
  tested fog receiver.

## References

- XRENGINE, [Default Render Pipeline Known Issues And Lessons Learned](../../../architecture/rendering/default-render-pipeline-notes.md).
- XRENGINE, [Atmospheric Scattering Component Design](atmospheric-scattering-component-design.md).
- XRENGINE, [Shadow Resource Migration Audit](shadows/shadow-resource-migration-audit.md).
- Historical phase ledger: [Volumetric Fog Refactor TODO](../../todo/rendering/volumetric-fog-refactor-todo.md).
