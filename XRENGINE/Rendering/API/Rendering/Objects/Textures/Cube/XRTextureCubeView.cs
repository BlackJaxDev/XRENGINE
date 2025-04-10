using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    public class XRTextureCubeView(
        XRTextureCube viewedTexture,
        uint minLevel,
        uint numLevels,
        uint minLayer,
        uint numLayers,
        EPixelInternalFormat internalFormat,
        bool array,
        bool view2D) : XRTextureView<XRTextureCube>(viewedTexture, minLevel, numLevels, minLayer, numLayers, internalFormat)
    {
        private bool _array = array;
        public bool Array
        {
            get => _array;
            set => SetField(ref _array, value);
        }
        private bool _view2D = view2D;
        public bool View2D
        {
            get => _view2D;
            set => SetField(ref _view2D, value);
        }
        public override uint MaxDimension { get; } = 2u;
        public override Vector3 WidthHeightDepth => new(0, 0, 0);
        public override ETextureTarget TextureTarget => ETextureTarget.TextureCubeMap;
    }
}
