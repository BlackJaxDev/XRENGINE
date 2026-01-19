# Light Probes

Light probes capture environment lighting at specific points in the scene, enabling efficient indirect lighting for dynamic objects and providing high-quality reflections through Image-Based Lighting (IBL).

## Overview

The light probe system in XREngine uses cubemap captures that are processed into:
- **Irradiance maps** - Diffuse indirect lighting (low-frequency)
- **Prefiltered environment maps** - Specular reflections (varying roughness levels)

Probes can be baked offline or updated in real-time, and multiple probes are automatically interpolated using Delaunay triangulation.

## Components

### LightProbeComponent

The primary component for capturing environment lighting.

```csharp
var probe = sceneNode.AddComponent<LightProbeComponent>();

// Capture settings
probe.CaptureResolution = 512;
probe.IrradianceResolution = 32;

// Influence region
probe.InfluenceShape = LightProbeComponent.EInfluenceShape.Sphere;
probe.InfluenceSphereOuterRadius = 10.0f;
probe.InfluenceSphereInnerRadius = 2.0f; // Fade start

// Real-time updates (optional)
probe.Realtime = true;
probe.RealtimeUpdateInterval = TimeSpan.FromMilliseconds(100);
```

**Key Properties:**

| Property | Description |
|----------|-------------|
| `CaptureResolution` | Resolution of the cubemap capture (256-2048) |
| `IrradianceResolution` | Resolution of the diffuse irradiance map (16-64) |
| `InfluenceShape` | `Sphere` or `Box` influence region |
| `InfluenceSphereOuterRadius` | Maximum influence distance |
| `InfluenceSphereInnerRadius` | Distance where blending begins |
| `Realtime` | Enable real-time cubemap updates |
| `RealtimeUpdateInterval` | Time between real-time captures |

### LightProbeGridSpawnerComponent

Automatically generates a grid of light probes at runtime.

```csharp
var spawner = sceneNode.AddComponent<LightProbeGridSpawnerComponent>();

// Grid configuration
spawner.ProbeCounts = new IVector3(4, 2, 4); // 4x2x4 = 32 probes
spawner.Spacing = new Vector3(5.0f, 3.0f, 5.0f); // Meters between probes
spawner.Offset = Vector3.Zero; // Local offset from node position

// Probe defaults applied to spawned probes
spawner.IrradianceResolution = 32;
spawner.InfluenceShape = LightProbeComponent.EInfluenceShape.Sphere;
spawner.InfluenceSphereOuterRadius = 8.0f;
```

The spawner creates child nodes with `LightProbeComponent` instances when the scene enters play mode and cleans them up when play ends.

## Influence Regions

### Sphere Influence

Spherical falloff with inner/outer radius:

```csharp
probe.InfluenceShape = LightProbeComponent.EInfluenceShape.Sphere;
probe.InfluenceSphereInnerRadius = 2.0f;  // Full intensity inside
probe.InfluenceSphereOuterRadius = 10.0f; // Zero intensity outside
// Smooth blend between inner and outer
```

### Box Influence

Axis-aligned box with inner/outer extents:

```csharp
probe.InfluenceShape = LightProbeComponent.EInfluenceShape.Box;
probe.InfluenceBoxInnerExtents = new Vector3(2.0f, 2.0f, 2.0f);
probe.InfluenceBoxOuterExtents = new Vector3(5.0f, 3.0f, 5.0f);
probe.InfluenceOffset = Vector3.Zero; // Offset from probe center
```

## Parallax Correction

For indoor scenes, enable parallax-corrected cubemaps to account for the probe's position relative to walls:

```csharp
probe.ParallaxCorrectionEnabled = true;
probe.ProxyBoxCenterOffset = Vector3.Zero;
probe.ProxyBoxHalfExtents = new Vector3(10.0f, 3.0f, 10.0f); // Room size
probe.ProxyBoxRotation = Quaternion.Identity;
```

The proxy box represents the room geometry, and reflections are adjusted to appear as if they originate from the box surfaces rather than infinity.

## HDR Encoding

For baked probes, multiple HDR encoding formats are supported:

```csharp
probe.HdrEncoding = LightProbeComponent.EHdrEncoding.Rgb16f; // Default
// probe.HdrEncoding = LightProbeComponent.EHdrEncoding.RGBM;  // Compressed
// probe.HdrEncoding = LightProbeComponent.EHdrEncoding.RGBE;  // Shared exponent
// probe.HdrEncoding = LightProbeComponent.EHdrEncoding.YCoCg; // Luminance-based
```

| Encoding | Size | Quality | Use Case |
|----------|------|---------|----------|
| `Rgb16f` | 6 bytes/pixel | Highest | Real-time, desktop |
| `RGBM` | 4 bytes/pixel | High | Mobile, compressed |
| `RGBE` | 4 bytes/pixel | High | Compatibility |
| `YCoCg` | Variable | Good | Bandwidth-limited |

## Mip Streaming

For large probe counts, enable on-demand mip streaming:

```csharp
probe.StreamHighMipsOnDemand = true;
probe.TargetMipLevel = 0; // Request full resolution
// StreamedMipLevel indicates current loaded level
```

## Delaunay Triangulation

When multiple probes exist in a scene, the renderer automatically builds a Delaunay triangulation to interpolate between the nearest probes. This provides smooth transitions without manual blend weight configuration.

The triangulation is managed by <xref:XREngine.Scene.Lights3DCollection> and updated when probes are added, removed, or moved.

## Real-Time Capture

For dynamic scenes, enable real-time probe updates:

```csharp
probe.Realtime = true;
probe.RealtimeUpdateInterval = TimeSpan.FromMilliseconds(50); // 20 updates/sec
```

**Performance Considerations:**
- Real-time capture is expensive (6 render passes per probe per update)
- Use lower `CaptureResolution` for real-time probes (128-256)
- Stagger update intervals across multiple probes
- Consider using only one or two real-time probes for hero objects

## Debug Visualization

Enable debug rendering in the editor:

```csharp
probe.AutoShowPreviewOnSelect = true;
probe.RenderInfluenceOnSelection = true;
probe.PreviewDisplay = LightProbeComponent.ERenderPreview.Irradiance;
```

Preview modes:
- `Environment` - Raw cubemap capture
- `Irradiance` - Processed diffuse lighting
- `Prefilter` - Glossy reflection map

## Baking Workflow

1. **Place Probes**: Add `LightProbeComponent` instances at key locations or use `LightProbeGridSpawnerComponent` for automatic placement.

2. **Configure Influence**: Set up influence shapes to cover the playable area with appropriate overlap.

3. **Capture**: In the editor, use the "Bake Light Probes" command or trigger captures programmatically:
   ```csharp
   probe.CaptureEnvironment();
   ```

4. **Process**: The engine automatically generates irradiance and prefiltered maps after capture.

5. **Save**: Probe data is serialized with the scene and reloaded automatically.

## API Reference

- <xref:XREngine.Components.Capture.Lights.LightProbeComponent> - Main probe component
- <xref:XREngine.Components.Capture.LightProbeGridSpawnerComponent> - Grid spawner
- <xref:XREngine.Scene.Lights3DCollection.LightProbeCell> - Triangulation cell

## See Also

- [Global Illumination Overview](global-illumination.md)
- [Light Volumes](light-volumes.md) - Alternative volumetric GI
- [Radiance Cascades](radiance-cascades.md) - Cascaded GI volumes
