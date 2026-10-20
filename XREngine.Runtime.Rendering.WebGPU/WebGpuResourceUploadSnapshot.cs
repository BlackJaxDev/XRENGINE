namespace XREngine.Rendering.WebGPU;

/// <summary>Immutable CPU bytes retained before the associated physical allocation exists.</summary>
internal sealed record WebGpuResourceUploadSnapshot(int Mip, int Layer, int Width, int Height, byte[] Bytes, int BufferOffset = 0);
