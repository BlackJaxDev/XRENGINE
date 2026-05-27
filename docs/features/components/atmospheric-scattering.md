# Atmospheric Scattering Component

`AtmosphericScatteringComponent` adds an OpenGL-first planetary atmosphere path for physical sky rendering and distance aerial perspective. It is separate from `SkyboxComponent.DynamicProcedural` and from local `VolumetricFogVolumeComponent` fog volumes.

## Units And Placement

The first implementation uses SI-like world-unit defaults:

- Ground radius: `6,371,000`
- Atmosphere height: `100,000`
- Rayleigh scale height: `8,000`
- Mie scale height: `1,200`

The component transform origin is treated as the authored local ground point. The planet center is computed from that origin by moving down the component up vector by `GroundRadius + GroundLevelOffset`. This makes the default setup usable in a local test world without placing the scene at a huge negative coordinate.

Only one atmosphere is selected per camera. Selection prefers atmospheres that contain the camera, then higher `Priority`, then the nearest outer shell.

## Component Properties

Add `AtmosphericScatteringComponent` to a scene node under the rendering environment components.

Core controls:

- `Enabled`: registers the component and makes it selectable.
- `Priority`: tie-breaker when more than one active atmosphere is available.
- `RenderSky`: enables the background-pass sky draw.
- `AerialPerspective`: enables screen-space distance scattering.
- `GroundRadius`, `AtmosphereHeight`, `GroundLevelOffset`: define the planet shell.

Sun controls:

- `SunSource`: selects primary directional light, explicit directional light, or direction override.
- `SunDirectionalLight`: explicit light used when `SunSource` is `ExplicitDirectionalLight`.
- `SunDirectionOverride`: world-space direction toward the sun when no light is used.
- `SunIntensity`, `SunColor`: radiance controls for the scattering shaders.

Scattering controls:

- `RayleighScaleHeight`, `MieScaleHeight`
- `RayleighScattering`, `MieScattering`
- `MieAnisotropy`
- `ExposureScale`
- `GroundAlbedo`

The ImGui component editor includes helper buttons for primary light assignment, scene light picking, Earth-at-ground defaults, Earth-at-center defaults, and copying direction from the selected light.

## Camera Settings

Camera post-process state exposes an `atmosphericScattering` stage backed by `AtmosphericScatteringSettings`.

Settings:

- `Enabled`
- `RenderSky`
- `AerialPerspective`
- `Quality`: `Low`, `Balanced`, `High`, `Reference`
- `ViewSamples`
- `OpticalDepthSamples`
- `MaxDistance`
- `JitterStrength`
- `TemporalEnabled`
- `DebugMode`

Debug modes:

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

## Pipeline Order

The aerial-perspective chain is implemented in both `DefaultRenderPipeline` and `DefaultRenderPipeline2`:

1. Half-resolution depth downsample.
2. Half-resolution scatter/transmittance.
3. Temporal reprojection into half-resolution history.
4. Full-resolution bilateral upscale into `AtmosphereColor`.
5. `PostProcess.fs` composites atmosphere before local volumetric fog:

```glsl
hdrSceneColor = hdrSceneColor * atmosphere.a + atmosphere.rgb;
```

Disabled or no-active-atmosphere frames output neutral `(0,0,0,1)`, so the composite is a no-op.

## Unit Testing World

The generated Unit Testing World settings include:

- `InitializeAtmosphericScattering`
- `AtmosphericScattering`

Enable `InitializeAtmosphericScattering` in `Assets/UnitTestingWorldSettings.jsonc` to spawn a default atmosphere. Regenerate the schema and mirrored server settings after settings-shape changes:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\Generate-UnitTestingWorldSettings.ps1
```

`pwsh Tools/Generate-UnitTestingWorldSettings.ps1` is equivalent when PowerShell 7 is installed.

## Limitations

- OpenGL 4.6 mono rendering is the first implemented path.
- Stereo texture-array variants and OpenVR/OpenXR visual validation are follow-up work.
- Only one active atmosphere is selected per camera.
- The initial path is direct single scattering, without transmittance or inscatter LUTs.
- Multiple scattering, clouds, ozone, rainbows, fogbows, and weather are out of scope for the first pass.

## Related Documentation

- [Default Render Pipeline Notes](../../architecture/rendering/default-render-pipeline-notes.md)
- [Unit Testing World](../unit-testing-world.md)
- [Atmospheric Scattering Component Design](../../work/design/rendering/atmospheric-scattering-component-design.md)
