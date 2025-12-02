using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    public abstract class XRTextureViewBase(
        uint minLevel,
        uint numLevels,
        uint minLayer,
        uint numLayers,
        ESizedInternalFormat internalFormat) : XRTexture
    {
        private ETexMagFilter _magFilter = ETexMagFilter.Linear;
        private ETexMinFilter _minFilter = ETexMinFilter.Linear;
        private ETexWrapMode _uWrap = ETexWrapMode.Repeat;
        private ETexWrapMode _vWrap = ETexWrapMode.Repeat;
        private float _lodBias = 0.0f;
        private ESizedInternalFormat _internalFormat = internalFormat;
        private uint _numLayers = numLayers;
        private uint _minLayer = minLayer;
        private uint _numLevels = numLevels;
        private uint _minLevel = minLevel;

        public uint MinLevel
        {
            get => _minLevel;
            set => SetField(ref _minLevel, value);
        }
        public uint NumLevels
        {
            get => _numLevels;
            set => SetField(ref _numLevels, value);
        }
        public uint MinLayer
        {
            get => _minLayer;
            set => SetField(ref _minLayer, value);
        }
        public uint NumLayers
        {
            get => _numLayers;
            set => SetField(ref _numLayers, value);
        }
        public ESizedInternalFormat InternalFormat
        {
            get => _internalFormat;
            set => SetField(ref _internalFormat, value);
        }
        public ETexMagFilter MagFilter
        {
            get => _magFilter;
            set => SetField(ref _magFilter, value);
        }
        public ETexMinFilter MinFilter
        {
            get => _minFilter;
            set => SetField(ref _minFilter, value);
        }
        public ETexWrapMode UWrap
        {
            get => _uWrap;
            set => SetField(ref _uWrap, value);
        }
        public ETexWrapMode VWrap
        {
            get => _vWrap;
            set => SetField(ref _vWrap, value);
        }
        public float LodBias
        {
            get => _lodBias;
            set => SetField(ref _lodBias, value);
        }

        public abstract ETextureTarget TextureTarget { get; }

        public abstract XRTexture GetViewedTexture();
        public event Action? ViewedTextureChanged;
        protected void OnViewedTextureChanged()
            => ViewedTextureChanged?.Invoke();
    }
}
