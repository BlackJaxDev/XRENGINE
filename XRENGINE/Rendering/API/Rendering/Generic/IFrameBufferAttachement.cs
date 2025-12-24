using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    public interface IFrameBufferAttachement
    {
        uint Width { get; }
        uint Height { get; }

        void AttachToFBO(XRFrameBuffer target, int mipLevel = 0);
        void DetachFromFBO(XRFrameBuffer target, int mipLevel = 0);
        void AttachToFBO(XRFrameBuffer target, EFrameBufferAttachment attachment, int mipLevel = 0);
        void DetachFromFBO(XRFrameBuffer target, EFrameBufferAttachment attachment, int mipLevel = 0);
    }
}
