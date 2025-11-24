using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Rendering
{
    public class XRSampler : GenericRenderObject
    {
        private ETexMinFilter _minFilter = ETexMinFilter.Linear;
        private ETexMagFilter _magFilter = ETexMagFilter.Linear;
        private ETexWrapMode _uWrap = ETexWrapMode.Repeat;
        private ETexWrapMode _vWrap = ETexWrapMode.Repeat;
        private ETexWrapMode _wWrap = ETexWrapMode.Repeat;
        private float _minLod = 0f;
        private float _maxLod = 16f;
        private float _lodBias = 0f;
        private bool _enableAnisotropy = true;
        private float _maxAnisotropy = 8f;
        private bool _enableComparison = false;
        private ETextureCompareFunc _compareFunc = ETextureCompareFunc.LessOrEqual;
        private ColorF4 _borderColor = ColorF4.Transparent;

        public ETexMinFilter MinFilter
        {
            get => _minFilter;
            set => SetField(ref _minFilter, value);
        }

        public ETexMagFilter MagFilter
        {
            get => _magFilter;
            set => SetField(ref _magFilter, value);
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

        public ETexWrapMode WWrap
        {
            get => _wWrap;
            set => SetField(ref _wWrap, value);
        }

        public float MinLod
        {
            get => _minLod;
            set => SetField(ref _minLod, value);
        }

        public float MaxLod
        {
            get => _maxLod;
            set => SetField(ref _maxLod, value);
        }

        public float LodBias
        {
            get => _lodBias;
            set => SetField(ref _lodBias, value);
        }

        public bool EnableAnisotropy
        {
            get => _enableAnisotropy;
            set => SetField(ref _enableAnisotropy, value);
        }

        public float MaxAnisotropy
        {
            get => _maxAnisotropy;
            set => SetField(ref _maxAnisotropy, Math.Max(1f, value));
        }

        public bool EnableComparison
        {
            get => _enableComparison;
            set => SetField(ref _enableComparison, value);
        }

        public ETextureCompareFunc CompareFunc
        {
            get => _compareFunc;
            set => SetField(ref _compareFunc, value);
        }

        public ColorF4 BorderColor
        {
            get => _borderColor;
            set => SetField(ref _borderColor, value);
        }

    }
}
