using System.Runtime.InteropServices;

namespace XREngine.Rendering.OpenGL;

internal sealed unsafe partial class OpenGlXrGraphicsBinding
{
    [DllImport("opengl32.dll")]
    private static extern nint wglGetCurrentContext();

    [DllImport("opengl32.dll")]
    private static extern nint wglGetCurrentDC();
}
