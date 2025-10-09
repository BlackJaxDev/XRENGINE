# Mesh SDF Generator

This module provides GPU-accelerated Signed Distance Field (SDF) generation from mesh data using compute shaders.

## Overview

The `MeshSDFGenerator` class uses the `MeshSDFGen.comp` compute shader to generate 3D signed distance fields from mesh geometry. SDFs are useful for:

- **Collision Detection**: Fast distance queries to mesh surfaces
- **Ray Marching**: Efficient ray-surface intersection testing
- **Procedural Generation**: Distance-based effects and animations
- **Level of Detail**: Multi-resolution collision representations

## Features

- **GPU-Accelerated**: Uses compute shaders for parallel processing
- **Flexible Resolution**: Configurable 3D texture resolution
- **Custom Bounds**: Support for custom bounding volumes
- **Automatic Padding**: Configurable padding around mesh bounds
- **Memory Efficient**: Direct GPU memory management
- **Thread Safe**: Designed for use in multi-threaded environments

## Quick Start

### Basic Usage

```csharp
using XREngine.Rendering.Compute;

// Create and initialize the SDF generator
var sdfGenerator = new MeshSDFGenerator();
sdfGenerator.Initialize();

// Generate SDF from a mesh
var mesh = GetMeshFromSomewhere();
var resolution = new IVector3(128, 128, 128);
var sdfTexture = sdfGenerator.GenerateSDF(mesh, resolution);

// Use the SDF texture for collision detection, rendering, etc.
if (sdfTexture != null)
{
    // Your SDF is ready to use!
}

// Clean up when done
sdfGenerator.Cleanup();
```

### Using the Example Class

```csharp
// Generate SDF with automatic bounds calculation
var sdfTexture = MeshSDFExample.GenerateSDFFromMesh(mesh);

// Generate SDF with custom bounds
var minBounds = new Vector3(-10, -10, -10);
var maxBounds = new Vector3(10, 10, 10);
var sdfTexture = MeshSDFExample.GenerateSDFWithCustomBounds(mesh, minBounds, maxBounds);

// Generate high-resolution SDF for detailed collision detection
var sdfTexture = MeshSDFExample.GenerateHighResolutionSDF(mesh);

// Generate multiple SDFs for LOD system
var sdfTextures = MeshSDFExample.GenerateMultiResolutionSDFs(mesh);
```

## API Reference

### MeshSDFGenerator

#### Constructor
```csharp
public MeshSDFGenerator()
```

#### Properties
- `XRTexture3D? SDFTexture` - The generated SDF texture
- `bool IsInitialized` - Whether the generator is initialized

#### Methods

##### Initialize()
Initializes the SDF generator with the compute shader and resources.

##### Cleanup()
Cleans up all resources used by the SDF generator.

##### GenerateSDF(XRMesh mesh, IVector3 resolution, float padding = 0.1f)
Generates an SDF from mesh data with automatic bounds calculation.

**Parameters:**
- `mesh` - The mesh to generate SDF from
- `resolution` - 3D texture resolution (x, y, z)
- `padding` - Additional padding around mesh bounds (default: 0.1f)

**Returns:** The generated SDF texture, or null if generation failed

##### GenerateSDF(XRMesh mesh, IVector3 resolution, Vector3 minBounds, Vector3 maxBounds)
Generates an SDF with custom bounds.

**Parameters:**
- `mesh` - The mesh to generate SDF from
- `resolution` - 3D texture resolution (x, y, z)
- `minBounds` - Minimum bounds of the SDF volume
- `maxBounds` - Maximum bounds of the SDF volume

**Returns:** The generated SDF texture, or null if generation failed

## Compute Shader Details

The `MeshSDFGen.comp` compute shader:

- **Work Group Size**: 8x8x8 threads per group
- **Input**: Mesh vertices and triangle indices via SSBOs
- **Output**: 3D texture with signed distance values
- **Algorithm**: Computes minimum signed distance to all triangles for each voxel

### Shader Bindings

- **Binding 0**: `sdfTexture` - Output 3D texture (R32F format)
- **Binding 1**: `Vertices` - Vertex positions SSBO
- **Binding 2**: `Indices` - Triangle indices SSBO

### Uniforms

- `sdfMinBounds` (vec3) - Minimum corner of bounding box
- `sdfMaxBounds` (vec3) - Maximum corner of bounding box  
- `sdfResolution` (ivec3) - 3D texture resolution

## Performance Considerations

### Resolution Guidelines

- **Low Resolution (64³)**: Good for distant objects, LOD systems
- **Medium Resolution (128³)**: Balanced performance and accuracy
- **High Resolution (256³)**: High accuracy for close-up collision detection
- **Very High Resolution (512³)**: Maximum accuracy, high memory usage

### Memory Usage

Memory usage scales with resolution³:
- 64³: ~1MB
- 128³: ~8MB  
- 256³: ~64MB
- 512³: ~512MB

### Performance Tips

1. **Use Appropriate Resolution**: Match resolution to use case
2. **Batch Processing**: Generate multiple SDFs in sequence
3. **LOD System**: Use different resolutions for different distances
4. **Custom Bounds**: Limit SDF volume to necessary area
5. **Reuse Generators**: Keep generator instances for multiple meshes

## Integration Examples

### Component Integration

```csharp
public class SDFCollisionComponent : XRComponent
{
    private MeshSDFGenerator? _sdfGenerator;
    private XRTexture3D? _sdfTexture;
    
    protected internal override void OnComponentActivated()
    {
        _sdfGenerator = new MeshSDFGenerator();
        _sdfGenerator.Initialize();
        
        // Generate SDF when mesh changes
        var mesh = GetMesh();
        if (mesh != null)
        {
            _sdfTexture = _sdfGenerator.GenerateSDF(mesh, new IVector3(128, 128, 128));
        }
    }
    
    protected internal override void OnComponentDeactivated()
    {
        _sdfGenerator?.Cleanup();
        _sdfGenerator = null;
    }
}
```

### Shader Integration

```glsl
// In your fragment shader, sample the SDF texture
uniform sampler3D sdfTexture;
uniform vec3 sdfMinBounds;
uniform vec3 sdfMaxBounds;

float GetDistanceToMesh(vec3 worldPos)
{
    // Convert world position to texture coordinates
    vec3 uvw = (worldPos - sdfMinBounds) / (sdfMaxBounds - sdfMinBounds);
    return texture(sdfTexture, uvw).r;
}
```

## Troubleshooting

### Common Issues

1. **"MeshSDFGenerator must be initialized"**
   - Call `Initialize()` before generating SDFs

2. **"Mesh does not have position buffer"**
   - Ensure mesh has vertex positions loaded

3. **"Mesh does not have index buffer"**
   - Ensure mesh has triangle indices loaded

4. **High memory usage**
   - Reduce resolution or use custom bounds

5. **Slow performance**
   - Lower resolution or batch multiple SDF generations

### Debug Tips

- Check `Debug.LogInfo` and `Debug.LogError` messages
- Verify mesh data is properly loaded
- Monitor GPU memory usage
- Use appropriate resolution for your use case

## Future Enhancements

- **Async Generation**: Non-blocking SDF generation
- **Compression**: SDF texture compression
- **Streaming**: Progressive SDF generation
- **Multi-GPU**: Distributed SDF generation
- **Optimization**: Adaptive resolution based on mesh complexity 