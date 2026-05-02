using Silk.NET.OpenGL;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL;

public partial class GLTexture2D
{
    internal bool TryPushBaseLevelAndGenerateMipmapsForTextureStreamingCacheCook(out string failure)
    {
        failure = string.Empty;

        if (!Engine.IsRenderThread)
        {
            failure = "Texture streaming cache GPU cook must run on the render thread.";
            return false;
        }

        if (IsPushing)
        {
            failure = "Texture is already pushing data.";
            return false;
        }

        if (Mipmaps is not { Length: > 0 } || Mipmaps[0].Mipmap?.HasData() != true)
        {
            failure = "Texture does not have a CPU-backed base mip.";
            return false;
        }

        try
        {
            IsPushing = true;
            ApplyPendingImmutableStorageRecreate();
            Bind();

            GLEnum glTarget = ToGLEnum(TextureTarget);
            Api.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

            EPixelInternalFormat? internalFormatForce = EnsureStorageAllocated();
            PushMipmap(glTarget, 0, Mipmaps[0], internalFormatForce);

            ClearProgressiveVisibleMipRange();
            FinalizePushData(allowPostPushCallback: false);
            IsInvalidated = false;
            return true;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            Debug.OpenGLException(ex);
            return false;
        }
        finally
        {
            if (IsPushing)
            {
                IsPushing = false;
                Unbind();
            }
        }
    }
}
