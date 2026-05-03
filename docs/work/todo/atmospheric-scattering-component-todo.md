# Atmospheric Scattering Component TODO

Last Updated: 2026-05-03
Current Status: implementation not started; generated from the [Atmospheric Scattering Component Design](../design/atmospheric-scattering-component-design.md).
Scope: OpenGL 4.6 first implementation of `AtmosphericScatteringComponent`, `AtmosphericScatteringSettings`, sky-background rendering, screen-space aerial perspective, editor controls, tests, and docs.

## Phase 0 - Branch, Baseline, And Contract Decisions

Outcome: implementation starts on an isolated branch with the key contracts
settled before shader and pipeline code grows around them.

- [ ] Create a dedicated branch for this work, for example
  `feature/atmospheric-scattering-component`.
- [ ] Re-read
  [atmospheric-scattering-component-design.md](../design/atmospheric-scattering-component-design.md)
  and keep this TODO in sync with any contract changes.
- [ ] Decide first-pass unit policy:
  - physical SI-like defaults with large radii,
  - optional scene-scale multiplier,
  - or explicit "small world" preset.
- [ ] Decide whether the first implementation wires schema entries in both
  `DefaultRenderPipeline` and `DefaultRenderPipeline2`, or only the active
  `DefaultRenderPipeline`.
- [ ] Confirm pass order relative to volumetric fog:
  `AtmosphereColor` composite first, then `VolumetricFogColor`.
- [ ] Capture baseline screenshots of the current
  `SkyboxComponent.DynamicProcedural` sky at noon, sunset, and night in the
  Unit Testing World.
- [ ] Capture a baseline scene with volumetric fog enabled so final atmosphere
  plus local fog ordering can be compared.

Acceptance criteria:

- [ ] Branch exists and no unrelated work is included in the atmosphere branch.
- [ ] Unit policy and pipeline-schema parity decision are documented in this
  TODO or the design doc.
- [ ] Baseline captures are available or explicitly marked as deferred owner
  validation.

---

## Non-Goals

- Do not replace `SkyboxComponent.DynamicProcedural` in the first pass.
- Do not implement full multiple scattering, clouds, ozone absorption,
  rainbows, fogbows, or weather.
- Do not ship Vulkan/DX12 parity as part of the first implementation.
- Do not support multiple simultaneous blended planetary atmospheres. Select
  one active atmosphere per camera.
- Do not add a transmittance/inscatter LUT before the direct single-scattering
  path is stable.

---

## Phase 1 - Component And Registry Shell

Outcome: scenes can author an active atmosphere component, and render code can
query the selected atmosphere without per-frame allocations.

Primary files:

- `XREngine.Runtime.Rendering/Scene/Components/Environment/AtmosphericScatteringComponent.cs`
- `XREngine.UnitTests/Rendering/AtmosphericScatteringComponentTests.cs`

Tasks:

- [ ] Add `AtmosphericScatteringComponent : XRComponent`.
- [ ] Put all property mutation through `SetField(...)`.
- [ ] Add core authoring properties:
  - `Enabled`
  - `Priority`
  - `RenderSky`
  - `AerialPerspective`
  - `GroundRadius`
  - `AtmosphereHeight`
  - `GroundLevelOffset`
  - `SunSource`
  - `SunDirectionOverride`
  - `SunIntensity`
  - `SunColor`
  - `RayleighScaleHeight`
  - `MieScaleHeight`
  - `RayleighScattering`
  - `MieScattering`
  - `MieAnisotropy`
  - `ExposureScale`
  - `GroundAlbedo`
- [ ] Clamp radii, scale heights, intensities, and Mie anisotropy to valid
  ranges.
- [ ] Add precomputed coefficient/state fields that update only when relevant
  properties change.
- [ ] Add a per-`IRuntimeRenderWorld` registry mirroring the volumetric fog
  registry shape.
- [ ] Implement allocation-free active selection using fixed spans and
  insertion sorting by camera containment, priority, and shell distance.
- [ ] Register/unregister on activation, deactivation, world changes, and
  `Enabled` changes.
- [ ] Add tests for:
  - disabled components do not select,
  - inactive components do not select,
  - higher priority wins,
  - camera-inside atmosphere wins over outside candidate,
  - registry copy does not exceed the fixed destination size.

Acceptance criteria:

- [ ] Component compiles and appears in component discovery.
- [ ] Registry tests pass.
- [ ] No render-thread or per-frame heap allocation is introduced in registry
  selection.

---

## Phase 2 - Camera Settings And Pipeline Schema

Outcome: atmosphere has per-camera controls exposed through the existing
post-process settings and schema path.

Primary files:

- `XREngine.Runtime.Rendering/Rendering/Camera/AtmosphericScatteringSettings.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.PostProcessing.cs`
- `XREngine.Runtime.Rendering/Rendering/PostProcessing/RenderPipelinePostProcessSchemaBuilder.cs`
- `XREngine.UnitTests/Rendering/AtmosphericScatteringSettingsTests.cs`

Tasks:

- [ ] Add `AtmosphericScatteringSettings : PostProcessSettings`.
- [ ] Add `EQualityMode` with at least `Low`, `Balanced`, `High`, and
  `Reference`.
- [ ] Add `EDebugMode` values:
  - `Off`
  - `ActiveMask`
  - `RaySegment`
  - `Altitude`
  - `OpticalDepth`
  - `Transmittance`
  - `RayleighOnly`
  - `MieOnly`
  - `SunVisibility`
  - `CameraInsideOutside`
- [ ] Add settings:
  - `Enabled`
  - `RenderSky`
  - `AerialPerspective`
  - `Quality`
  - `ViewSamples`
  - `OpticalDepthSamples`
  - `MaxDistance`
  - `JitterStrength`
  - `TemporalEnabled`
  - `DebugMode`
- [ ] Implement `SetUniforms(XRRenderProgram program)` with inert shader state
  when disabled or no active atmosphere is selected.
- [ ] Upload atmosphere data using cached arrays/struct fields; do not allocate
  inside `SetUniforms`.
- [ ] Add pipeline schema stage `atmosphericScattering`.
- [ ] Add visibility conditions so detailed controls only show when enabled.
- [ ] Keep constructor defaults and schema defaults aligned.
- [ ] Add tests for defaults/schema parity and inert disabled uniforms.

Acceptance criteria:

- [ ] New settings stage appears in the render-pipeline post-process schema.
- [ ] Defaults/schema parity test passes.
- [ ] Disabled/no-active-atmosphere path uploads neutral values.

---

## Phase 3 - Shared Shader Math

Outcome: GLSL helpers exist for atmospheric ray segments, density,
transmittance, and phase functions before any production pass depends on them.

Primary files:

- `Build/CommonAssets/Shaders/Scene3D/Atmosphere/AtmosphereCommon.glsl`
- `XREngine.UnitTests/Rendering/AtmosphericScatteringShaderContractTests.cs`

Tasks:

- [ ] Add `AtmosphereCommon.glsl`.
- [ ] Implement ray/sphere intersection for inner planet and outer atmosphere.
- [ ] Implement camera-inside, camera-outside, and atmosphere-miss segment
  classification helpers.
- [ ] Implement exponential Rayleigh and Mie density functions.
- [ ] Implement Rayleigh phase function.
- [ ] Implement Henyey-Greenstein Mie phase function with anisotropy clamped
  away from `-1` and `1`.
- [ ] Implement analytic optical-depth scale approximation from the GPU Gems
  model.
- [ ] Add optional nested optical-depth loop for `Reference` quality/debug only.
- [ ] Keep wavelength-dependent coefficients uniform-driven; do not recompute
  `1 / pow(wavelength, 4)` inside sample loops.
- [ ] Add shader contract tests that assert the common file contains:
  - outer-atmosphere ray/sphere intersection,
  - inner-planet occlusion handling,
  - per-pixel phase functions,
  - anisotropy clamp,
  - analytic scale approximation,
  - reference nested integration path.

Acceptance criteria:

- [ ] Shared shader file is included by later atmosphere shaders.
- [ ] Contract tests cover the invariants most likely to regress.
- [ ] No atmosphere math is added inline to `PostProcess.fs`.

---

## Phase 4 - Sky Background Rendering

Outcome: an active atmosphere component can render a physical sky in the
`Background` pass without breaking existing skybox modes.

Primary files:

- `XREngine.Runtime.Rendering/Scene/Components/Environment/AtmosphericScatteringComponent.cs`
- `Build/CommonAssets/Shaders/Scene3D/Atmosphere/AtmosphereSky.fs`
- `Build/CommonAssets/Shaders/Scene3D/Atmosphere/AtmosphereSky.vs` if
  `Skybox.vs` cannot be reused cleanly
- `XREngine.UnitTests/Rendering/AtmosphericScatteringSkyTests.cs`

Tasks:

- [ ] Make `AtmosphericScatteringComponent` implement `IRenderable` for the
  sky draw, or add an equivalent render command owner.
- [ ] Use `EDefaultRenderPass.Background`.
- [ ] Use a fullscreen triangle and `ExcludeFromGpuIndirect = true`, following
  the existing `SkyboxComponent` contract.
- [ ] Set render options:
  - depth test enabled,
  - depth writes disabled,
  - `Lequal` depth compare,
  - camera uniforms required.
- [ ] Add `AtmosphereSky.fs`.
- [ ] Reuse `Skybox.vs` if its direction reconstruction remains correct;
  otherwise add `AtmosphereSky.vs`.
- [ ] Implement sky-ray segment selection for:
  - camera inside atmosphere,
  - camera outside atmosphere looking through the atmosphere,
  - camera outside atmosphere missing the atmosphere.
- [ ] Implement Rayleigh and Mie sky integration using `ViewSamples`.
- [ ] Evaluate phase functions per pixel.
- [ ] Add debug outputs for active mask, altitude, optical depth, Rayleigh-only,
  Mie-only, transmittance, and camera classification.
- [ ] Add tests that `SkyboxComponent` fallback source contracts are not broken
  by the new atmosphere path.
- [ ] Validate existing `SkyboxComponent` texture, gradient, solid-color, and
  dynamic-procedural modes still render.

Acceptance criteria:

- [ ] Atmosphere sky renders when `RenderSky` is enabled.
- [ ] Existing skybox modes still render.
- [ ] No visible discontinuity occurs when camera crosses the outer atmosphere.
- [ ] Debug views visibly distinguish active, miss, inside, and outside cases.

---

## Phase 5 - Aerial Perspective Resources

Outcome: pipeline textures and FBOs exist for the separated aerial perspective
chain, with correct resize and identity-recreate behavior.

Primary files:

- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.Textures.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.FBOs.cs`
- `XREngine.UnitTests/Rendering/AtmosphericScatteringPipelineResourceTests.cs`

Tasks:

- [ ] Add texture-name constants:
  - `AtmosphereColorTextureName`
  - `AtmosphereHalfDepthTextureName`
  - `AtmosphereHalfScatterTextureName`
  - `AtmosphereHalfTemporalTextureName`
  - `AtmosphereHalfHistoryTextureName`
- [ ] Add FBO-name constants:
  - `AtmosphereHalfDepthQuadFBOName`
  - `AtmosphereHalfDepthFBOName`
  - `AtmosphereHalfScatterQuadFBOName`
  - `AtmosphereHalfScatterFBOName`
  - `AtmosphereReprojectQuadFBOName`
  - `AtmosphereReprojectFBOName`
  - `AtmosphereHistoryFBOName`
  - `AtmosphereUpscaleQuadFBOName`
  - `AtmosphereUpscaleFBOName`
- [ ] Create `AtmosphereHalfDepth` as half-internal `R32F`.
- [ ] Create `AtmosphereHalfScatter`, `AtmosphereHalfTemporal`, and
  `AtmosphereHalfHistory` as half-internal `RGBA16F`.
- [ ] Create `AtmosphereColor` as internal-resolution `RGBA16F`.
- [ ] Add texture resize predicates using existing internal and half-internal
  helpers.
- [ ] Add FBO creation methods for all aerial perspective stages.
- [ ] Add FBO recreation predicates that verify attachment texture identity,
  not only size.
- [ ] Mark transient quad/source FBOs with `RenderResourceLifetime.Transient`
  where appropriate.
- [ ] Add tests or contract assertions for resource names, formats, and
  identity predicates.

Acceptance criteria:

- [ ] All resources are cached and resized through the pipeline resource system.
- [ ] FBOs recreate after texture invalidation or output format changes.
- [ ] Disabled/no-atmosphere frames can bind or produce neutral
  `AtmosphereColor`.

---

## Phase 6 - Aerial Perspective Shaders

Outcome: screen-space scene geometry receives atmospheric scattering and
transmittance from the camera-to-depth segment.

Primary shader files:

- `Build/CommonAssets/Shaders/Scene3D/Atmosphere/AtmosphereHalfDepthDownsample.fs`
- `Build/CommonAssets/Shaders/Scene3D/Atmosphere/AtmosphereAerialPerspective.fs`
- `Build/CommonAssets/Shaders/Scene3D/Atmosphere/AtmosphereReproject.fs`
- `Build/CommonAssets/Shaders/Scene3D/Atmosphere/AtmosphereUpscale.fs`

Tasks:

- [ ] Add `AtmosphereHalfDepthDownsample.fs` to copy raw depth to
  `AtmosphereHalfDepth`.
- [ ] Add `AtmosphereAerialPerspective.fs`.
- [ ] Reconstruct world position from raw depth using existing camera uniform
  conventions.
- [ ] Output neutral `(0,0,0,1)` for far-depth/sky pixels.
- [ ] March only the camera-to-surface segment that intersects the selected
  atmosphere.
- [ ] Clamp aerial perspective by `MaxDistance`.
- [ ] Add jitter controlled by `JitterStrength`.
- [ ] Add `AtmosphereReproject.fs` with per-camera history readiness, camera
  cut rejection, and current-frame neutral passthrough.
- [ ] Add `AtmosphereUpscale.fs` with full-resolution depth-aware bilateral
  upscale.
- [ ] Add debug modes matching `AtmosphericScatteringSettings.EDebugMode`.
- [ ] Add shader contract tests for far-depth neutral output, depth
  reconstruction hooks, temporal neutral passthrough, and upscale sampling of
  temporal output rather than raw scatter.

Acceptance criteria:

- [ ] Opaque/deferred and forward geometry fade with distance through
  atmospheric air.
- [ ] Sky pixels are not double-atmosphered.
- [ ] Half-res scatter upscales without obvious foreground halos.
- [ ] Temporal history does not persist after current-frame neutral output.

---

## Phase 7 - Pipeline Command Chain And Composite

Outcome: the aerial perspective chain runs before exposure and composites into
the HDR scene before local volumetric fog.

Primary files:

- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.CommandChain.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_AtmosphereHistoryPass.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/RenderPipelineAntiAliasingResources.cs`
- `Build/CommonAssets/Shaders/Scene3D/PostProcess.fs`
- `docs/architecture/rendering/default-render-pipeline-notes.md`

Tasks:

- [ ] Add `AppendAtmosphericScattering(ViewportRenderCommandContainer c)`.
- [ ] Run it before `AppendVolumetricFog(c)` and before `AppendExposureUpdate(c)`.
- [ ] Add `VPRC_AtmosphereHistoryPass` mirroring the volumetric fog history
  begin/commit pattern.
- [ ] Reset atmosphere history on:
  - first frame,
  - texture size change,
  - anti-aliasing resource invalidation,
  - camera cut,
  - active atmosphere switch,
  - large atmosphere parameter changes.
- [ ] Bind `AtmosphereColor` to `PostProcess.fs`.
- [ ] Composite atmosphere before `VolumetricFogColor`:
  `hdrSceneColor = hdrSceneColor * atmosphere.a + atmosphere.rgb`.
- [ ] Keep disabled/no-atmosphere composite neutral.
- [ ] Add `DefaultRenderPipeline` notes documenting atmosphere pass ordering,
  neutral output, and history-reset invariants.
- [ ] If schema/resource changes are mirrored in `DefaultRenderPipeline2`,
  keep the V2 path consistent or explicitly document why it is skipped.

Acceptance criteria:

- [ ] Pipeline produces a valid `AtmosphereColor` texture each frame.
- [ ] Volumetric fog still composites after atmosphere.
- [ ] Exposure and bloom see the expected atmospheric HDR contribution.
- [ ] No new FBO completeness, texture identity, or sampler binding warnings.

---

## Phase 8 - Unit Testing World, Editor UX, And Authoring Helpers

Outcome: developers can enable and inspect the feature from normal editor and
unit-testing workflows.

Primary files:

- `XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Toggles.cs`
- `XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Models.cs`
- `XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Pawns.cs`
- `XREngine.Runtime.Bootstrap/UnitTestingWorldSettings.cs`
- `Tools/Generate-UnitTestingWorldSettings.ps1`
- `XREngine.Editor/ComponentEditors/AtmosphericScatteringComponentEditor.cs`

Tasks:

- [ ] Add Unit Testing World toggle `InitializeAtmosphericScattering`.
- [ ] Add generated JSONC/schema support for new toggle fields.
- [ ] Add bootstrap/unit-test-world creation of a default atmosphere component.
- [ ] Add camera post-process setup for `AtmosphericScatteringSettings`.
- [ ] Add demo presets:
  - ground camera,
  - high-altitude camera,
  - space camera,
  - sunset sun angle.
- [ ] Add ImGui component editor controls for physical parameters and sun
  source selection.
- [ ] Add authoring helper buttons:
  - `Use Primary Directional Light`,
  - `Pick Sun Light From Scene`,
  - `Earth At Local Ground Origin`,
  - `Earth At Component Center`,
  - `Copy Sun Direction From Selected Light`.
- [ ] Expose debug mode through the existing post-process schema path.
- [ ] Regenerate Unit Testing World settings and schema after changing toggle
  types.

Acceptance criteria:

- [ ] Unit Testing World can spawn atmosphere with a JSONC toggle.
- [ ] Editor exposes component controls without requiring native UI work.
- [ ] Debug views can be selected from the render-pipeline/post-process UI.
- [ ] Generated settings and schema are updated if toggle types changed.

---

## Phase 9 - Validation, Tuning, And Regression Tests

Outcome: visual behavior, performance, and old skybox paths are validated
before the feature is considered stable.

Tasks:

- [ ] Run targeted unit tests:
  - `AtmosphericScatteringComponentTests`
  - `AtmosphericScatteringSettingsTests`
  - `AtmosphericScatteringShaderContractTests`
  - any touched skybox/volumetric fog/post-process tests
- [ ] Build the editor:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
- [ ] Run Unit Testing World with atmosphere enabled.
- [ ] Validate ground, high-altitude, and space cameras.
- [ ] Validate noon, sunset, and below-horizon sun cases.
- [ ] Validate atmosphere plus volumetric fog composite order.
- [ ] Validate existing skybox modes:
  - texture,
  - gradient,
  - solid color,
  - dynamic procedural.
- [ ] Capture GPU profiler timings for:
  - sky background,
  - half-depth,
  - half-scatter,
  - temporal reprojection,
  - upscale,
  - post-process composite.
- [ ] Tune defaults so:
  - sky background is 0.20 ms or less at 1080p where practical,
  - half-res aerial scatter is 0.40 ms or less at 1080p where practical,
  - temporal plus upscale is 0.20 ms or less at 1080p where practical.
- [ ] Check logs for new rendering, OpenGL, shader, FBO, and texture warnings.
- [ ] Fix easy nearby validation failures; report larger unrelated failures.

Acceptance criteria:

- [ ] Targeted tests pass.
- [ ] Editor build succeeds.
- [ ] Atmosphere has no visible outer-boundary pop.
- [ ] Far-depth/sky pixels are not double-composited.
- [ ] Disabled component and disabled camera setting are visually neutral.
- [ ] No new warnings are introduced.

---

## Phase 10 - Stable Docs And Handoff

Outcome: user-facing behavior and implementation invariants are documented.

Tasks:

- [ ] Add stable feature doc:
  `docs/features/components/atmospheric-scattering.md`.
- [ ] Document component properties, camera settings, debug views, and Unit
  Testing World toggles.
- [ ] Link the feature doc from `docs/README.md`.
- [ ] Update `docs/work/README.md` status when implementation moves from
  active TODO to stable doc/testing.
- [ ] Update `docs/architecture/rendering/default-render-pipeline-notes.md`
  with atmosphere-specific resource and composite invariants.
- [ ] Add screenshots or a short validation note if baseline captures are kept
  in docs.
- [ ] Record known limitations:
  - OpenGL first,
  - mono aerial perspective until stereo variants land if stereo is deferred,
  - single active atmosphere per camera,
  - no multiple scattering/LUT in the initial path.

Acceptance criteria:

- [ ] User-visible settings and workflows are documented.
- [ ] Rendering invariants are documented near other pipeline invariants.
- [ ] Work-doc index points readers at the canonical stable doc once ready.

---

## Phase 11 - Follow-Up Quality And Platform Work

Outcome: advanced work is tracked separately and does not block the first
usable feature.

Tasks:

- [ ] Add stereo texture-array variants for aerial perspective resources.
- [ ] Validate OpenVR two-pass stereo.
- [ ] Validate OpenVR/OpenXR single-pass stereo when the pipeline path is ready.
- [ ] Add optional transmittance LUT quality mode.
- [ ] Add optional material hooks for objects outside the atmosphere.
- [ ] Evaluate ambient-light sync from sky luminance and sun elevation.
- [ ] Evaluate physically scaled editor gizmos for large-radius atmospheres.
- [ ] Evaluate Vulkan resource and shader portability once the Vulkan
  post-process path catches up.

Acceptance criteria:

- [ ] Deferred follow-ups are either completed or split into focused TODO docs.
- [ ] The first OpenGL mono implementation remains stable while advanced paths
  are developed.

---

## Cross-Phase Guardrails

- [ ] Keep `XRBase`-derived property setters using `SetField(...)`.
- [ ] Avoid per-frame heap allocations in registry selection, settings uniform
  upload, render commands, and shader-binding paths.
- [ ] Keep `PostProcess.fs` responsible only for final atmosphere composite,
  not the atmosphere raymarch.
- [ ] Keep sky phase evaluation per pixel.
- [ ] Keep no-component/disabled shader output neutral as `(0,0,0,1)`.
- [ ] Keep FBO recreation predicates checking attachment texture identity.
- [ ] Update docs in the same change when user-visible settings, toggles,
  debug modes, or workflows change.

---

## Final Integration

- [ ] Merge the dedicated atmosphere branch back into `main` after all completed
  phases are validated and documented.
