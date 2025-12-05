using System.ComponentModel;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Rendering
{
    /// <summary>
    /// A sampler object works by defining how to read a texture (filtering, wrapping, etc.)
    /// by linking to a texture unit instead of being tied to a specific texture,
    /// allowing the same texture to be sampled in different ways.
    /// </summary>
    /// <param name="renderer"></param>
    /// <param name="sampler"></param>
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

        /// <summary>
        /// The minification filter to use when sampling the texture.
        /// </summary>
        /// 
        [DisplayName("Min Filter")]
        [Description("The minification filter to use when sampling the texture.")]
        public ETexMinFilter MinFilter
        {
            get => _minFilter;
            set => SetField(ref _minFilter, value);
        }

        /// <summary>
        /// The magnification filter to use when sampling the texture.
        /// </summary>
        [DisplayName("Mag Filter")]
        [Description("The magnification filter to use when sampling the texture.")]
        public ETexMagFilter MagFilter
        {
            get => _magFilter;
            set => SetField(ref _magFilter, value);
        }

        /// <summary>
        /// The wrapping mode to use for the U (or S) texture coordinate.
        /// </summary>
        [DisplayName("U Wrap")]
        [Description("The wrapping mode to use for the U (or S) texture coordinate.")]
        public ETexWrapMode UWrap
        {
            get => _uWrap;
            set => SetField(ref _uWrap, value);
        }

        /// <summary>
        /// The wrapping mode to use for the V (or T) texture coordinate.
        /// </summary>
        [DisplayName("V Wrap")]
        [Description("The wrapping mode to use for the V (or T) texture coordinate.")]
        public ETexWrapMode VWrap
        {
            get => _vWrap;
            set => SetField(ref _vWrap, value);
        }

        /// <summary>
        /// The wrapping mode to use for the W (or R) texture coordinate.
        /// </summary>
        [DisplayName("W Wrap")]
        [Description("The wrapping mode to use for the W (or R) texture coordinate.")]
        public ETexWrapMode WWrap
        {
            get => _wWrap;
            set => SetField(ref _wWrap, value);
        }

        /// <summary>
        /// The minimum level of detail (LOD) to use when sampling the texture.
        /// </summary>
        [DisplayName("Min LOD")]
        [Description("The minimum level of detail (LOD) to use when sampling the texture.")]
        public float MinLod
        {
            get => _minLod;
            set => SetField(ref _minLod, value);
        }

        /// <summary>
        /// The maximum level of detail (LOD) to use when sampling the texture.
        /// </summary>
        [DisplayName("Max LOD")]
        [Description("The maximum level of detail (LOD) to use when sampling the texture.")]
        public float MaxLod
        {
            get => _maxLod;
            set => SetField(ref _maxLod, value);
        }

        /// <summary>
        /// The level of detail (LOD) bias to apply when sampling the texture.
        /// </summary>
        [DisplayName("LOD Bias")]
        [Description("The level of detail (LOD) bias to apply when sampling the texture.")]
        public float LodBias
        {
            get => _lodBias;
            set => SetField(ref _lodBias, value);
        }

        /// <summary>
        /// Enables or disables anisotropic filtering.
        /// </summary>
        [DisplayName("Enable Anisotropy")]
        [Description("Enables or disables anisotropic filtering.")]
        public bool EnableAnisotropy
        {
            get => _enableAnisotropy;
            set => SetField(ref _enableAnisotropy, value);
        }

        /// <summary>
        /// The maximum anisotropy level to use when anisotropic filtering is enabled.
        /// </summary>
        [DisplayName("Max Anisotropy")]
        [Description("The maximum anisotropy level to use when anisotropic filtering is enabled.")]
        public float MaxAnisotropy
        {
            get => _maxAnisotropy;
            set => SetField(ref _maxAnisotropy, Math.Max(1f, value));
        }

        /// <summary>
        /// Enables or disables comparison mode for the sampler.
        /// </summary>
        [DisplayName("Enable Comparison")]
        [Description("Enables or disables comparison mode for the sampler.")]
        public bool EnableComparison
        {
            get => _enableComparison;
            set => SetField(ref _enableComparison, value);
        }

        /// <summary>
        /// The comparison function to use when comparison mode is enabled.
        /// </summary>
        [DisplayName("Compare Function")]
        [Description("The comparison function to use when comparison mode is enabled.")]
        public ETextureCompareFunc CompareFunc
        {
            get => _compareFunc;
            set => SetField(ref _compareFunc, value);
        }

        /// <summary>
        /// The border color to use when the wrap mode is set to ClampToBorder.
        /// </summary>
        [DisplayName("Border Color")]
        [Description("The border color to use when the wrap mode is set to ClampToBorder.")]
        public ColorF4 BorderColor
        {
            get => _borderColor;
            set => SetField(ref _borderColor, value);
        }

    }
}
