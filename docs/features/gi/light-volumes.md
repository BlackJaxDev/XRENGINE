# Light Volumes

Light Volumes provide efficient baked global illumination using 3D irradiance textures. They offer smooth, continuous GI sampling ideal for large open areas.

## Overview

A Light Volume is a 3D texture containing pre-computed irradiance data. During rendering, the pipeline samples this volume based on world position to provide indirect lighting. This approach is simpler than cascaded solutions and ideal for single-region coverage.

## How It Works

1. **Setup**: Place a `LightVolumeComponent` and assign a baked 3D irradiance texture
2. **Rendering**: The `VPRC_LightVolumesPass` compute shader samples the volume
3. **Composite**: Results are blended into the forward render target

The volume is sampled using world-space coordinates transformed into the volume's local space, allowing the volume to be positioned and rotated freely in the scene.

## Component Configuration

### LightVolumeComponent

```csharp
var volume = sceneNode.AddComponent<LightVolumeComponent>();

// Assign the baked irradiance texture
volume.VolumeTexture = bakedIrradianceVolume; // XRTexture3D

// Define the volume bounds
volume.HalfExtents = new Vector3(25.0f, 5.0f, 25.0f); // 50×10×50 world units

// Appearance settings
volume.Tint = ColorF4.White;     // Color multiplier
volume.Intensity = 1.0f;         // Brightness multiplier

// Enable/disable without removing
volume.VolumeEnabled = true;
```

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `VolumeTexture` | `XRTexture3D` | Baked irradiance volume (RGB) |
| `HalfExtents` | `Vector3` | Half-size of volume bounds in local space |
| `Tint` | `ColorF4` | Color tint applied to sampled radiance |
| `Intensity` | `float` | Brightness multiplier (0+) |
| `VolumeEnabled` | `bool` | Enable/disable this volume |

## Creating Volume Textures

Volume textures should be RGB format 3D textures containing pre-computed irradiance:

```csharp
var volumeTexture = new XRTexture3D(
    width: 64,
    height: 16,
    depth: 64,
    internalFormat: EPixelInternalFormat.Rgb16f,
    format: EPixelFormat.Rgb,
    type: EPixelType.HalfFloat
);

// Configure sampling
volumeTexture.MinFilter = ETexMinFilter.Linear;
volumeTexture.MagFilter = ETexMagFilter.Linear;
volumeTexture.UWrap = ETexWrapMode.ClampToEdge;
volumeTexture.VWrap = ETexWrapMode.ClampToEdge;
volumeTexture.WWrap = ETexWrapMode.ClampToEdge;
```

**Baking Process:**
1. Divide the volume bounds into a voxel grid
2. For each voxel center, compute indirect lighting:
   - Cast rays in all directions
   - Accumulate incoming radiance
   - Store average in the 3D texture
3. Optionally blur/filter for smoother results

## Enabling Light Volumes

```csharp
Engine.UserSettings.GlobalIlluminationMode = EGlobalIlluminationMode.LightVolumes;
```

## Render Pipeline Integration

The `VPRC_LightVolumesPass` performs:

1. **Depth/Normal Read**: Sample G-Buffer for pixel world position and normal
2. **Local Space Transform**: Convert world position to volume local space
3. **Bounds Check**: Verify position is within volume bounds
4. **Radiance Sample**: Trilinear sample from 3D texture
5. **Tint/Intensity**: Apply component settings
6. **Output**: Write to `LightVolumeGITexture`

The `LightVolumeComposite` shader then additively blends the GI into the forward target.

## Stereo/VR Support

Light Volumes fully support stereo rendering:

```csharp
// Automatic detection - no configuration needed
// Pipeline uses:
// - sampler2DArray for depth/normal input
// - image2DArray for GI output
// - Single dispatch processes both eyes
```

## Volume Registration

Volumes are automatically registered with the `LightVolumeRegistry` when:
- The component is activated
- `VolumeEnabled` is true
- A valid `VolumeTexture` is assigned

The registry provides fast per-world lookup for the render pipeline.

## Performance Characteristics

| Resolution | Memory | Sample Cost |
|------------|--------|-------------|
| 32³ | ~1 MB | Very Low |
| 64³ | ~8 MB | Low |
| 128³ | ~64 MB | Medium |

Light volumes are one of the most efficient GI solutions - a single trilinear sample per pixel with minimal branching.

## Limitations

- **Single Volume**: Only the first active volume per world is sampled
- **Pre-Baked**: No in-engine baking; textures must be computed externally
- **No Occlusion**: Light volumes don't account for runtime geometry changes
- **Box Coverage**: Volumes are axis-aligned boxes (in local space)

## Comparison with Other GI Modes

| Feature | Light Volumes | Radiance Cascades | Light Probes |
|---------|---------------|-------------------|--------------|
| Resolution | Single level | Multiple cascades | Sparse points |
| Coverage | Continuous | Continuous | Interpolated |
| Memory | Low-Medium | Medium | Low |
| Quality | Good | Very Good | Good |
| Setup | Simple | Complex | Medium |

## Files of Interest

| File | Purpose |
|------|---------|
| `Compute/LightVolumes/LightVolumes.comp` | Mono compute shader |
| `Compute/LightVolumes/LightVolumesStereo.comp` | Stereo compute shader |
| `Scene3D/LightVolumeComposite.fs` | Composite fragment shader |
| `LightVolumeComponent.cs` | Component implementation |
| `LightVolumeRegistry.cs` | Per-world volume tracking |
| `VPRC_LightVolumesPass.cs` | Render pass command |

## Best Practices

1. **Size Appropriately**: Match volume bounds to your playable area with some padding
2. **Resolution Balance**: Use higher resolution where detail matters (near ground level)
3. **Aspect Ratio**: Match voxel aspect ratio to your scene (e.g., thin volumes for outdoor areas)
4. **Multiple Volumes**: For complex scenes, consider upgrading to Radiance Cascades

## Example Setup

```csharp
// Create volume node at scene center
var volumeNode = world.RootNode.AddChild("GI Volume");
volumeNode.Transform.SetPosition(0, 2.5f, 0); // Centered 2.5m up

// Add and configure component
var volume = volumeNode.AddComponent<LightVolumeComponent>();
volume.VolumeTexture = LoadBakedVolume("level1_gi.dds");
volume.HalfExtents = new Vector3(50, 5, 50); // 100×10×100 area
volume.Intensity = 1.2f;

// Enable light volumes mode
Engine.UserSettings.GlobalIlluminationMode = EGlobalIlluminationMode.LightVolumes;
```

## API Reference

- <xref:XREngine.Components.Lights.LightVolumeComponent> - Volume component
- <xref:XREngine.Rendering.GI.LightVolumeRegistry> - Volume registration
- <xref:XREngine.Rendering.Pipelines.Commands.VPRC_LightVolumesPass> - Render pass

## See Also

- [Global Illumination Overview](global-illumination.md)
- [Radiance Cascades](radiance-cascades.md) - Multi-level alternative
- [Light Probes](light-probes.md) - Sparse sampling alternative
