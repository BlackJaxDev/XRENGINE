using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private readonly HashSet<ulong> _residentBindlessTextureHandles = [];

    public bool SupportsBindlessTextureHandles => ARBBindlessTexture is not null;

    public bool TryGetResidentBindlessTextureHandle(XRTexture texture, out ulong handle)
    {
        handle = 0ul;
        if (ARBBindlessTexture is null)
            return false;

        if (GetOrCreateAPIRenderObject(texture, generateNow: true) is not GLObjectBase glObject)
            return false;

        if (glObject is IGLTexture glTexture)
        {
            if (glTexture.TextureTarget != ETextureTarget.Texture2D)
                return false;

            glTexture.Bind();
        }

        uint textureId = glObject.BindingId;
        if (textureId == GLObjectBase.InvalidBindingId || textureId == 0u || !Api.IsTexture(textureId))
            return false;

        handle = ARBBindlessTexture.GetTextureHandle(textureId);
        if (handle == 0ul)
            return false;

        if (!ARBBindlessTexture.IsTextureHandleResident(handle))
            ARBBindlessTexture.MakeTextureHandleResident(handle);

        _residentBindlessTextureHandles.Add(handle);
        return true;
    }

    public void ReleaseResidentBindlessTextureHandle(ulong handle)
    {
        if (handle == 0ul || ARBBindlessTexture is null || !_residentBindlessTextureHandles.Remove(handle))
            return;

        if (ARBBindlessTexture.IsTextureHandleResident(handle))
            ARBBindlessTexture.MakeTextureHandleNonResident(handle);
    }
}
