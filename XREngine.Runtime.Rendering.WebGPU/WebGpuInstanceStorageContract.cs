namespace XREngine.Rendering.WebGPU;

/// <summary>Names a cooked, read-only instance stream and its packed physical stride.</summary>
internal readonly record struct WebGpuInstanceStorageContract(string Name, int StrideBytes, uint MaximumInstances);
