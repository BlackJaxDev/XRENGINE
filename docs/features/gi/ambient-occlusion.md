# Ambient Occlusion

Ambient Occlusion (AO) simulates the soft shadows that occur in crevices, corners, and areas where surfaces are close together. XREngine provides multiple AO techniques with varying quality and performance characteristics.

## AO Types

The engine supports seven ambient occlusion algorithms:

| Type | Description | Performance | Quality |
|------|-------------|-------------|---------|
| `ScreenSpace` | Classic SSAO from depth buffer | ⭐⭐⭐⭐⭐ | ⭐⭐⭐ |
| `HorizonBased` | HBAO-style horizon tracing | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| `HorizonBasedPlus` | Enhanced HBAO+ algorithm | ⭐⭐⭐ | ⭐⭐⭐⭐ |
| `ScalableAmbientObscurance` | SAO algorithm | ⭐⭐⭐⭐ | ⭐⭐⭐ |
| `MultiScaleVolumetricObscurance` | MSVO for large-scale AO | ⭐⭐⭐ | ⭐⭐⭐⭐ |
| `MultiViewAmbientOcclusion` | Uses multiple depth views | ⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| `SpatialHashRaytraced` | Ray-marched with spatial hashing | ⭐⭐ | ⭐⭐⭐⭐⭐ |

## Configuration

AO is configured through the camera's post-process settings:

```csharp
var camera = GetComponent<CameraComponent>();
var aoSettings = camera.PostProcessSettings.Get<AmbientOcclusionSettings>();

// Enable and select type
aoSettings.Enabled = true;
aoSettings.Type = AmbientOcclusionSettings.EType.ScreenSpace;

// Common settings
aoSettings.Intensity = 1.0f;    // Overall darkness (0-3)
aoSettings.Radius = 2.0f;       // World-space effect radius
aoSettings.Bias = 0.05f;        // Depth bias to prevent self-occlusion
aoSettings.Power = 1.4f;        // Contrast adjustment

// Type-specific settings (see below)
```

## Common Settings

All AO types share these core parameters:

| Property | Range | Description |
|----------|-------|-------------|
| `Enabled` | bool | Enable/disable AO |
| `Intensity` | 0-3 | Overall darkness strength |
| `Radius` | 0.1-10 | Maximum occlusion distance (world units) |
| `Bias` | 0-0.5 | Depth bias for self-occlusion prevention |
| `Power` | 0.5-3 | Contrast curve: `pow(ao, power)` |

## Screen-Space AO

The default, fast screen-space technique:

```csharp
aoSettings.Type = AmbientOcclusionSettings.EType.ScreenSpace;

// SSAO-specific
aoSettings.Samples = 64;           // Sample count (16-128)
aoSettings.Rings = 4.0f;           // Sampling pattern rings
aoSettings.ResolutionScale = 1.0f; // Render at lower resolution
aoSettings.Iterations = 1;         // Denoising blur passes
```

## Horizon-Based AO

HBAO traces the horizon in screen space for more accurate occlusion:

```csharp
aoSettings.Type = AmbientOcclusionSettings.EType.HorizonBased;

// HBAO-specific
aoSettings.Distance = 1.0f;          // Ray march distance
aoSettings.DistanceIntensity = 1.0f; // Falloff rate
aoSettings.Thickness = 0.5f;         // Occluder thickness assumption
```

## Horizon-Based Plus (HBAO+)

Enhanced version with improved quality:

```csharp
aoSettings.Type = AmbientOcclusionSettings.EType.HorizonBasedPlus;
// Same settings as HBAO with additional internal optimizations
```

## Scalable Ambient Obscurance

SAO algorithm optimized for scalability:

```csharp
aoSettings.Type = AmbientOcclusionSettings.EType.ScalableAmbientObscurance;

// SAO-specific
aoSettings.SamplesPerPixel = 3.0f; // Samples per pixel
```

## Multi-Scale Volumetric Obscurance

MSVO handles both small and large-scale occlusion:

```csharp
aoSettings.Type = AmbientOcclusionSettings.EType.MultiScaleVolumetricObscurance;

// MSVO automatically handles multiple scales
aoSettings.SecondaryRadius = 1.6f; // Secondary (larger) radius
```

## Multi-View AO

Uses information from multiple depth views for improved accuracy:

```csharp
aoSettings.Type = AmbientOcclusionSettings.EType.MultiViewAmbientOcclusion;

// Multi-view specific
aoSettings.MultiViewBlend = 0.6f;  // Blend factor between views
aoSettings.MultiViewSpread = 0.5f; // View separation
```

Best for VR/stereo rendering where multiple views are already available.

## Spatial Hash Raytraced

Ray-marched AO using spatial hashing for acceleration:

```csharp
aoSettings.Type = AmbientOcclusionSettings.EType.SpatialHashRaytraced;

// Spatial hash settings
aoSettings.SpatialHashCellSize = 0.07f;    // Grid cell size (smin)
aoSettings.SpatialHashMaxDistance = 1.5f;  // Maximum ray distance
aoSettings.SpatialHashSteps = 8;           // Ray march steps
aoSettings.SpatialHashJitterScale = 0.35f; // Sample jittering
```

Provides the highest quality but is computationally expensive.

## Denoising

All AO types support spatial denoising:

```csharp
aoSettings.Iterations = 2;     // Blur passes
aoSettings.LumaPhi = 4.0f;     // Luminance edge sensitivity
aoSettings.DepthPhi = 4.0f;    // Depth edge sensitivity
aoSettings.NormalPhi = 64.0f;  // Normal edge sensitivity
```

Higher phi values = more aggressive denoising (softer edges).

## Performance Tips

### Resolution Scaling
```csharp
aoSettings.ResolutionScale = 0.5f; // Half resolution
// Uses bilateral upscaling to preserve edges
```

### Sample Reduction
```csharp
aoSettings.Samples = 32;  // Reduce from default 64
aoSettings.Rings = 3.0f;  // Fewer sampling rings
```

### Type Selection by Platform

| Platform | Recommended Type |
|----------|------------------|
| High-end Desktop | `SpatialHashRaytraced` or `MultiViewAmbientOcclusion` |
| Mid-range Desktop | `HorizonBasedPlus` |
| VR | `MultiViewAmbientOcclusion` (reuses stereo views) |
| Mobile | `ScreenSpace` at 0.5x resolution |

## Visual Debugging

To visualize raw AO output:

```csharp
// In editor, enable AO debug view in camera settings
// Or output AO texture directly for inspection
```

## Combining with GI

AO is applied after global illumination to add contact shadows:

```
Final Color = BaseColor × (Ambient + GI) × AO + DirectLighting
```

Reduce AO intensity when using high-quality GI that already captures occlusion:

```csharp
if (usingRadianceCascades || usingReSTIR)
    aoSettings.Intensity = 0.5f; // Softer AO to complement GI
```

## API Reference

- <xref:XREngine.Rendering.AmbientOcclusionSettings> - Configuration class
- <xref:XREngine.Rendering.AmbientOcclusionSettings.EType> - AO type enumeration

## See Also

- [Global Illumination Overview](global-illumination.md)
- Post-Processing (coming soon) - Other camera effects
