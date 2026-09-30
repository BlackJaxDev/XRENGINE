namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private sealed unsafe partial class OpenGLImGuiMultiViewportController
    {
        private static void LogCallbackException(string callback, Exception error)
            => Debug.RenderingWarningEvery(
                $"OpenGL.ImGui.MultiViewport.{callback}",
                TimeSpan.FromSeconds(2),
                "[ImGuiMultiViewport] {0} failed: {1}",
                callback,
                error.Message);
    }
}
