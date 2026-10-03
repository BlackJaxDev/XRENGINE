using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

/// <summary>Backend lowering context passed through the ordinary authored draw preparation.</summary>
internal readonly record struct WebGpuAuthoredIndexedDrawRequest(
    GpuMeshSubmissionRecord Record,
    XRCamera Camera,
    WebGpuRenderProgram Cull,
    WebGpuMeshletWork? MeshletWork,
    WebGpuMeshletGeometry? Geometry,
    WebGpuRenderProgram? FinalizeProgram,
    WebGpuRenderProgram? Refit,
    WebGpuIndirectWork? IndirectWork,
    WebGpuAuthoredIndexedLodSelection Selection);
