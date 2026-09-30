namespace XREngine.Runtime.Bootstrap;

public class UnitTestingOpenGLRenderSettings
{
    public bool AllowProgramPipelines { get; set; } = false;
    public UnitTestingOpenGLShaderLinkingSettings ShaderLinking { get; set; } = new();
}
