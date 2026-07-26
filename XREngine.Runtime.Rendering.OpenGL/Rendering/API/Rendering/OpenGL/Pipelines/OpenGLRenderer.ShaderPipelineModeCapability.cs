namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer : IShaderPipelineModeBackendCapability
{
    void IShaderPipelineModeBackendCapability.HandleShaderPipelineModeChanged(bool allowShaderPipelines)
        => HandleShaderPipelineModeChanged(allowShaderPipelines);
}
