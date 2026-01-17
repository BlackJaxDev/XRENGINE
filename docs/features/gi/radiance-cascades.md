# Radiance Cascades

Radiance Cascades provide high-quality baked global illumination using a hierarchy of 3D radiance volumes at different resolutions and coverage areas.

## Overview

The radiance cascade system uses multiple "cascades" - 3D textures that store pre-computed radiance at different scales:

- **Cascade 0**: Highest resolution, smallest coverage (near-field detail)
- **Cascade 1**: Medium resolution, medium coverage
- **Cascade 2**: Lower resolution, larger coverage
- **Cascade 3**: Lowest resolution, largest coverage (far-field)

During rendering, the system automatically selects and blends between cascades based on distance from the camera and surface position.

## Components

### RadianceCascadeComponent

The main component for configuring radiance cascades:

```csharp
var cascades = sceneNode.AddComponent<RadianceCascadeComponent>();

// Add cascade levels (up to 4)
cascades.Cascades.Add(new RadianceCascadeLevel
{
    RadianceTexture = highResVolume,  // XRTexture3D
    VoxelSize = 0.25f,                // World units per voxel
    Enabled = true
});

cascades.Cascades.Add(new RadianceCascadeLevel
{
    RadianceTexture = medResVolume,
    VoxelSize = 0.5f,
    Enabled = true
});

// Global settings
cascades.Intensity = 1.0f;
cascades.Tint = ColorF4.White;
cascades.CascadesEnabled = true;
```

### RadianceCascadeLevel

Individual cascade configuration:

| Property | Description |
|----------|-------------|
| `RadianceTexture` | 3D texture containing baked radiance (XRTexture3D) |
| `VoxelSize` | Size of each voxel in world units |
| `Enabled` | Whether this cascade level is active |
| `LocalOrigin` | Origin of the volume in local space |
| `LocalExtents` | Size of the volume in local space |

## Quality Settings

### Temporal Accumulation

Reduce flickering with temporal blending:

```csharp
cascades.TemporalBlendFactor = 0.85f; // 0 = no blending, 0.95 = heavy smoothing
```

Higher values provide more stable GI but may cause ghosting on fast camera movement.

### Normal Offset

Reduce light leaking through thin geometry:

```csharp
cascades.NormalOffsetScale = 1.5f; // 0-5 range
```

Higher values push sample points along the surface normal, reducing light bleed but potentially causing banding artifacts.

### Half-Resolution Rendering

Trade quality for performance:

```csharp
cascades.HalfResolution = true; // 4x faster, uses depth-aware upscaling
```

The half-resolution pass uses bilinear sampling with depth-aware filtering during upscale to minimize artifacts.

## Debug Visualization

```csharp
cascades.DebugMode = ERadianceCascadeDebugMode.CascadeIndex;
// Off - Normal rendering
// CascadeIndex - Visualize cascade selection (red=0, green=1, blue=2, yellow=3)
// BlendWeights - Visualize blend factors as grayscale
```

## Render Pipeline Integration

The `VPRC_RadianceCascadesPass` compute shader:

1. Reads depth and normal buffers from the G-Buffer
2. Reconstructs world position for each pixel
3. Selects appropriate cascade based on distance and coverage
4. Samples and blends radiance from selected cascades
5. Applies temporal accumulation with history buffer
6. Outputs to `RadianceCascadeGITexture`

The `RadianceCascadeComposite` shader then blends the GI result into the forward render target.

## Cascade Selection Algorithm

```
For each pixel:
  1. Compute world position from depth
  2. For each cascade (high to low resolution):
     a. Transform position to cascade local space
     b. Check if position is within cascade bounds
     c. If valid, sample radiance and blend with lower cascades
  3. Apply temporal blending with previous frame
```

Cascades are prioritized by resolution - higher resolution cascades are preferred when the position falls within their bounds.

## Stereo/VR Support

Radiance cascades fully support stereo rendering:

```csharp
// Automatic - pipeline detects stereo mode
// Uses sampler2DArray for depth/normal input
// Uses image2DArray for GI output
// Single dispatch processes both eyes
```

## Creating Cascade Textures

Cascade textures should be RGB16F 3D textures containing pre-computed radiance:

```csharp
var cascadeTexture = new XRTexture3D(
    width: 64,
    height: 64,
    depth: 64,
    internalFormat: EPixelInternalFormat.Rgb16f,
    format: EPixelFormat.Rgb,
    type: EPixelType.HalfFloat
);
cascadeTexture.MinFilter = ETexMinFilter.LinearMipmapLinear;
cascadeTexture.MagFilter = ETexMagFilter.Linear;
cascadeTexture.UWrap = ETexWrapMode.ClampToBorder;
cascadeTexture.VWrap = ETexWrapMode.ClampToBorder;
cascadeTexture.WWrap = ETexWrapMode.ClampToBorder;
```

**Baking Process:**
1. Voxelize the scene geometry at the cascade resolution
2. Compute direct lighting for each voxel
3. Propagate light through the volume (light propagation volumes or similar)
4. Store final radiance in the 3D texture

## Performance Characteristics

| Setting | Impact |
|---------|--------|
| Cascade Count | +1 cascade ≈ +15% cost |
| Texture Resolution | 2x resolution ≈ 8x memory, 2x sample cost |
| Half Resolution | ~4x faster with minimal quality loss |
| Temporal Blend | Negligible cost, improves stability |
| Normal Offset | Negligible cost, reduces leaking |

**Recommended Configurations:**

| Scenario | Cascades | Resolution | Half-Res |
|----------|----------|------------|----------|
| Desktop High | 4 | 128³, 64³, 32³, 16³ | No |
| Desktop Medium | 3 | 64³, 32³, 16³ | No |
| VR | 2-3 | 64³, 32³ | Yes |
| Mobile | 2 | 32³, 16³ | Yes |

## Files of Interest

- **Compute (mono):** `Build/CommonAssets/Shaders/Compute/RadianceCascades/RadianceCascades.comp`
- **Compute (stereo):** `Build/CommonAssets/Shaders/Compute/RadianceCascades/RadianceCascadesStereo.comp`
- **Composite:** `Build/CommonAssets/Shaders/Scene3D/RadianceCascadeComposite.fs`
- **Component:** <xref:XREngine.Components.Lights.RadianceCascadeComponent>
- **Registry:** <xref:XREngine.Rendering.GI.RadianceCascadeRegistry>
- **Render Pass:** <xref:XREngine.Rendering.Pipelines.Commands.VPRC_RadianceCascadesPass>

## Current Limitations

- Only the first active `RadianceCascadeComponent` per world is sampled
- No in-engine baking - cascade textures must be pre-computed externally
- Cascade positions are relative to the component's transform (not camera-relative)

## See Also

- [Global Illumination Overview](global-illumination.md)
- [Light Volumes](light-volumes.md) - Single-level alternative
- [Surfel GI](surfel-gi.md) - Dynamic alternative
