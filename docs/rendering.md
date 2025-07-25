# Rendering System

XRENGINE features a modern, multi-threaded rendering system with support for multiple graphics APIs and advanced rendering techniques optimized for VR and XR applications.

## Overview

The rendering system is built around a parallel architecture with separate threads for collecting visible objects and rendering, ensuring optimal performance for VR applications.

## Architecture

### Multi-Threaded Rendering
```
Update Thread → Transform Snapshot → Collect Visible Thread → Render Thread
     ↓              ↓                      ↓                    ↓
  Scene Updates  World Transforms    Frustum Culling      Graphics API
  Component Ticks Depth Sorted        LOD Selection       Shader Execution
```

### Thread Responsibilities

#### Collect Visible Thread
- Frustum culling of scene objects
- LOD (Level of Detail) selection
- Render command generation
- Material and shader preparation

#### Render Thread
- Graphics API calls (OpenGL/Vulkan)
- Shader execution
- Buffer management
- Frame presentation

## Graphics APIs

### OpenGL 4.6 (Fully Supported)
- Modern OpenGL with compute shaders
- OVR MultiView for VR optimization
- NVIDIA stereo extensions
- Advanced shader pipeline

### Vulkan (In Development)
- Modern low-overhead API
- Parallel rendering support
- Advanced memory management
- Cross-platform compatibility

### DirectX 12 (Planned)
- Windows-specific optimization
- Advanced GPU features
- Ray tracing support (future)

## Rendering Pipeline

### Default Render Pipeline
The engine uses a deferred rendering pipeline optimized for VR:

1. **Geometry Pass**: Render scene geometry to G-buffer
2. **Lighting Pass**: Calculate lighting using G-buffer data
3. **Post-Processing**: Apply effects like bloom, SSAO, etc.
4. **UI Pass**: Render UI elements

### VR-Specific Optimizations

#### Single-Pass Stereo
- Render both eyes in a single pass
- Reduces draw calls by 50%
- Uses OVR MultiView or NVIDIA stereo extensions

#### Foveated Rendering
- Support for eye-tracking based rendering
- Reduces GPU load in peripheral vision
- Maintains quality in focus area

#### Parallel Eye Rendering
- Vulkan-based parallel rendering
- Simultaneous rendering for both eyes
- Improved performance on multi-queue GPUs

## Shader System

### Shader Types
- **Vertex Shaders**: Transform vertices and pass data to fragment shaders
- **Fragment Shaders**: Calculate pixel colors and effects
- **Compute Shaders**: GPU-accelerated calculations for physics and animation
- **Geometry Shaders**: Generate additional geometry
- **Tessellation Shaders**: Dynamic mesh subdivision

### Built-in Shaders

#### Scene3D Shaders
- PBR (Physically Based Rendering) materials
- Deferred lighting
- Shadow mapping
- Normal mapping

#### Compute Shaders
- **PhysicsChain.comp**: GPU-accelerated physics chains
- **Skinning.comp**: GPU-based skeletal animation
- **ParticleSystem.comp**: GPU particle simulation

### Shader Extensions
```glsl
// OVR MultiView for VR
#extension GL_OVR_multiview2 : require

// NVIDIA stereo rendering
#extension GL_NV_stereo_view_rendering : require

// Compute shader support
#version 450
layout(local_size_x = 256) in;
```

## Materials and Textures

### Material System
- PBR workflow with metallic/roughness model
- Support for various texture types
- Material instances for performance
- Dynamic material properties

### Texture Types
- **Albedo**: Base color and transparency
- **Normal**: Surface normal information
- **Metallic/Roughness**: PBR material properties
- **Emissive**: Self-illumination
- **Height**: Displacement mapping
- **AO**: Ambient occlusion

### Texture Formats
- **Compressed**: BC1-7, ASTC for mobile
- **HDR**: EXR, HDR for high dynamic range
- **Cubemaps**: Environment maps and reflections
- **Arrays**: Multiple textures in single resource

## Rendering Components

### IRenderable Interface
Components that can be rendered implement this interface:

```csharp
public interface IRenderable
{
    RenderInfo[] RenderedObjects { get; }
}
```

### RenderInfo
Contains information about how to render an object:

```csharp
public class RenderInfo3D : RenderInfo
{
    public RenderCommandMethod3D RenderMethod { get; }
    public EDefaultRenderPass RenderPass { get; }
}
```

### Render Commands
Commands are generated by the collect visible thread and executed by the render thread:

```csharp
public class RenderCommand
{
    public XRMesh? Mesh { get; set; }
    public XRMaterial? Material { get; set; }
    public Matrix4x4 Transform { get; set; }
    public int RenderPass { get; set; }
}
```

## Performance Features

### Frustum Culling
- Hierarchical frustum culling
- Occlusion culling support
- Spatial partitioning (Octree/Quadtree)

### LOD System
- Automatic LOD selection based on distance
- Smooth LOD transitions
- GPU instancing for similar objects

### Memory Management
- Efficient texture streaming
- Buffer pooling
- Memory defragmentation

### GPU Optimization
- Draw call batching
- State sorting
- Shader program caching

## VR Rendering

### Stereo Rendering
- Separate view matrices for each eye
- Interpupillary distance (IPD) adjustment
- Lens distortion correction

### Time Warp
- Asynchronous time warp for smooth VR
- Prediction-based head tracking
- Reduced motion sickness

### Performance Monitoring
- Frame timing analysis
- GPU/CPU profiling
- VR-specific metrics

## Configuration

### Graphics Settings
```json
{
  "RenderLibrary": "OpenGL",
  "TargetFramesPerSecond": 90,
  "VSync": false,
  "StereoRendering": true,
  "MultiViewRendering": true,
  "FoveatedRendering": false,
  "ShadowQuality": "High",
  "AntiAliasing": "MSAA_4x"
}
```

### Quality Presets
- **Low**: Mobile/VR performance
- **Medium**: Balanced quality/performance
- **High**: Desktop quality
- **Ultra**: Maximum quality settings

## Debugging and Profiling

### Debug Visualization
- Wireframe rendering
- Bounding box display
- Normal visualization
- Performance overlays

### Profiling Tools
- Frame time analysis
- Draw call counting
- Memory usage tracking
- GPU utilization monitoring

## Best Practices

### Performance
- Use LOD systems for complex models
- Minimize draw calls through batching
- Use appropriate texture compression
- Profile regularly on target hardware

### VR Development
- Maintain 90+ FPS for VR
- Use single-pass stereo when possible
- Test on actual VR hardware
- Consider foveated rendering for performance

### Shader Development
- Use compute shaders for heavy calculations
- Minimize texture fetches
- Use appropriate precision qualifiers
- Test on multiple GPU architectures

## Example: Custom Shader

```glsl
#version 450

layout(location = 0) in vec3 Position;
layout(location = 1) in vec3 Normal;
layout(location = 2) in vec2 TexCoord;

layout(location = 0) out vec2 FragTexCoord;
layout(location = 1) out vec3 FragNormal;
layout(location = 2) out vec3 FragWorldPos;

uniform mat4 ModelViewProjection;
uniform mat4 Model;
uniform mat3 NormalMatrix;

void main()
{
    FragTexCoord = TexCoord;
    FragNormal = NormalMatrix * Normal;
    FragWorldPos = (Model * vec4(Position, 1.0)).xyz;
    
    gl_Position = ModelViewProjection * vec4(Position, 1.0);
}
```

## Related Documentation
- [Component System](components.md)
- [Scene System](scene.md)
- [Physics System](physics.md)
- [VR Development](vr-development.md) 