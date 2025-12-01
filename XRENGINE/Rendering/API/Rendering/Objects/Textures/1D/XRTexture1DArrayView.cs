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
        public override uint MaxDimension { get; } = 1u;
        public override Vector3 WidthHeightDepth => new(0, 0, 0);
        public override ETextureTarget TextureTarget => ETextureTarget.Texture1D;
    }
}
