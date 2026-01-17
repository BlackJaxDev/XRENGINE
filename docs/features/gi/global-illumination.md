# Global Illumination Overview

XREngine provides multiple global illumination (GI) strategies to simulate realistic indirect lighting. Each mode offers different trade-offs between quality, performance, and workflow complexity.

## Available GI Modes

| Mode | Type | Performance | Quality | Best For |
|------|------|-------------|---------|----------|
| [Light Probes & IBL](light-probes.md) | Baked/Hybrid | ⭐⭐⭐⭐⭐ | ⭐⭐⭐ | Static scenes, VR |
| [Light Volumes](light-volumes.md) | Baked | ⭐⭐⭐⭐⭐ | ⭐⭐⭐ | Large open areas |
| [Radiance Cascades](radiance-cascades.md) | Baked | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ | High-quality static GI |
| [Surfel GI](surfel-gi.md) | Real-time | ⭐⭐⭐ | ⭐⭐⭐⭐ | Dynamic scenes |
| [ReSTIR GI](restir-gi.md) | Real-time | ⭐⭐ | ⭐⭐⭐⭐⭐ | Ray tracing capable hardware |
| [Voxel Cone Tracing](voxel-cone-tracing.md) | Hybrid | ⭐⭐⭐ | ⭐⭐⭐⭐ | Medium-scale dynamic scenes |

## Selecting a GI Mode

Set the GI mode through code or user settings:

```csharp
// Via user settings (persists across sessions)
Engine.UserSettings.GlobalIlluminationMode = EGlobalIlluminationMode.RadianceCascades;

// Via startup settings (per-launch override)
var startup = new GameStartupSettings
{
    GlobalIlluminationModeOverride = new OverrideableSetting<EGlobalIlluminationMode>
    {
        Override = true,
        Value = EGlobalIlluminationMode.SurfelGI
    }
};
```

## Mode Details

### Light Probes & IBL (Default)
The default mode uses a sparse grid of environment probes that capture and interpolate indirect lighting. Combined with Image-Based Lighting (IBL) from reflection probes, this provides efficient high-quality GI for static and mostly-static scenes.

**Components:** <xref:XREngine.Components.Capture.Lights.LightProbeComponent>, <xref:XREngine.Components.Capture.LightProbeGridSpawnerComponent>

### Light Volumes
Baked 3D irradiance volumes provide smooth, continuous GI sampling. Best for large open areas where probe interpolation would be too sparse.

**Components:** <xref:XREngine.Components.Lights.LightVolumeComponent>

### Radiance Cascades
A cascaded 3D radiance volume system with multiple resolution levels. Higher-resolution cascades cover near-field GI while lower-resolution cascades extend coverage. Features temporal accumulation and half-resolution rendering options.

**Components:** <xref:XREngine.Components.Lights.RadianceCascadeComponent>

### Surfel GI
A GIBS-inspired dynamic GI system using GPU-accelerated surfels (surface elements). Surfels are spawned from the G-Buffer, organized in a spatial hash grid, and used to accumulate and shade indirect lighting in real-time.

**Render Pass:** <xref:XREngine.Rendering.Pipelines.Commands.VPRC_SurfelGIPass>

### ReSTIR GI
Hardware ray-traced GI using NVIDIA's ReSTIR algorithm for efficient light path sampling. Provides the highest quality results but requires RTX-capable hardware and Vulkan rendering.

**API:** <xref:XREngine.Rendering.GI.RestirGI>

### Voxel Cone Tracing
Voxelizes the scene and traces cones through the voxel grid to approximate indirect lighting. Provides a balance between quality and performance for medium-scale scenes with some dynamic content.

## Ambient Occlusion

In addition to GI, XREngine supports multiple ambient occlusion (AO) techniques that can be combined with any GI mode:

| AO Type | Description |
|---------|-------------|
| Screen-Space AO | Fast, depth-buffer based occlusion |
| Multi-View AO | Uses multiple depth views for improved accuracy |
| Horizon-Based AO | HBAO/HBAO+ style occlusion |
| Scalable Ambient Obscurance | SAO algorithm |
| Multi-Scale Volumetric Obscurance | MSVO for large-scale occlusion |
| Spatial Hash Raytraced | Ray-marched AO using spatial hashing |

Configure AO through the camera's post-process settings:

```csharp
var camera = GetComponent<CameraComponent>();
var aoSettings = camera.PostProcessSettings.Get<AmbientOcclusionSettings>();
aoSettings.Type = AmbientOcclusionSettings.EType.ScreenSpace;
aoSettings.Intensity = 1.2f;
aoSettings.Radius = 2.0f;
```

## Pipeline Integration

The default render pipeline integrates GI through dedicated render passes:

1. **G-Buffer Pass** - Generates depth, normals, albedo for GI sampling
2. **GI Compute Pass** - Mode-specific GI calculation (varies by mode)
3. **GI Composite Pass** - Blends GI results into the forward target
4. **Post-Process** - Applies AO and other effects

Each GI mode has its own composite FBO and texture targets managed by <xref:XREngine.Rendering.Pipelines.Types.DefaultRenderPipeline>.

## Performance Considerations

### VR/Stereo Rendering
All GI modes support stereo rendering with optimized shader variants that process both eyes in a single dispatch using `sampler2DArray` and `image2DArray`.

### Half-Resolution Rendering
Radiance Cascades and other compute-based modes support half-resolution rendering with depth-aware upscaling for improved performance:

```csharp
var cascades = GetComponent<RadianceCascadeComponent>();
cascades.HalfResolution = true; // 4x faster, minimal quality loss
```

### Temporal Accumulation
Dynamic GI modes use temporal blending to reduce noise and flickering:

```csharp
cascades.TemporalBlendFactor = 0.85f; // Higher = more stable, more ghosting
```

## See Also

- [Light Probes](light-probes.md) - Detailed light probe documentation
- [Light Volumes](light-volumes.md) - Light volume configuration
- [Radiance Cascades](radiance-cascades.md) - Cascaded radiance volumes
- [Surfel GI](surfel-gi.md) - Dynamic surfel-based GI
- [ReSTIR GI](restir-gi.md) - Hardware ray-traced GI
- [Ambient Occlusion](ambient-occlusion.md) - AO configuration
