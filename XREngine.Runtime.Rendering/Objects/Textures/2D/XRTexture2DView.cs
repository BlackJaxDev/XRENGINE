using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    /// <summary>
    /// Views into a 2D texture
    /// </summary>
    /// <param name="viewedTexture"></param>
    /// <param name="minLevel"></param>
    /// <param name="numLevels"></param>
    /// <param name="internalFormat"></param>
    /// <param name="array"></param>
    /// <param name="multisample"></param>
    public class XRTexture2DView(
        XRTexture2D viewedTexture,
        uint minLevel,
        uint numLevels,
        ESizedInternalFormat internalFormat,
        bool array,
        bool multisample) : XRTextureView<XRTexture2D>(viewedTexture, minLevel, numLevels, 0u, 1u, internalFormat), IFrameBufferAttachement
    {
        public override uint MaxDimension { get; } = 2;

        public override Vector3 WidthHeightDepth => new(Width, Height, 1);

        private bool _array = array;
        public bool Array
        {
            get => _array;
            set => SetField(ref _array, value);
        }
        private bool _multisample = multisample;
        public bool Multisample 
        {
            get => _multisample;
            set => SetField(ref _multisample, value);
        }

        private EDepthStencilFmt _depthStencilFormat = EDepthStencilFmt.None;
        public EDepthStencilFmt DepthStencilViewFormat 
        {
            get => _depthStencilFormat;
            set => SetField(ref _depthStencilFormat, value);
        }

        public uint Width => ViewedTexture.Width;
        public uint Height => ViewedTexture.Height;

        public override bool HasAlphaChannel => ViewedTexture.HasAlphaChannel;

        public override ETextureTarget TextureTarget { get; } = ETextureTarget.Texture2D;
    }
}
