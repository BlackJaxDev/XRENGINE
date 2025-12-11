# Light Volumes

This engine build adds a Light Volumes-inspired GI path as an alternative to light probes, ReSTIR, or voxel cone tracing.

## How it works
- A compute pass (`VPRC_LightVolumesPass`) samples a baked 3D irradiance volume into `LightVolumeGITexture` using the current depth/normal buffers.
- A lightweight composite shader (`LightVolumeComposite.fs`) additively blends the GI buffer into the forward target.
- Volumes are provided by `LightVolumeComponent` instances registered per-world via `LightVolumeRegistry`.
- Both mono and stereo (VR) rendering modes are supported. In stereo mode, the pass uses `sampler2DArray` and `image2DArray` to process both eyes in a single dispatch.

## Usage
1. Add a `LightVolumeComponent` to a scene node and assign an `XRTexture3D` baked irradiance volume. Set `HalfExtents`, `Tint`, and `Intensity` to match the baked content.
2. Enable the mode at runtime: `Engine.UserSettings.GlobalIlluminationMode = EGlobalIlluminationMode.LightVolumes;` (or through any settings UI you expose).
3. Ensure your volume bounds cover the region you want lit; only the first active volume per world is sampled right now.
4. For VR/stereo rendering, the pipeline automatically detects stereo mode and dispatches the appropriate shader variant.

## Current limitations
- Only single-volume sampling is supported; the first active volume in the current world is used.
- The pass assumes a pre-baked RGB irradiance texture; no in-engine baking or shadow volume import is performed.

## Files of interest
- Compute (mono): `Build/CommonAssets/Shaders/Compute/LightVolumes/LightVolumes.comp`
- Compute (stereo): `Build/CommonAssets/Shaders/Compute/LightVolumes/LightVolumesStereo.comp`
- Composite: `Build/CommonAssets/Shaders/Scene3D/LightVolumeComposite.fs`
- Pipeline hook: `DefaultRenderPipeline` (`VPRC_LightVolumesPass`, `LightVolumeGITexture`, `LightVolumeCompositeFBO`)
- Component/registry: `LightVolumeComponent`, `LightVolumeRegistry`
