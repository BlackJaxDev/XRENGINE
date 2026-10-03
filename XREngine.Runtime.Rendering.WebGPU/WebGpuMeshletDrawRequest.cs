using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

/// <summary>Backend lowering context passed through the ordinary authored draw preparation.</summary>
internal readonly record struct WebGpuMeshletDrawRequest(
    GpuMeshSubmissionRecord Record,
    WebGpuMeshletWork Work,
    WebGpuMeshletGeometry Geometry,
    XRCamera Camera,
    WebGpuRenderProgram Cull,
    WebGpuRenderProgram FinalizeProgram,
    WebGpuRenderProgram Refit);
