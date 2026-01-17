# Surfel GI

Surfel GI is a real-time global illumination system inspired by GIBS (Global Illumination Based on Surfels). It dynamically spawns and manages surface elements (surfels) from the G-Buffer to compute indirect lighting.

## Overview

Unlike baked solutions, Surfel GI operates entirely at runtime:

1. **Spawn**: Surfels are created from visible G-Buffer pixels in 16×16 tiles
2. **Organize**: Surfels are inserted into a spatial hash grid for efficient lookup
3. **Shade**: Each pixel gathers lighting from nearby surfels
4. **Recycle**: Old surfels are removed and their slots reused

This approach handles fully dynamic scenes with moving geometry and changing lighting.

## How It Works

### Surfel Structure

Each surfel stores:
```csharp
struct SurfelGPU
{
    Vector4 PositionRadius;  // xyz = local position, w = world radius
    Vector4 Normal;          // xyz = local normal
    Vector4 Albedo;          // Surface color
    Vector4 Meta;            // x = frame index, y/z/w = reserved
}
```

Surfels are stored in object/local space for stability when objects move, with world-space reconstruction happening in shaders.

### Spatial Hash Grid

Surfels are organized in a 3D grid (default 32×32×32) centered on the camera:

| Parameter | Value |
|-----------|-------|
| Grid Dimensions | 32 × 32 × 32 cells |
| Grid Half-Extent | 50 world units |
| Max Surfels Per Cell | 16 |
| Max Total Surfels | 131,072 |

This allows O(1) lookup for nearby surfels during the gather phase.

### Pipeline Stages

1. **Init**: One-time initialization of buffers and free list
2. **Recycle**: Remove surfels older than 300 frames, return to free list
3. **Reset Grid**: Clear per-cell counts
4. **Build Grid**: Insert active surfels into spatial cells
5. **Spawn**: Create new surfels from G-Buffer where coverage is low
6. **Shade**: Gather and accumulate indirect lighting per pixel

## Configuration

Surfel GI is enabled through the global illumination mode:

```csharp
Engine.UserSettings.GlobalIlluminationMode = EGlobalIlluminationMode.SurfelGI;
```

The `VPRC_SurfelGIPass` render command handles all computation:

```csharp
// Exposed for debug visualization
public XRDataBuffer? SurfelBuffer { get; }
public XRDataBuffer? CounterBuffer { get; }
public XRDataBuffer? GridCountsBuffer { get; }
public XRDataBuffer? GridIndicesBuffer { get; }

// Current grid state
public Vector3 CurrentGridOrigin { get; }
public float CurrentCellSize { get; }
```

### Texture Configuration

| Property | Default |
|----------|---------|
| `DepthTextureName` | `DepthViewTexture` |
| `NormalTextureName` | `NormalTexture` |
| `AlbedoTextureName` | `AlbedoOpacityTexture` |
| `TransformIdTextureName` | `TransformIdTexture` |
| `OutputTextureName` | `SurfelGITexture` |

## Debug Visualization

The `SurfelDebugRenderPipeline` provides visualization modes:

```csharp
// Render surfels as colored points
var debugPipeline = new SurfelDebugRenderPipeline();
// Shows surfel positions, normals, and density
```

The `VPRC_SurfelDebugVisualization` command can be added to any pipeline for surfel overlay rendering.

## Performance Characteristics

| Operation | Cost |
|-----------|------|
| Recycle | Low (parallel scan) |
| Reset Grid | Very Low (buffer clear) |
| Build Grid | Medium (atomic insertions) |
| Spawn | Medium (16×16 tile dispatch) |
| Shade | Medium-High (per-pixel gather) |

**Approximate Performance:**
- 1080p: ~2-3ms on RTX 3070
- 4K: ~6-8ms on RTX 3070
- VR (2× eye): Currently mono-only, stereo fallback clears to transparent

### Memory Usage

```
Surfel Buffer:    131,072 × 64 bytes = 8 MB
Counter Buffer:   16 bytes
Free Stack:       131,072 × 4 bytes = 512 KB
Grid Counts:      32³ × 4 bytes = 128 KB
Grid Indices:     32³ × 16 × 4 bytes = 2 MB
Total:            ~10.5 MB
```

## Current Implementation Status

The current implementation includes:

✅ Surfel spawning from G-Buffer tiles  
✅ Spatial hash grid organization  
✅ Basic surfel gather for GI output  
✅ Surfel recycling (age-based)  
✅ Composite into forward target  

### Planned Improvements

🔲 Ray-traced irradiance integration  
🔲 Non-linear grid adaptation  
🔲 Coverage-driven spawning (paper algorithm)  
🔲 Stereo/VR support  
🔲 Multi-bounce propagation  

## Shader Files

| Shader | Purpose |
|--------|---------|
| `Compute/SurfelGI/Init.comp` | Initialize buffers and free list |
| `Compute/SurfelGI/Recycle.comp` | Remove old surfels |
| `Compute/SurfelGI/ResetGrid.comp` | Clear grid counts |
| `Compute/SurfelGI/BuildGrid.comp` | Insert surfels into grid |
| `Compute/SurfelGI/Spawn.comp` | Create surfels from G-Buffer |
| `Compute/SurfelGI/Shade.comp` | Gather and shade pixels |
| `Scene3D/SurfelGIComposite.fs` | Blend into forward target |

## Technical Details

### Spawn Algorithm

```
For each 16×16 tile:
  1. Sample G-Buffer at tile center
  2. Check grid cell for existing surfel coverage
  3. If coverage low and valid geometry:
     a. Pop index from free stack (atomic)
     b. Initialize surfel from G-Buffer data
     c. Store in object space using TransformId
```

### Shade Algorithm

```
For each pixel:
  1. Reconstruct world position from depth
  2. Compute grid cell coordinates
  3. For each surfel in cell (and neighbors):
     a. Reconstruct surfel world position
     b. Compute distance and visibility
     c. Accumulate weighted radiance
  4. Output accumulated indirect lighting
```

## Comparison with Other GI Modes

| Feature | Surfel GI | Light Probes | Radiance Cascades |
|---------|-----------|--------------|-------------------|
| Dynamic Scenes | ✅ Full | ⚠️ Limited | ❌ Baked |
| Memory | Medium | Low | Medium |
| Quality | Good | Good | Very Good |
| Performance | Medium | Very Fast | Fast |
| Setup Required | None | Probe Placement | Baking |

## API Reference

- <xref:XREngine.Rendering.Pipelines.Commands.VPRC_SurfelGIPass> - Main GI compute pass
- <xref:XREngine.Rendering.Pipelines.Commands.VPRC_SurfelDebugVisualization> - Debug overlay
- <xref:XREngine.Rendering.Pipelines.Types.SurfelDebugRenderPipeline> - Debug pipeline

## See Also

- [Global Illumination Overview](global-illumination.md)
- [ReSTIR GI](restir-gi.md) - Higher quality ray-traced alternative
- [Light Probes](light-probes.md) - Faster baked alternative
