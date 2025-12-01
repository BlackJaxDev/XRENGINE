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
        public override uint MaxDimension { get; } = 1u;
        public override Vector3 WidthHeightDepth => new(0, 0, 0);
        public override ETextureTarget TextureTarget => ETextureTarget.Texture1D;
    }
}
