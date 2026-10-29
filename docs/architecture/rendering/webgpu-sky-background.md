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

The default-pipeline output admits mono, one or four samples, normal-Z, with less-equal depth
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

## Cooked texture admission

The Editor world audit validates static CPU mip storage before shader lookup.
`BrowserSkyTextureCookProfile` accepts exact `XRTexture2D` and `XRTextureCube`
sources with resident pixels. Mip dimensions must follow the natural halving
chain. Partial chains are valid. Cubes require six square faces at each level.
RGBA8 and sRGB RGBA8 require four RGBA unsigned bytes per texel. RGBA16F requires
eight RGBA half-float bytes; cube faces can also use sixteen RGBA float bytes,
which the layered upload backend converts to half-float.

The audit rejects missing or malformed payloads, streaming upload buffers,
GPU-written sources, external memory, and sRGB storage-image usage. The 2D path
also reuses the canonical static-image settings check for sparse, grab-pass, and
active upload state. Errors include the scene node and texture identity under
`BrowserCook.SkyboxUnsupported` and `BrowserCook.SkyTextureStorageUnsupported`.
Validation does not copy pixels or run in the frame loop. It does not generate
mips, encode platform compression, or prove device-specific texture limits.

`Mipmap2D` has an explicit runtime cooked payload for dimensions, pixel formats,
and nullable CPU bytes. This retains cube faces when canonical cooking suppresses
MemoryPack: reflection excludes both `Data` and `DataBytes`. The payload has a
version and a checked byte length. Rendering serialization installs its canonical
and runtime constructors for published loading. Existing reflection payloads can
still be read, but their omitted pixels cannot be recovered. The separate
`XRTexture2D` streamable mip format and YAML/MemoryPack contracts are unchanged.

## Published sky texture assets

All six texture diagnostics use authored assets from the engine content package.
`BrowserSkyTextureFixtures` cooks stable identities and canonical `BinaryV2`
envelopes with MemoryPack suppressed. Cube payloads use explicit nested mip
storage; image payloads use the existing full `XRTexture2D` reader, including
its mip descriptors and sampling fields. These browser fixtures do not generate
texture pixel buffers.

The browser installs verified type metadata, catalog providers, and asset
services, then calls `Engine.Assets.LoadFromRuntimeSourceAsync` in `Published`
mode. It passes the loaded texture to the shared sky fixture. The fixture
borrows the asset until its source owner ends the session. GPU diagnostic labels
follow the production asset loader's file-derived names.

| Virtual asset under `/game/` | Pixel storage | Retained pixel bytes |
|---|---|---|
| `SkyCubeHdr.asset` | Six 4×4 RGBA16F faces | 768 |
| `SkyCubeFiltered.asset` | Six sRGB faces, 4×4 through 1×1 | 504 |
| `SkyEquirectangularHdr.asset` | 8×4 RGBA16F | 256 |
| `SkyOctahedralHdr.asset` | 8×8 RGBA16F | 512 |
| `SkyEquirectangularFiltered.asset` | sRGB, 8×4 through 1×1 | 172 |
| `SkyOctahedralFiltered.asset` | sRGB, 8×8 through 1×1 | 340 |

The browser delivery ledger counts distinct owned mip `DataSource` buffers.
Checks require drained staging, one retained asset and object, the exact asset
path, and zero surviving asset sources after stop. The canonical custom reader
checks the legacy interface before constructing that branch. Runtime-custom
objects are constructed only by their runtime reader; a discarded constructor
probe must not enter the ownership ledger.

These are source-retention estimates, not measurements of immediate native heap
reclamation. The route uses the browser interpreter and canonical reflection
loading; it does not establish AOT texture decoding or packaged Editor publication.
See the [cube record](../../work/investigations/rendering/archive/2026-10-29-published-cube-sky-qualification.md)
and [image ownership record](../../work/investigations/rendering/archive/2026-10-29-published-image-sky-qualification.md).

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
warnings and errors.

`Tools/BrowserSmoke` includes `engine-sky-gradient`. Its real engine fixture
checks asymmetric gradient orientation, camera translation and rotation, linear
HDR intensity, solid color, foreground occlusion, and tonemapped canvas pixels.
It also checks non-square resize, restoration, two device sessions, and bounded
resource retention. The [qualification record](../../work/investigations/rendering/archive/2026-10-29-gradient-sky-qualification.md)
separates measured software results from broader acceptance.

`engine-sky-cubemap` uses six asymmetric RGBA16F faces. Camera rays select each
face and check its row/column orientation. Translation, authored rotation, HDR
intensity, foreground depth, restart, and resize use the same retained scene.
The [cubemap qualification record](../../work/investigations/rendering/archive/2026-10-29-cubemap-sky-qualification.md)
records the raw half-float and nearest-sampling boundary.

`engine-sky-equirectangular` and `engine-sky-octahedral` check asymmetric HDR
images through the real component and shared pipeline. Their coordinate
references cover longitude/latitude and the lower-hemisphere octahedral fold,
plus camera changes, authored rotation, HDR output, foreground depth, resize,
and restart. The [image projection record](../../work/investigations/rendering/archive/2026-10-29-sky-image-projection-qualification.md)
separates nearest-texel mapping from seam and pole filtering.

`engine-sky-procedural` checks clear atmosphere at noon, sunrise, sunset, and
midnight. Clouds and stars are disabled. Independent camera-ray calculations
check atmospheric scattering, dusk weighting, night base, and off-disc celestial
glow in HDR and after tonemapping. Camera pitch, yaw, translation, authored
rotation, intensity, foreground depth, resize, and restart use the same scene.
Zero cloud coverage explicitly skips the noise and density calculation, so it
does not evaluate a smoothstep interval with equal edges. Positive coverage
retains the existing formulas. See the
[clear procedural sky record](../../work/investigations/rendering/archive/2026-10-29-clear-procedural-sky-qualification.md).

`engine-sky-sun` aims the real camera at the procedural sun and checks disc
radiance, limb darkening, day-cycle color, authored size, and dimmed dawn before
display clipping. It also checks translation and foreground occlusion. The
shared clear-atmosphere reference evaluates the optional sun disc without GPU
input. Each resized extent samples both foreground and sun, so the full program
inventory is active when resource retention is checked. See the
[sun disc record](../../work/investigations/rendering/archive/2026-10-29-sun-disc-qualification.md).

The `engine-sky-equirectangular-filtered` and `engine-sky-octahedral-filtered`
profiles check sRGB decoding before bilinear interpolation with four authored
mips. They cover panorama U repetition and pole clamping, octahedral axis
clamping, integer LOD selection, camera pose, intensity, resize, and restart.
Texture storage and native programs stay fixed when sampler changes rebuild the
selected draw's managed pipeline handle. See the
[filtered image record](../../work/investigations/rendering/archive/2026-10-29-filtered-sky-image-qualification.md)
for the authored-image and mip-selection boundaries.

The `engine-sky-cubemap-filtered` profile checks six sRGB faces, all twelve
two-face edges at two mip levels, and selection of a uniform final mip. The
reference decodes face colors before edge interpolation. Cube storage, the
sampled cube view, and native programs remain fixed across sampler changes,
camera changes, intensity, resize, and restart. The
[filtered cube record](../../work/investigations/rendering/archive/2026-10-29-filtered-cubemap-qualification.md)
separates two-face blending from corner weights and imported-image quality.

The `engine-sky-moon` profile checks exact off-disc corona and daytime values,
plus analytic bounds for the noisy moon surface and its combined corona.
Twelve authored cases cover day/night visibility, size, HDR intensity, camera
translation, sky rotation, resize, and restart. The report marks bounded regions;
this does not establish exact maria/crater-pattern parity. The
[moon bounds record](../../work/investigations/rendering/archive/2026-10-29-moon-radiance-bounds-qualification.md)
records the failed float32 noise comparison and the unexercised phase/edge terms.

Known-value GPU acceptance remains open for implicit and fractional LOD,
automatic mip generation, anisotropy, filtering across octahedral seams,
three-face cube corner filtering, sky bloom, sorted transparency over sky, procedural
clouds, stars, exact moon surface detail, nonzero lunar phase and soft-edge falloff,
automatic cycling, retained sun/moon/ambient
behavior, and hardware devices.
