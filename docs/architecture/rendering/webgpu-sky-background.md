# WebGPU sky backgrounds

`SkyboxComponent` uses its existing fullscreen-triangle `RenderCommandMesh3D`,
`EDefaultRenderPass.Background`, and typed binding publisher on WebGPU. The shared
`DefaultRenderPipeline` executes that pass in the linear RGBA16F scene target
after opaque/masked draws and before sorted transparency, bloom and tonemapping.
No browser-owned scene or substitute unlit material is involved.

## Authored modes and shader identity

The cooked material catalog has five version-one semantic identities:

- `SkyboxGradient`: world-Y gradient; solid color publishes the top color into
  both endpoints
- `SkyboxEquirectangular`: the desktop spherical direction-to-UV mapping
- `SkyboxOctahedral`: world Y maps to the octahedral Z axis, including the lower
  hemisphere fold
- `SkyboxCubemap`: an actual six-face cube sampled by direction
- `SkyboxDynamicProcedural`: the complete authored procedural sky, including
  scattering, day/night weighting, sun and moon discs, moon phases, directional
  clouds, stars, the Milky Way and camera-motion twinkle

Each uses the exact `background/fullscreen-sky-v1/linear-hdr-v1` variant key.
Sources and recipes are in `Build/CommonAssets/Shaders/WebGPU/Skybox*.slang` and
`engine-skybox-*.recipe.json`. A selected semantic requires its matching recipe
in the project's cooked shader manifest. Missing variants fail explicitly.

Browser material construction is source-free and does not load GLSL. Desktop
material construction and its original shader files remain unchanged. The
component retains its existing time-of-day update, optional sun/moon directional
light synchronization, color temperatures and global ambient contribution.
The forward material still consumes the engine's no-probe ambient semantics;
visible sky radiance does not silently enable image-based lighting.

## Camera, depth and color

The vertex shader uses the actual inverse projection and inverse camera-view
matrices. The camera translation is removed by transforming the reconstructed
view ray with `w=0`; authored sky rotation remains a Y-axis rotation in degrees
converted by the existing component publisher. Normal-Z far clip depth is 1.

The admitted output is mono, single-sample, normal-Z, with less-equal depth
testing, no depth writes, no blending and no face culling. Sky color and opaque
alpha are written to HDR before presentation. Foreground depth occludes the sky,
and sorted transparent surfaces blend over it. The engine's existing normal-Z
camera and mono-output gates remain authoritative.

Sky texture topology and storage are preserved: equirectangular and octahedral
inputs are `XRTexture2D`, and cube inputs are `XRTextureCube`. Admitted color
formats are RGBA8, sRGB RGBA8 and linear RGBA16F. Hardware sRGB decoding applies
only to the sRGB format; no additional sky gamma transform is added. RGBA16F
uploads retain the authored half-float bytes (eight bytes per texel), including
values above one. There is no conversion to RGBA8 or a single cube face.
Authored mip ranges, filters, wrapping and LOD clamps use the shared texture
backend. Automatic mip generation remains excluded; supply the required mip
levels explicitly.

## Explicit limits

Preflight and runtime diagnostics identify the missing contract:

- `WebGPU.Skybox.CubemapArrayUnsupported`: cube arrays require a separate texture
  view and shader profile
- `WebGPU.Skybox.TextureMissing`, `TextureTopologyMismatch`,
  `TextureFormatUnsupported`, `MipGenerationUnsupported` and `ParametersInvalid`:
  authored sky requirements cannot be represented exactly
- `BrowserCook.SkyboxVariantMissing` / `WebGPU.Skybox.VariantMissing`: the exact
  selected shader program is absent
- `BrowserCook.EnvironmentCaptureUnsupported` / `WebGPU.Skybox.CaptureUnsupported`:
  live environment capture, probe convolution and reflection/capture outputs
  have no admitted capture pipeline
- `WebGPU.DefaultPipeline.LightProbeIblUnsupported`: active probes, irradiance or
  prefiltered IBL resources require an explicit lighting implementation
- `BrowserCook.AtmosphereUnsupported`: the separate planetary atmosphere and
  aerial-perspective component is not the procedural `SkyboxComponent` shader

Shader cooking and managed compilation are separate from visual acceptance.
On 2026-10-02, the pinned official Slang 2026.8 toolchain cooked all five sky
recipes successfully, with their declared view/material/texture ABIs verified
by ShaderCooker. Its tool build completed with zero warnings and errors.
The integrated Editor and native-Jolt Browser Release builds also pass with zero
warnings and errors. Known-value GPU rendering remains a separate acceptance
step.
Known-value GPU acceptance must cover asymmetric gradient orientation, camera
translation/rotation, labeled cube faces and seams, panorama/octahedral mapping,
HDR values through bloom/tonemap, foreground occlusion, sorted transparency over
sky, procedural day/night controls and retained sun/moon/ambient behavior.
