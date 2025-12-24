using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    public class XRTexture1DArrayView(
        XRTexture1DArray viewedTexture,
        uint minLevel,
        uint numLevels,
        uint minLayer,
        uint numLayers,
        ESizedInternalFormat internalFormat,
        bool view1D) : XRTextureView<XRTexture1DArray>(viewedTexture, minLevel, numLevels, minLayer, numLayers, internalFormat)
    {
        private bool _view1D = view1D;
        public bool View1D
        {
            get => _view1D;
            set => SetField(ref _view1D, value);
        }
        public uint Width => ViewedTexture.Width;
        public override uint MaxDimension => ViewedTexture.MaxDimension;
        public override Vector3 WidthHeightDepth
            => View1D && NumLayers == 1
                ? new(Width, 1, 1)
                : new(Width, 1, NumLayers);

        public override bool HasAlphaChannel => ViewedTexture.HasAlphaChannel;

        public override ETextureTarget TextureTarget
            => View1D && NumLayers == 1
                ? ETextureTarget.Texture1D
                : ETextureTarget.Texture1DArray;
    }
}
