# ReSTIR GI

ReSTIR (Reservoir-based Spatio-Temporal Importance Resampling) GI provides the highest quality global illumination using hardware ray tracing. It efficiently samples light paths using NVIDIA's ReSTIR algorithm.

## Overview

ReSTIR GI uses the `GL_NV_ray_tracing` extension to trace rays and resample light contributions across space and time. This provides:

- Physically accurate indirect lighting
- Soft shadows and glossy reflections
- Multi-bounce global illumination
- Efficient noise reduction through resampling

## Requirements

- **GPU**: NVIDIA RTX series (RTX 20xx or newer)
- **API**: Vulkan rendering mode
- **Driver**: Recent NVIDIA driver with ray tracing support
- **Native DLL**: `RestirGI.Native.dll` must be present

## Initialization

```csharp
// Check hardware support
if (RestirGI.VerifyRayTracingSupport(logSuccess: true))
{
    // Enable ReSTIR GI mode
    Engine.UserSettings.GlobalIlluminationMode = EGlobalIlluminationMode.Restir;
    
    // Initialize the ray tracing pipeline
    if (RestirGI.TryInit())
    {
        Debug.Out("ReSTIR GI initialized successfully");
    }
}
else
{
    Debug.Out("Ray tracing not supported, falling back to compute GI");
}
```

## API

### Static Methods

```csharp
// Check if ray tracing is supported
bool supported = RestirGI.VerifyRayTracingSupport(logSuccess: true);

// Initialize (call once)
bool success = RestirGI.TryInit();

// Bind a ray tracing pipeline
RestirGI.Bind(pipelineHandle);

// Dispatch ray tracing
RestirGI.Dispatch(sbtBuffer, sbtStride, width, height, depth);
```

### TraceParameters

For fine-grained control over ray dispatching:

```csharp
var parameters = new RestirGI.TraceParameters
{
    RaygenBuffer = raygenBufferHandle,
    RaygenOffset = 0,
    RaygenStride = 64,
    MissBuffer = missBufferHandle,
    MissOffset = 0,
    MissStride = 32,
    HitGroupBuffer = hitGroupBufferHandle,
    HitGroupOffset = 0,
    HitGroupStride = 64,
    CallableBuffer = 0,
    CallableOffset = 0,
    CallableStride = 0,
    Width = screenWidth,
    Height = screenHeight,
    Depth = 1
};

RestirGI.Dispatch(in parameters);

// Or use the simplified single-table version:
var simple = RestirGI.TraceParameters.CreateSingleTable(
    sbtBuffer, offset: 0, stride: 64, 
    width, height, depth: 1);
```

## How ReSTIR Works

### 1. Initial Sampling
For each pixel, trace multiple candidate light paths and store them in a "reservoir" - a data structure that maintains the best samples.

### 2. Temporal Reuse
Reproject reservoirs from the previous frame. Paths that were good last frame are likely still good, reducing noise.

### 3. Spatial Reuse
Share samples between neighboring pixels. A bright light visible to one pixel can benefit nearby pixels with similar geometry.

### 4. Final Shading
Use the resampled light paths to compute final indirect lighting with minimal noise.

## Fallback Behavior

When ray tracing is unavailable, the system automatically falls back to compute-based GI:

```csharp
if (!RestirGI.TryInit())
{
    // Automatic fallback to SurfelGI or other compute-based method
    Debug.LogWarning("ReSTIR unavailable, using compute fallback");
}
```

## Performance Characteristics

| Resolution | Approximate Cost |
|------------|------------------|
| 1080p | 4-8ms on RTX 3070 |
| 1440p | 6-12ms on RTX 3070 |
| 4K | 10-20ms on RTX 3070 |

Performance scales with:
- Number of rays per pixel
- Number of resampling passes
- Scene complexity (BVH traversal)
- Material complexity

## Native Bridge

The `RestirGI.Native.dll` provides the OpenGL/Vulkan interop:

```cpp
// Native functions exposed:
extern "C" {
    bool InitReSTIRRayTracingNV();
    bool IsReSTIRRayTracingSupportedNV();
    bool BindReSTIRPipelineNV(uint pipeline);
    bool TraceRaysNVWrapper(...);
}
```

### Building the Native DLL

```bash
cd XRENGINE/Rendering/GI
mkdir build && cd build
cmake ..
cmake --build . --config Release
```

The CMake project (`CMakeLists.txt`) is provided in the `GI` folder.

## Error Handling

```csharp
try
{
    RestirGI.Init(); // Throws if unavailable
}
catch (InvalidOperationException ex)
{
    Debug.LogWarning($"ReSTIR unavailable: {ex.Message}");
    // Handle fallback
}

// Or use Try* methods that return bool:
if (!RestirGI.TryBind(pipeline))
{
    // Handle binding failure
}

if (!RestirGI.TryDispatch(in parameters))
{
    // Handle dispatch failure
}
```

## Comparison with Other GI Modes

| Feature | ReSTIR GI | Surfel GI | Radiance Cascades |
|---------|-----------|-----------|-------------------|
| Quality | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| Performance | ⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐⭐ |
| Dynamic Scenes | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐ |
| Hardware Req | RTX GPU | Any | Any |
| Multi-bounce | ⭐⭐⭐⭐⭐ | ⭐⭐ | ⭐⭐⭐ |

## Current Limitations

- Requires Vulkan rendering mode
- NVIDIA GPUs only (GL_NV_ray_tracing)
- Native DLL must be compiled and present
- Currently mono rendering only (no VR stereo path)

## Files of Interest

- **Managed API**: `XRENGINE/Rendering/GI/RestirGI.cs`
- **Native Source**: `XRENGINE/Rendering/GI/RestirGI.Native.cpp`
- **CMake Build**: `XRENGINE/Rendering/GI/CMakeLists.txt`

## API Reference

- <xref:XREngine.Rendering.GI.RestirGI> - Main ReSTIR API
- <xref:XREngine.Rendering.GI.RestirGI.TraceParameters> - Ray dispatch parameters

## See Also

- [Global Illumination Overview](global-illumination.md)
- [Surfel GI](surfel-gi.md) - Compute-based fallback
- [NVIDIA ReSTIR Paper](https://research.nvidia.com/publication/2020-07_spatiotemporal-reservoir-resampling-real-time-ray-tracing-dynamic-direct) - Academic reference
