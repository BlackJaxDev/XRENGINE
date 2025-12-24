using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    public class XRTextureBufferView(
        XRTextureBuffer viewedTexture,
        ESizedInternalFormat internalFormat)
        : XRTextureView<XRTextureBuffer>(viewedTexture, 0u, 1u, 0u, 1u, internalFormat)
    {
        public override uint MaxDimension => ViewedTexture.TexelCount;

        public override Vector3 WidthHeightDepth => new(ViewedTexture.TexelCount, 1, 1);

        public override bool HasAlphaChannel => false;

        public override ETextureTarget TextureTarget => ETextureTarget.TextureBuffer;
    }
}
