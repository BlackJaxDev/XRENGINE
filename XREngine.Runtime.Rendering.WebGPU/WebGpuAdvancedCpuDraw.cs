namespace XREngine.Rendering.WebGPU;

/// <summary>CPU-authored direct draw identity; it never contains a GPU visibility or count readback.</summary>
internal readonly record struct WebGpuAdvancedCpuDraw(uint PayloadIndex, uint VertexCount, int BucketIndex);
