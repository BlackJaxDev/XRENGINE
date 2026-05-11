# Atmospheric Scattering Component TODO

Last Updated: 2026-05-09
Current Status: implemented for the OpenGL mono pipeline; visual/profiler validation and stereo/platform parity remain follow-up work.
Stable doc: [Atmospheric Scattering Component](../../../features/components/atmospheric-scattering.md)
Design doc: [Atmospheric Scattering Component Design](../../design/atmospheric-scattering-component-design.md)

## Completed Implementation

- [x] Created dedicated branch `feature/atmospheric-scattering-component`.
- [x] Chose SI-like physical defaults with the component transform origin treated as local ground level.
- [x] Mirrored schema, resources, FBOs, command chain, and post-process bindings in both `DefaultRenderPipeline` and `DefaultRenderPipeline2`.
- [x] Confirmed pass order: `AtmosphereColor` composites before `VolumetricFogColor`, bloom, and exposure/tonemapping.
- [x] Added `AtmosphericScatteringComponent` with `SetField(...)` property mutation, clamping, revision tracking, sky render command, and per-world registry selection.
- [x] Added `AtmosphericScatteringSettings` with quality/debug modes, camera stage defaults, active-atmosphere selection, and inert disabled/no-active uniform state.
- [x] Added shared GLSL atmosphere math in `AtmosphereCommon.glsl`.
- [x] Added sky, half-depth, aerial-perspective, temporal-reprojection, and upscale shaders under `Build/CommonAssets/Shaders/Scene3D/Atmosphere/`.
- [x] Added atmosphere texture and FBO resources, identity recreation predicates, and anti-aliasing/history invalidation hooks.
- [x] Bound `AtmosphereColor` into `PostProcess.fs` and kept atmosphere raymarching out of the final composite shader.
- [x] Added ImGui component editor controls and authoring helpers.
- [x] Added Unit Testing World settings, bootstrap creation, camera post-process setup, generated schema support, and server settings mirror.
- [x] Added CPU/source-level regression tests for component selection, settings/schema defaults, shader contracts, and pipeline resource/order contracts.
- [x] Added stable feature docs and default-render-pipeline invariants.

## Validation Performed

- [x] `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj`
- [x] `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
- [x] `powershell -ExecutionPolicy Bypass -File Tools\Generate-UnitTestingWorldSettings.ps1`
- [x] `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter AtmosphericScattering --no-restore` attempted.

The targeted test command did not reach test execution because the current unit-test project has unrelated compile failures outside the atmosphere work, including stale Audio2Face/LipSync test symbols, `Engine` type ambiguity between `XREngine` and `XREngine.Runtime.Rendering`, a moved light-probe type, and read-only VR transform assignments. A quiet compile pass did not report errors from the new `AtmosphericScattering*` test files.

## Deferred Owner/GPU Validation

- [ ] Capture baseline and final screenshots for current skybox noon, sunset, and night.
- [ ] Capture atmosphere plus volumetric-fog ordering screenshots.
- [ ] Run Unit Testing World with atmosphere enabled.
- [ ] Validate ground, high-altitude, space, noon, sunset, and below-horizon cases.
- [ ] Validate existing skybox texture, gradient, solid-color, and dynamic-procedural modes visually.
- [ ] Capture GPU profiler timings for sky, half-depth, half-scatter, reprojection, upscale, and final composite.
- [ ] Check runtime logs for new OpenGL, shader, FBO, and texture warnings.

These items require a live graphics/editor session and are intentionally kept as validation work rather than code TODOs.

## Follow-Up Platform And Quality Work

- [ ] Add stereo texture-array variants for aerial perspective resources.
- [ ] Validate OpenVR two-pass stereo.
- [ ] Validate OpenVR/OpenXR single-pass stereo when the pipeline path is ready.
- [ ] Add optional transmittance/inscatter LUT quality mode.
- [ ] Add optional material hooks for objects outside the atmosphere.
- [ ] Evaluate ambient-light sync from sky luminance and sun elevation.
- [ ] Evaluate physically scaled editor gizmos for large-radius atmospheres.
- [ ] Evaluate Vulkan and DX12 resource/shader parity.

## Final Integration

- [ ] Merge `feature/atmospheric-scattering-component` back into `main` after owner visual validation and any required review.
