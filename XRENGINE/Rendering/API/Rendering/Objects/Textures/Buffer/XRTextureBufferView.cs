using Assimp;
using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    public class XRTextureBufferView(
        XRTextureBuffer viewedTexture,
        uint minLevel,
        uint numLevels,
        uint minLayer,
        uint numLayers,
        EPixelInternalFormat internalFormat) : XRTextureView<XRTextureBuffer>(viewedTexture, minLevel, numLevels, minLayer, numLayers, internalFormat)
    {
        public override uint MaxDimension { get; } = 0;

        public override Vector3 WidthHeightDepth => new(0, 0, 0);

        public override ETextureTarget TextureTarget => ETextureTarget.TextureBuffer;
    }
}
