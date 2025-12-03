using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    public class XRTexture1DView(
        XRTexture1D viewedTexture,
        uint minLevel,
        uint numLevels,
        uint minLayer,
        uint numLayers,
        ESizedInternalFormat internalFormat,
        bool array) : XRTextureView<XRTexture1D>(viewedTexture, minLevel, numLevels, minLayer, numLayers, internalFormat)
    {
        private bool _array = array;
        public bool Array 
        {
            get => _array;
            set => SetField(ref _array, value);
        }
        public uint Width => ViewedTexture.Width;
        public override uint MaxDimension => ViewedTexture.MaxDimension;
        public override Vector3 WidthHeightDepth => Array ? new(Width, 1, NumLayers) : new(Width, 1, 1);
        public override bool HasAlphaChannel => ViewedTexture.HasAlphaChannel;
        public override ETextureTarget TextureTarget => Array ? ETextureTarget.Texture1DArray : ETextureTarget.Texture1D;
    }
}
