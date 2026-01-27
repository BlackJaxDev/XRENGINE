using MemoryPack;
using System.ComponentModel;
using XREngine.Core.Files;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Models.Materials
{
    /// <summary>
    /// Contains parameters for rendering an object, such as blending and depth testing.
    /// </summary>
    [MemoryPackable(GenerateType.NoGenerate)]
    public partial class RenderingParameters : XRAsset
    {
        private DepthTest _depthTest = new();
        private StencilTest _stencilTest = new();
        private Dictionary<uint, BlendMode>? _blendModesPerDrawBuffer;
        //private float _lineWidth = AbstractRenderer.DefaultLineSize;
        //private float _pointSize = AbstractRenderer.DefaultPointSize;
        private ECullMode _cullMode = ECullMode.Back;
        private EWinding _winding = EWinding.CounterClockwise;
        private bool _writeAlpha = true;
        private bool _writeBlue = true;
        private bool _writeGreen = true;
        private bool _writeRed = true;
        private EUniformRequirements _requiredEngineUniforms = EUniformRequirements.None;
        private BlendMode? _blendModeAllDrawBuffers;
        private bool _excludeFromGpuIndirect;

        [Browsable(false)]
        public bool HasBlending => (BlendModesPerDrawBuffer?.Values.Any(x => x.Enabled == ERenderParamUsage.Enabled) ?? false) || BlendModeAllDrawBuffers?.Enabled == ERenderParamUsage.Enabled;

        public RenderingParameters() { }
        public RenderingParameters(bool defaultBlendEnabled)
        {
            if (!defaultBlendEnabled)
                return;

            BlendModeAllDrawBuffers = BlendMode.EnabledTransparent();
        }

        /// <summary>
        /// The engine can provide built-in uniforms to the shader.
        /// This property allows you to request the engine to provide which uniforms you need.
        /// </summary>
        public EUniformRequirements RequiredEngineUniforms
        {
            get => _requiredEngineUniforms;
            set => SetField(ref _requiredEngineUniforms, value);
        }
        public bool WriteRed
        {
            get => _writeRed;
            set => SetField(ref _writeRed, value);
        }
        public bool WriteGreen
        {
            get => _writeGreen;
            set => SetField(ref _writeGreen, value);
        }
        public bool WriteBlue
        {
            get => _writeBlue;
            set => SetField(ref _writeBlue, value);
        }
        public bool WriteAlpha
        {
            get => _writeAlpha;
            set => SetField(ref _writeAlpha, value);
        }
        /// <summary>
        /// Specifies the winding order of the triangles.
        /// Default is counter-clockwise.
        /// </summary>
        public EWinding Winding
        {
            get => _winding;
            set => SetField(ref _winding, value);
        }
        /// <summary>
        /// Specifies which side(s) of each triangle should be left unrendered, if any.
        /// </summary>
        public ECullMode CullMode
        {
            get => _cullMode;
            set => SetField(ref _cullMode, value);
        }
        //public float PointSize
        //{
        //    get => _pointSize;
        //    set => SetField(ref _pointSize, value);
        //}
        //public float LineWidth
        //{
        //    get => _lineWidth;
        //    set => SetField(ref _lineWidth, value);
        //}
        /// <summary>
        /// Specifies how the depth buffer should be tested.
        /// </summary>
        public DepthTest DepthTest
        {
            get => _depthTest;
            set => SetField(ref _depthTest, value);
        }
        /// <summary>
        /// Specifies how the stencil buffer should be tested.
        /// </summary>
        public StencilTest StencilTest
        {
            get => _stencilTest;
            set => SetField(ref _stencilTest, value);
        }
        /// <summary>
        /// If set, each draw buffer attachment can be blended with a different blend mode.
        /// BlendModeAllDrawBuffers takes precedence over this if both are set.
        /// </summary>
        public Dictionary<uint, BlendMode>? BlendModesPerDrawBuffer
        {
            get => _blendModesPerDrawBuffer;
            set => SetField(ref _blendModesPerDrawBuffer, value);
        }
        /// <summary>
        /// If set, this blend mode will be used for all draw buffers.
        /// Specifies how all color attachments should be blended with this material.
        /// Takes precedence over BlendModesPerDrawBuffer if both are set.
        /// </summary>
        public BlendMode? BlendModeAllDrawBuffers
        {
            get => _blendModeAllDrawBuffers;
            set => SetField(ref _blendModeAllDrawBuffers, value);
        }

        /// <summary>
        /// When true, this material will be rendered using the CPU path even when GPU indirect dispatch is enabled.
        /// Use this for materials that require specialized vertex shaders (e.g., skybox, fullscreen effects)
        /// that cannot use the standard GPU indirect vertex shader which transforms positions via model matrices.
        /// </summary>
        [Category("GPU Dispatch")]
        [DisplayName("Exclude From GPU Indirect")]
        [Description("Forces CPU rendering path for materials with specialized vertex shaders that cannot use GPU indirect dispatch.")]
        public bool ExcludeFromGpuIndirect
        {
            get => _excludeFromGpuIndirect;
            set => SetField(ref _excludeFromGpuIndirect, value);
        }
    }
}
