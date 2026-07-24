using Silk.NET.OpenGL;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer : IRenderSurfaceSampleBackendCapability
{
    /// <inheritdoc />
    public int GetCurrentSurfaceSampleCount()
        => GetInteger(GLEnum.Samples);
}
