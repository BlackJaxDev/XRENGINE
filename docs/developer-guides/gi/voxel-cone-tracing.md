# Voxel Cone Tracing

Voxel Cone Tracing (VCT) is a hybrid global illumination technique that voxelizes the scene and traces cones through the voxel grid to approximate indirect lighting.

## Overview

VCT works in two phases:

1. **Voxelization**: Convert scene geometry and lighting into a 3D voxel grid
2. **Cone Tracing**: Trace wide cones through the voxel grid to gather indirect light

This provides a balance between baked and real-time GI, allowing semi-dynamic scenes with reasonable performance.

## Enabling VCT

```csharp
Engine.UserSettings.GlobalIlluminationMode = EGlobalIlluminationMode.VoxelConeTracing;
```

## How It Works

### Voxelization Phase

The scene is rendered into a 3D texture from orthographic viewpoints:

1. Render scene from +X, +Y, +Z directions
2. Write fragment colors into 3D texture (atomic operations)
3. Generate mipmaps for cone tracing

Voxel resolution typically ranges from 64³ to 256³ depending on quality requirements.

### Cone Tracing Phase

For each pixel:

1. **Diffuse Cones**: Trace multiple wide cones in the hemisphere around the normal
2. **Specular Cone**: Trace a narrow cone along the reflection direction
3. **Accumulate**: Gather radiance from voxels along each cone

Cone width increases with distance, naturally sampling coarser mip levels for distant voxels (similar to how LOD works for textures).

## Quality vs Performance

| Voxel Resolution | Memory | Voxelization | Cone Tracing |
|------------------|--------|--------------|--------------|
| 64³ | ~16 MB | Fast | Very Fast |
| 128³ | ~128 MB | Medium | Fast |
| 256³ | ~1 GB | Slow | Medium |

### Cone Configuration

| Setting | Impact |
|---------|--------|
| Diffuse Cones | 5-9 cones (more = smoother, slower) |
| Cone Aperture | Wider = softer GI, narrower = sharper |
| Max Distance | Larger = more GI coverage, more samples |
| Mip Bias | Controls sharpness vs smoothness tradeoff |

## Dynamic Updates

VCT supports partial re-voxelization for dynamic objects:

1. **Full Revoxelize**: Complete scene rebuild (expensive, 1-2 frames)
2. **Cascaded Voxels**: Multiple resolutions for near/far (like cascaded shadow maps)
3. **Clipmap**: Scrolling voxel volume centered on camera

For mostly static scenes, voxelize once and update only when significant changes occur.

## Advantages

- **Semi-Dynamic**: Can update voxels for moving objects
- **Multi-Bounce**: Naturally supports multiple light bounces
- **Unified**: Same system handles diffuse and specular GI
- **Scalable**: Quality/performance tradeoff via resolution

## Limitations

- **Memory**: High-resolution voxels require significant VRAM
- **Temporal Stability**: Can flicker without temporal filtering
- **Thin Geometry**: Small objects may be missed during voxelization
- **Light Leaking**: Coarse voxels can cause light to pass through walls

## Comparison with Other GI Modes

| Feature | VCT | Surfel GI | Radiance Cascades |
|---------|-----|-----------|-------------------|
| Update Cost | Medium | Low | N/A (baked) |
| Memory | High | Medium | Medium |
| Quality | Good | Good | Very Good |
| Thin Geometry | ⚠️ | ✅ | ✅ |
| Multi-Bounce | ✅ | ⚠️ | ✅ |

## Implementation Status

⚠️ **Note**: Voxel Cone Tracing is currently marked as an available mode in `EGlobalIlluminationMode` but the full implementation may be in progress. Check the render pipeline for current support status.

## Recommended Use Cases

- Medium-scale environments (rooms, small outdoor areas)
- Scenes with limited dynamic content
- Projects that need multi-bounce GI without ray tracing hardware
- VR where consistent frame timing is critical

## See Also

- [Global Illumination Overview](global-illumination.md)
- [Surfel GI](surfel-gi.md) - Lighter-weight dynamic alternative
- [Radiance Cascades](radiance-cascades.md) - Pre-baked cascaded volumes
- [Light Probes](light-probes.md) - Fastest baked solution
