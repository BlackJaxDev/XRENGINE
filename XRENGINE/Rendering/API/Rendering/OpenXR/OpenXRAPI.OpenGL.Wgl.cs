using System.Runtime.InteropServices;

public unsafe partial class OpenXRAPI
{
    [DllImport("opengl32.dll")]
    private static extern nint wglGetCurrentContext();

    [DllImport("opengl32.dll")]
    private static extern nint wglGetCurrentDC();
}
