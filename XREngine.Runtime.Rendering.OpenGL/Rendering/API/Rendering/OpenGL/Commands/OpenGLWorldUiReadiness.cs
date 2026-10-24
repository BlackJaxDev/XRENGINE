using XREngine.Rendering.UI;

namespace XREngine.Rendering.OpenGL;

/// <summary>Native warm-up identity, including the selected shader program mode.</summary>
internal sealed record OpenGLWorldUiReadiness(
    UIWorldOutputReadiness Snapshot, int ShaderConfigVersion);
