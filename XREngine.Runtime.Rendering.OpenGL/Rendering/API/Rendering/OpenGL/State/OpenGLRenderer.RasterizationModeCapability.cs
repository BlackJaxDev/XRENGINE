using Silk.NET.OpenGL;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer : IRasterizationModeBackendCapability
{
    /// <inheritdoc />
    public void SetWireframeRasterization(bool enabled)
        => RawGL.PolygonMode(GLEnum.FrontAndBack, enabled ? GLEnum.Line : GLEnum.Fill);
}
