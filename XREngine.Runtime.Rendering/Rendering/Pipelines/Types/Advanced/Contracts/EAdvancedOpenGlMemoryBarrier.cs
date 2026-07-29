namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral names for the OpenGL memory-barrier bits required by an advanced boundary.
/// The OpenGL backend lowers these values to the corresponding GL barrier flags.
/// </summary>
[Flags]
public enum EAdvancedOpenGlMemoryBarrier
{
    None = 0,
    VertexAttributeArray = 1 << 0,
    ElementArray = 1 << 1,
    Command = 1 << 2,
    TextureFetch = 1 << 3,
    ShaderImageAccess = 1 << 4,
    ShaderStorage = 1 << 5,
    FrameBuffer = 1 << 6,
}
