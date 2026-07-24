using Silk.NET.OpenGL;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer : IUiFramebufferInteropBackendCapability
{
    public bool TryGetUiFramebufferInterop(
        XRFrameBuffer frameBuffer,
        out UiFramebufferInteropInfo interopInfo)
    {
        if (GenericToAPI<GLFrameBuffer>(frameBuffer) is not { } glFrameBuffer)
        {
            interopInfo = default;
            return false;
        }

        interopInfo = new UiFramebufferInteropInfo(
            glFrameBuffer.BindingId,
            glFrameBuffer.GetAttachmentParameter(
                GLEnum.ColorAttachment0,
                GLEnum.FramebufferAttachmentStencilSize));
        return true;
    }
}
