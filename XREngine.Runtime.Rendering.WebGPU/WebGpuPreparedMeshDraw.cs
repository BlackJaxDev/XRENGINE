namespace XREngine.Rendering.WebGPU;

/// <summary>One exact authored material/binding snapshot after callbacks and ordered canonical deformation.</summary>
internal readonly record struct WebGpuPreparedMeshDraw(
    WebGpuMaterial Material,
    WebGpuBindingSet Bindings,
    WebGpuMeshDeformation? Deformation);
