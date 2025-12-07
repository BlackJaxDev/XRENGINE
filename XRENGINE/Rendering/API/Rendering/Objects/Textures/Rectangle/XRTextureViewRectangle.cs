using MemoryPack;
using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    /// <summary>
    /// View into a rectangle texture. Rectangle textures do not support mipmaps or array layers.
    /// </summary>
    [MemoryPackable]
    public partial class XRTextureViewRectangle(
        XRTextureRectangle viewedTexture,
        ESizedInternalFormat internalFormat)
        : XRTextureView<XRTextureRectangle>(viewedTexture, 0u, 1u, 0u, 1u, internalFormat), IFrameBufferAttachement
    {
        public override uint MaxDimension => ViewedTexture.MaxDimension;

        public override Vector3 WidthHeightDepth => ViewedTexture.WidthHeightDepth;

        public uint Width => ViewedTexture.Width;

        public uint Height => ViewedTexture.Height;

        public override bool HasAlphaChannel => ViewedTexture.HasAlphaChannel;

        public override ETextureTarget TextureTarget => ETextureTarget.TextureRectangle;
    }
}
