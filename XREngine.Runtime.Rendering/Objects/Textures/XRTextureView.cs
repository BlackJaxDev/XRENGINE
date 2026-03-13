using MemoryPack;
using System;
using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    [MemoryPackable]
        public partial class XRTextureView<T>(
        T viewedTexture,
        uint minLevel,
        uint numLevels,
        uint minLayer,
        uint numLayers,
        ESizedInternalFormat internalFormat) 
        : XRTextureViewBase(minLevel, numLevels, minLayer, numLayers, internalFormat) where T : XRTexture
    {
        private T _viewedTexture = viewedTexture;
        public T ViewedTexture
        {
            get => _viewedTexture;
            set => SetField(ref _viewedTexture, value);
        }

        protected override void OnPropertyChanged<T2>(string? propName, T2 prev, T2 field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(ViewedTexture):
                    OnViewedTextureChanged();
                    break;
            }
        }

        public override ETextureTarget TextureTarget => throw new NotSupportedException("Texture view requires a concrete subtype for target.");

        public override uint MaxDimension => ViewedTexture.MaxDimension;

        public override Vector3 WidthHeightDepth => ViewedTexture.WidthHeightDepth;

        public override bool HasAlphaChannel => ViewedTexture.HasAlphaChannel;

        public override XRTexture GetViewedTexture()
            => ViewedTexture;
    }
}
