using MemoryPack;
using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    [MemoryPackable]
    public partial class XRTextureViewCubeArray(
        XRTextureCubeArray viewedTexture,
        uint minLevel,
        uint numLevels,
        uint minLayer,
        uint numLayers,
        ESizedInternalFormat internalFormat,
        bool array,
        bool view2D) : XRTextureView<XRTextureCubeArray>(viewedTexture, minLevel, numLevels, minLayer, numLayers, internalFormat)
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
        public override uint MaxDimension => ViewedTexture.MaxDimension;
        public override Vector3 WidthHeightDepth
            => View2D
                ? new(ViewedTexture.Extent, ViewedTexture.Extent, 1)
                : new(ViewedTexture.Extent, ViewedTexture.Extent, ViewedTexture.LayerCount);

        public override bool HasAlphaChannel => ViewedTexture.HasAlphaChannel;

        public override ETextureTarget TextureTarget
        {
            get
            {
                if (View2D)
                    return Array ? ETextureTarget.Texture2DArray : ETextureTarget.Texture2D;

                return Array ? ETextureTarget.TextureCubeMapArray : ETextureTarget.TextureCubeMap;
            }
        }
    }
}
