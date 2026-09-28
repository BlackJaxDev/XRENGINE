namespace XREngine.Rendering;

/// <summary>Portable buffer usages with the WebGPU bit encoding.</summary>
[Flags]
public enum BrowserBufferUsage
{
    CopySource = 4,
    CopyDestination = 8,
    Index = 16,
    Vertex = 32,
    Uniform = 64,
    Storage = 128,
    Indirect = 256
}
