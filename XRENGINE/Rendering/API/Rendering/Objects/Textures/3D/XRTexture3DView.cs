using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    public class XRTexture3DView(
        XRTexture3D viewedTexture,
        uint minLevel,
        uint numLevels,
        uint minLayer,
        uint numLayers,
        ESizedInternalFormat internalFormat) : XRTextureView<XRTexture3D>(viewedTexture, minLevel, numLevels, minLayer, numLayers, internalFormat)
    {
        public override uint MaxDimension => ViewedTexture.MaxDimension;

        public override Vector3 WidthHeightDepth => new(ViewedTexture.Width, ViewedTexture.Height, ViewedTexture.Depth);

        public override bool HasAlphaChannel => ViewedTexture.HasAlphaChannel;

        public override ETextureTarget TextureTarget => ETextureTarget.Texture3D;
    }
}
