# Ambient Occlusion

Ambient Occlusion (AO) simulates the soft shadows that occur in crevices, corners, and areas where surfaces are close together. XREngine provides multiple AO techniques with varying quality and performance characteristics.

## AO Types

The engine currently exposes eight user-facing ambient occlusion modes. One historical SAO enum value remains as a compatibility alias, but it is no longer shown as a separate editor choice because it maps to the same simplified prototype path.

| Type | Description | Performance | Quality |
|------|-------------|-------------|---------|
| `ScreenSpace` | SSAO from the depth buffer | ⭐⭐⭐⭐⭐ | ⭐⭐⭐ |
| `HorizonBased` | HBAO-style horizon tracing | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| `HorizonBasedPlus` | Enhanced HBAO+ algorithm | ⭐⭐⭐ | ⭐⭐⭐⭐ |
| `GroundTruthAmbientOcclusion` | Experimental GTAO gather + denoise path | ⭐⭐⭐ | ⭐⭐⭐ |
| `VoxelAmbientOcclusion` | Planned VXAO family slot backed by an honest neutral stub today | ⭐ | ⭐ |
| `MultiRadiusObscurancePrototype` | Simplified multi-radius obscurance prototype | ⭐⭐⭐ | ⭐⭐ |
| `MultiViewCustom` | Custom multi-view AO path | ⭐⭐⭐ | ⭐⭐⭐ |
| `SpatialHashExperimental` | Ray-marched AO with spatial hashing reuse | ⭐⭐ | ⭐⭐⭐ |

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

Classic HBAO is currently deferred. Keep using HBAO+ unless you are implementing a dedicated reference or debug path:

```csharp
aoSettings.Type = AmbientOcclusionSettings.EType.HorizonBased;

// Currently renders neutral AO and logs that the classic HBAO path is deferred.
```

## Horizon-Based Plus (HBAO+)

Enhanced version with improved quality:

```csharp
aoSettings.Type = AmbientOcclusionSettings.EType.HorizonBasedPlus;
// Same settings as HBAO with additional internal optimizations
```

## Ground-Truth Ambient Occlusion (GTAO)

GTAO now has a real slice-based horizon gather and edge-aware denoise path. It should still be treated as experimental until it is validated against canonical GTAO expectations under motion, thin geometry, and screen-edge stress cases.

```csharp
aoSettings.Type = AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion;

// GTAO-specific settings
aoSettings.GTAOSliceCount = 3;
aoSettings.GTAOStepsPerSlice = 6;
aoSettings.GTAOFalloffStartRatio = 0.4f;
aoSettings.GTAODenoiseEnabled = true;
aoSettings.GTAODenoiseRadius = 4;
aoSettings.GTAODenoiseSharpness = 4.0f;
aoSettings.GTAOUseInputNormals = true;
```

## Voxel Ambient Occlusion (VXAO)

VXAO is now an explicit planned AO family in the default pipeline, but it is not implemented yet. Selecting it currently routes to a neutral stub while preserving a dedicated settings contract for future voxel work.

```csharp
aoSettings.Type = AmbientOcclusionSettings.EType.VoxelAmbientOcclusion;

// Planned VXAO settings contract
aoSettings.VXAOVoxelGridResolution = 128;
aoSettings.VXAOCoverageExtent = 24.0f;
aoSettings.VXAOVoxelOpacityScale = 1.0f;
aoSettings.VXAOTemporalReuseEnabled = true;
aoSettings.VXAOCombineWithScreenSpaceDetail = true;
aoSettings.VXAODetailBlend = 0.35f;
```

Treat VXAO as roadmap scaffolding until the renderer owns a real voxelization plus cone-tracing path for AO.

## Multi-Radius AO Prototype

This is the current simplified obscurance prototype path. It is not a canonical SAO implementation, and the old `ScalableAmbientObscurance` and `MultiScaleVolumetricObscurance` names now exist only as compatibility aliases.

```csharp
aoSettings.Type = AmbientOcclusionSettings.EType.MultiRadiusObscurancePrototype;

// Prototype path currently consumes only the shared bias/intensity settings
aoSettings.Intensity = 1.0f;
aoSettings.Bias = 0.05f;
```

## Multi-View AO (Custom)

Uses information from multiple depth views for improved accuracy:

```csharp
aoSettings.Type = AmbientOcclusionSettings.EType.MultiViewCustom;

// Multi-view specific
aoSettings.MultiViewBlend = 0.6f;  // Blend factor between views
aoSettings.MultiViewSpread = 0.5f; // View separation
```

Best for VR/stereo rendering where multiple views are already available.

## Spatial Hash AO (Experimental)

Ray-marched AO using spatial hashing for acceleration:

```csharp
aoSettings.Type = AmbientOcclusionSettings.EType.SpatialHashExperimental;

// Spatial hash settings
aoSettings.SpatialHashCellSize = 0.07f;    // Grid cell size (smin)
aoSettings.SpatialHashMaxDistance = 1.5f;  // Maximum ray distance
aoSettings.SpatialHashSteps = 8;           // Ray march steps
aoSettings.SpatialHashJitterScale = 0.35f; // Sample jittering
```

Experimental path. Quality and stability still need validation before it should be treated as a production default.

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
| High-end Desktop | `HorizonBasedPlus` |
| Mid-range Desktop | `HorizonBasedPlus` |
| VR | `MultiViewCustom` |
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
