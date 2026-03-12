using System.ComponentModel;
using System.Numerics;
using System.Text.RegularExpressions;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using YamlDotNet.Serialization;
using Color = System.Drawing.Color;

namespace XREngine.Rendering
{
    [XRAssetInspector("XREngine.Editor.AssetEditors.XRMaterialInspector")]
    public class XRMaterial : XRMaterialBase
    {
        private int _opaqueRenderPass = (int)EDefaultRenderPass.OpaqueForward;

        [Browsable(false)]
        [YamlIgnore]
        public IReadOnlyList<XRShader> FragmentShaders => _fragmentShaders;
        [Browsable(false)]
        [YamlIgnore]
        public IReadOnlyList<XRShader> GeometryShaders => _geometryShaders;
        [Browsable(false)]
        [YamlIgnore]
        public IReadOnlyList<XRShader> TessEvalShaders => _tessEvalShaders;
        [Browsable(false)]
        [YamlIgnore]
        public IReadOnlyList<XRShader> TessCtrlShaders => _tessCtrlShaders;
        [Browsable(false)]
        [YamlIgnore]
        public IReadOnlyList<XRShader> VertexShaders => _vertexShaders;

        private readonly List<XRShader> _fragmentShaders = [];
        private readonly List<XRShader> _geometryShaders = [];
        private readonly List<XRShader> _tessEvalShaders = [];
        private readonly List<XRShader> _tessCtrlShaders = [];
        private readonly List<XRShader> _vertexShaders = [];
        private readonly List<XRShader> _meshShaders = [];
        private readonly List<XRShader> _taskShaders = [];
        private readonly List<XRShader> _computeShaders = [];
        private EventList<XRShader> _shaders;

        public XRMaterial()
        {
            _shaders = [];
            PostShadersSet();
        }
        public XRMaterial(params XRShader[] shaders)
        {
            _shaders = [.. shaders];
            PostShadersSet();
        }
        public XRMaterial(IEnumerable<XRShader> shaders)
        {
            _shaders = [.. shaders];
            PostShadersSet();
        }
        public XRMaterial(ShaderVar[] parameters, params XRShader[] shaders) : base(parameters)
        {
            _shaders = [.. shaders];
            PostShadersSet();
        }
        public XRMaterial(ShaderVar[] parameters, IEnumerable<XRShader> shaders) : base(parameters)
        {
            _shaders = [.. shaders];
            PostShadersSet();
        }
        public XRMaterial(XRTexture?[] textures, params XRShader[] shaders) : base(textures)
        {
            _shaders = [.. shaders];
            PostShadersSet();
        }
        public XRMaterial(XRTexture?[] textures, IEnumerable<XRShader> shaders) : base(textures)
        {
            _shaders = [.. shaders];
            PostShadersSet();
        }
        public XRMaterial(ShaderVar[] parameters, XRTexture?[] textures, params XRShader[] shaders) : base(parameters, textures)
        {
            _shaders = [.. shaders];
            PostShadersSet();
        }
        public XRMaterial(ShaderVar[] parameters, XRTexture?[] textures, IEnumerable<XRShader> shaders) : base(parameters, textures)
        {
            _shaders = [.. shaders];
            PostShadersSet();
        }
        public XRMaterial(XRTexture?[] textures, ShaderVar[] parameters, params XRShader[] shaders) : base(parameters, textures)
        {
            _shaders = [.. shaders];
            PostShadersSet();
        }
        public XRMaterial(XRTexture?[] textures, ShaderVar[] parameters, IEnumerable<XRShader> shaders) : base(parameters, textures)
        {
            _shaders = [.. shaders];
            PostShadersSet();
        }

        public EventList<XRShader> Shaders
        {
            get => _shaders;
            set => SetField(ref _shaders, value);
        }

        private EMeshBillboardMode _billboardMode = EMeshBillboardMode.None;
        public EMeshBillboardMode BillboardMode
        {
            get => _billboardMode;
            set => SetField(ref _billboardMode, value);
        }

        private ETransparencyMode _transparencyMode = ETransparencyMode.Opaque;
        public ETransparencyMode TransparencyMode
        {
            get => _transparencyMode;
            set => SetField(ref _transparencyMode, value);
        }

        private float _alphaCutoff = 0.5f;
        public float AlphaCutoff
        {
            get => _alphaCutoff;
            set => SetField(ref _alphaCutoff, value);
        }

        private int _transparentSortPriority;
        public int TransparentSortPriority
        {
            get => _transparentSortPriority;
            set => SetField(ref _transparentSortPriority, value);
        }

        private ETransparencyMode? _transparentTechniqueOverride;
        public ETransparencyMode? TransparentTechniqueOverride
        {
            get => _transparentTechniqueOverride;
            set => SetField(ref _transparentTechniqueOverride, value);
        }

        /// <summary>
        /// Returns the currently rendering pipeline's invalid material.
        /// Returns null if the current rendering pipeline is not set or does not have an invalid material.
        /// </summary>
        public static XRMaterial? InvalidMaterial => Engine.Rendering.State.CurrentRenderingPipeline!.InvalidMaterial;

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
                switch (propName)
                {
                    case nameof(Shaders):
                        PreShadersSet();
                        break;
                }
            return change;
        }
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            switch (propName)
            {
                case nameof(Shaders):
                    PostShadersSet();
                    break;
                case nameof(RenderPass):
                    if (!IsManagedTransparencyRenderPass(RenderPass))
                        _opaqueRenderPass = RenderPass;
                    break;
                case nameof(TransparencyMode):
                    ApplyTransparencyState();
                    break;
                case nameof(TransparentTechniqueOverride):
                    ApplyTransparencyState();
                    break;
                case nameof(AlphaCutoff):
                    SyncAlphaCutoffParameter();
                    break;
            }

            base.OnPropertyChanged(propName, prev, field);
        }

        private static bool IsManagedTransparencyRenderPass(int renderPass)
            => renderPass == (int)EDefaultRenderPass.MaskedForward ||
               renderPass == (int)EDefaultRenderPass.TransparentForward;

        private void PreShadersSet()
        {
            _shaders.PostModified -= ShadersChanged;
            _shaders.PostAnythingAdded -= ShaderAdded;
            _shaders.PostAnythingRemoved -= ShaderRemoved;
        }

        private void PostShadersSet()
        {
            _shaders.PostModified += ShadersChanged;
            _shaders.PostAnythingAdded += ShaderAdded;
            _shaders.PostAnythingRemoved += ShaderRemoved;

            foreach (var shader in _shaders)
                ShaderAdded(shader);
            ShadersChanged();
        }

        private void ShaderRemoved(XRShader item)
        {
            if (item is null)
                return;
            item.Reloaded -= ShaderReloaded;
        }

        private void ShaderAdded(XRShader item)
        {
            if (item is null)
                return;
            item.Reloaded += ShaderReloaded;
        }

        private void ShaderReloaded(XRAsset asset)
            => ShadersChanged();

        //[TPostDeserialize]
        internal void ShadersChanged()
        {
            _fragmentShaders.Clear();
            _geometryShaders.Clear();
            _tessCtrlShaders.Clear();
            _tessEvalShaders.Clear();
            _vertexShaders.Clear();
            _meshShaders.Clear();
            _taskShaders.Clear();
            _computeShaders.Clear();

            foreach (var shader in Shaders)
                if (shader != null)
                {
                    switch (shader.Type)
                    {
                        case EShaderType.Vertex:
                            _vertexShaders.Add(shader);
                            break;
                        case EShaderType.Fragment:
                            _fragmentShaders.Add(shader);
                            break;
                        case EShaderType.Geometry:
                            _geometryShaders.Add(shader);
                            break;
                        case EShaderType.TessControl:
                            _tessCtrlShaders.Add(shader);
                            break;
                        case EShaderType.TessEvaluation:
                            _tessEvalShaders.Add(shader);
                            break;
                        case EShaderType.Mesh:
                            _meshShaders.Add(shader);
                            break;
                        case EShaderType.Task:
                            _taskShaders.Add(shader);
                            break;
                        case EShaderType.Compute:
                            _computeShaders.Add(shader);
                            break;
                    }
                }

            ShaderPipelineProgram?.Destroy();
            ShaderPipelineProgram = Engine.Rendering.Settings.AllowShaderPipelines
                ? new XRRenderProgram(true, true, Shaders.Where(x => x.Type != EShaderType.Vertex))
                : null;

            SyncParametersToShaderUniforms();
            SyncAlphaCutoffParameter();
        }

        public ETransparencyMode GetEffectiveTransparencyMode()
            => TransparentTechniqueOverride ?? TransparencyMode;

        public ETransparencyMode InferTransparencyMode()
        {
            var blend = RenderOptions?.BlendModeAllDrawBuffers;
            bool blendEnabled = blend?.Enabled == ERenderParamUsage.Enabled;
            bool hasAlphaCutoff = Parameter<ShaderFloat>("AlphaCutoff") is not null;
            bool depthWrites = RenderOptions?.DepthTest?.UpdateDepth ?? true;

            if (blendEnabled)
            {
                if (blend!.RgbDstFactor == EBlendingFactor.One && blend.RgbSrcFactor == EBlendingFactor.SrcAlpha)
                    return ETransparencyMode.Additive;

                if (blend.RgbSrcFactor == EBlendingFactor.One && blend.RgbDstFactor == EBlendingFactor.OneMinusSrcAlpha)
                    return ETransparencyMode.PremultipliedAlpha;

                return ETransparencyMode.AlphaBlend;
            }

            if (hasAlphaCutoff && depthWrites)
                return ETransparencyMode.Masked;

            return ETransparencyMode.Opaque;
        }

        public bool IsTransparentLike(ETransparencyMode? mode = null)
        {
            ETransparencyMode value = mode ?? GetEffectiveTransparencyMode();
            return value is not ETransparencyMode.Opaque and not ETransparencyMode.Masked and not ETransparencyMode.AlphaToCoverage;
        }

        private void ApplyTransparencyState()
        {
            ETransparencyMode effectiveMode = GetEffectiveTransparencyMode();
            RenderOptions ??= new RenderingParameters();
            RenderOptions.DepthTest ??= new DepthTest();

            if (effectiveMode is ETransparencyMode.Masked or ETransparencyMode.AlphaToCoverage)
                EnsureAlphaCutoffParameter();

            switch (effectiveMode)
            {
                case ETransparencyMode.Opaque:
                    RenderOptions.BlendModeAllDrawBuffers = BlendMode.Disabled();
                    RenderOptions.DepthTest.Enabled = ERenderParamUsage.Enabled;
                    RenderOptions.DepthTest.UpdateDepth = true;
                    if (IsManagedTransparencyRenderPass(RenderPass))
                        RenderPass = _opaqueRenderPass;
                    break;
                case ETransparencyMode.Masked:
                case ETransparencyMode.AlphaToCoverage:
                    RenderOptions.BlendModeAllDrawBuffers = BlendMode.Disabled();
                    RenderOptions.DepthTest.Enabled = ERenderParamUsage.Enabled;
                    RenderOptions.DepthTest.UpdateDepth = true;
                    RenderPass = (int)EDefaultRenderPass.MaskedForward;
                    break;
                case ETransparencyMode.Additive:
                    RenderOptions.BlendModeAllDrawBuffers = new BlendMode()
                    {
                        Enabled = ERenderParamUsage.Enabled,
                        RgbSrcFactor = EBlendingFactor.SrcAlpha,
                        RgbDstFactor = EBlendingFactor.One,
                        AlphaSrcFactor = EBlendingFactor.SrcAlpha,
                        AlphaDstFactor = EBlendingFactor.One,
                    };
                    RenderOptions.DepthTest.Enabled = ERenderParamUsage.Enabled;
                    RenderOptions.DepthTest.UpdateDepth = false;
                    RenderPass = (int)EDefaultRenderPass.TransparentForward;
                    break;
                case ETransparencyMode.PremultipliedAlpha:
                    RenderOptions.BlendModeAllDrawBuffers = new BlendMode()
                    {
                        Enabled = ERenderParamUsage.Enabled,
                        RgbSrcFactor = EBlendingFactor.One,
                        RgbDstFactor = EBlendingFactor.OneMinusSrcAlpha,
                        AlphaSrcFactor = EBlendingFactor.One,
                        AlphaDstFactor = EBlendingFactor.OneMinusSrcAlpha,
                    };
                    RenderOptions.DepthTest.Enabled = ERenderParamUsage.Enabled;
                    RenderOptions.DepthTest.UpdateDepth = false;
                    RenderPass = (int)EDefaultRenderPass.TransparentForward;
                    break;
                default:
                    RenderOptions.BlendModeAllDrawBuffers = BlendMode.EnabledTransparent();
                    RenderOptions.DepthTest.Enabled = ERenderParamUsage.Enabled;
                    RenderOptions.DepthTest.UpdateDepth = false;
                    RenderPass = (int)EDefaultRenderPass.TransparentForward;
                    break;
            }

            SyncAlphaCutoffParameter();
        }

        private void EnsureAlphaCutoffParameter()
        {
            if (Parameter<ShaderFloat>("AlphaCutoff") is not null)
                return;

            ShaderVar[] parameters = Parameters ?? [];
            Array.Resize(ref parameters, parameters.Length + 1);
            parameters[^1] = new ShaderFloat(AlphaCutoff, "AlphaCutoff");
            Parameters = parameters;
        }

        private void SyncAlphaCutoffParameter()
        {
            var alphaCutoff = Parameter<ShaderFloat>("AlphaCutoff");
            if (alphaCutoff is not null)
                alphaCutoff.SetValue(AlphaCutoff);
        }

        /// <summary>
        /// Regex matching simple uniform declarations (no arrays, no blocks).
        /// Captures group 1 = GLSL type, group 2 = uniform name.
        /// Handles optional precision qualifiers (lowp / mediump / highp).
        /// </summary>
        private static readonly Regex UniformDeclRegex = new(
            @"\buniform\s+(?:(?:lowp|mediump|highp)\s+)?(\w+)\s+(\w+)\s*[;=]",
            RegexOptions.Compiled);

        /// <summary>
        /// Engine-managed uniform names that should never appear in the material
        /// <see cref="XRMaterialBase.Parameters"/> array (they are set automatically
        /// by the render pipeline each frame).
        /// </summary>
        private static readonly HashSet<string> EngineUniformNames =
            new(Enum.GetNames<EEngineUniform>(), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Parses all shader sources for uniform declarations and synchronizes
        /// <see cref="XRMaterialBase.Parameters"/> to match.
        /// <list type="bullet">
        ///   <item>Existing <see cref="ShaderVar"/>s whose name and type still match a declared uniform are preserved (keeping their current value).</item>
        ///   <item>New default-valued <see cref="ShaderVar"/>s are created for any previously undiscovered uniforms.</item>
        ///   <item>Stale entries that no longer correspond to any shader uniform are removed.</item>
        ///   <item>Engine-managed uniforms (see <see cref="EEngineUniform"/>), samplers, and array uniforms are excluded.</item>
        /// </list>
        /// Called automatically from <see cref="ShadersChanged"/>.
        /// </summary>
        private void SyncParametersToShaderUniforms()
        {
            // Nothing to parse → leave Parameters untouched (empty material, deserialization in progress, etc.)
            if (Shaders.Count == 0)
                return;

            Regex uniformRegex = UniformDeclRegex;

            // Track whether ANY shader actually had parseable source text.
            // If no source text is available yet (async load, deserialization in progress),
            // we must leave Parameters untouched to avoid wiping out explicitly provided values.
            bool anySourceParsed = false;

            // Discover uniforms from all shader sources
            var discoveredUniforms = new Dictionary<string, EShaderVarType>(StringComparer.Ordinal);
            foreach (var shader in Shaders)
            {
                if (shader?.Source?.Text is not { Length: > 0 } source)
                    continue;

                anySourceParsed = true;

                foreach (Match match in uniformRegex.Matches(source))
                {
                    string glslType = match.Groups[1].Value;
                    string uniformName = match.Groups[2].Value;

                    // Skip engine-managed uniforms
                    if (EngineUniformNames.Contains(uniformName))
                        continue;

                    // Skip types we can't represent as ShaderVars (samplers, images, etc.)
                    if (!ShaderVar.GlslTypeMap.TryGetValue(glslType, out EShaderVarType varType))
                        continue;

                    // First occurrence wins (same name across shader stages)
                    discoveredUniforms.TryAdd(uniformName, varType);
                }
            }

            // If no shader source text was available, don't touch Parameters.
            // The sync will run again when the shader source is loaded/reloaded
            // (via ShaderReloaded → ShadersChanged).
            if (!anySourceParsed)
                return;

            // Build lookup of existing parameters for fast merge
            var existingByName = new Dictionary<string, ShaderVar>(StringComparer.Ordinal);
            if (Parameters is not null)
                foreach (var p in Parameters)
                    if (p is not null && !string.IsNullOrEmpty(p.Name))
                        existingByName.TryAdd(p.Name, p);

            // Merge: keep matching existing vars (preserving values), create defaults for new, drop stale
            var merged = new List<ShaderVar>(discoveredUniforms.Count);
            foreach (var (name, type) in discoveredUniforms)
            {
                if (existingByName.TryGetValue(name, out ShaderVar? existing) && existing.TypeName == type)
                {
                    merged.Add(existing);
                }
                else
                {
                    ShaderVar? newVar = ShaderVar.CreateForType(type, name);
                    if (newVar is not null)
                        merged.Add(newVar);
                }
            }

            // Only reassign if the set actually changed (avoids unnecessary change-notification noise)
            ShaderVar[] mergedArray = [.. merged];
            ShaderVar[]? current = Parameters;
            if (current is null || current.Length != mergedArray.Length || !current.SequenceEqual(mergedArray))
                Parameters = mergedArray;
        }

        public static XRMaterial CreateUnlitAlphaTextureMaterialForward(XRTexture2D texture)
            => new([texture], ShaderHelper.UnlitAlphaTextureFragForward()!) { RenderPass = (int)EDefaultRenderPass.TransparentForward };

        public static XRMaterial CreateUnlitTextureMaterialForward(XRTexture texture)
            => new([texture], ShaderHelper.UnlitTextureFragForward()!) { RenderPass = (int)EDefaultRenderPass.OpaqueForward };

        public static XRMaterial CreateUnlitTextureMaterialForward()
            => new(ShaderHelper.UnlitTextureFragForward()!) { RenderPass = (int)EDefaultRenderPass.OpaqueForward };

        public static XRMaterial CreateLitTextureMaterial(bool deferred = true)
            => new((deferred ? ShaderHelper.LitTextureFragDeferred() : ShaderHelper.LitTextureFragForward())!) { RenderPass = (int)EDefaultRenderPass.OpaqueForward };

        public static XRMaterial CreateLitTextureMaterial(XRTexture2D texture, bool deferred = true)
            => new([texture], (deferred ? ShaderHelper.LitTextureFragDeferred() : ShaderHelper.LitTextureFragForward())!) { RenderPass = (int)EDefaultRenderPass.OpaqueForward };

        public static XRMaterial CreateLitTextureSilhouettePOMMaterial(
            XRTexture2D albedo,
            XRTexture2D height,
            bool deferred = true,
            float parallaxScale = 0.04f,
            int parallaxMinSteps = 12,
            int parallaxMaxSteps = 48,
            int parallaxRefineSteps = 5,
            float parallaxHeightBias = 0.0f,
            bool parallaxSilhouette = true,
            float forwardSpecularIntensity = 1.0f,
            float forwardShininess = 64.0f)
        {
            XRShader frag = deferred
                ? ShaderHelper.LitTextureSilhouettePOMFragDeferred()
                : ShaderHelper.LitTextureSilhouettePOMFragForward();

            ShaderVar[] parameters =
            [
                new ShaderFloat(parallaxScale, "ParallaxScale"),
                new ShaderInt(parallaxMinSteps, "ParallaxMinSteps"),
                new ShaderInt(parallaxMaxSteps, "ParallaxMaxSteps"),
                new ShaderInt(parallaxRefineSteps, "ParallaxRefineSteps"),
                new ShaderFloat(parallaxHeightBias, "ParallaxHeightBias"),
                new ShaderFloat(parallaxSilhouette ? 1.0f : 0.0f, "ParallaxSilhouette"),

                // Forward-only uniforms (harmless for deferred; the program will ignore unused uniforms)
                new ShaderFloat(forwardSpecularIntensity, "MatSpecularIntensity"),
                new ShaderFloat(forwardShininess, "MatShininess"),
            ];

            XRTexture?[] textures = [albedo, height];
            XRMaterial material = new(parameters, textures, frag);
            material.RenderPass = deferred
                ? (int)EDefaultRenderPass.OpaqueDeferred
                : (int)EDefaultRenderPass.OpaqueForward;

            return material;
        }

        public static XRMaterial CreateUnlitColorMaterialForward()
            => CreateUnlitColorMaterialForward(Color.DarkTurquoise);

        public static XRMaterial CreateColorMaterialDeferred()
            => CreateColorMaterialDeferred(Color.DarkTurquoise);

        public static XRMaterial CreateColorMaterialDeferred(ColorF4 color)
        {
            ShaderVar[] parameters =
            [
                new ShaderVector3((ColorF3)color, "BaseColor"),
                new ShaderFloat(color.A, "Opacity"),
            ];

            XRMaterial material = new(parameters, ShaderHelper.LitColorFragDeferred()!)
            {
                RenderPass = (int)EDefaultRenderPass.OpaqueDeferred
            };

            return material;
        }

        public static XRMaterial CreateUnlitColorMaterialForward(ColorF4 color)
            => new([new ShaderVector4(color, "MatColor")], ShaderHelper.UnlitColorFragForward()!) { RenderPass = (int)EDefaultRenderPass.OpaqueForward };

        public static XRMaterial CreateLitColorMaterial(bool deferred = true)
            => CreateLitColorMaterial(Color.DarkTurquoise, deferred);

        /// <summary>
        /// Creates a material for lit color rendering.
        /// Parameters are:
        /// ShaderVector3("BaseColor", color),
        /// ShaderFloat("Opacity", color.A),
        /// ShaderFloat("Specular", 1.0f),
        /// ShaderFloat("Roughness", 1.0f),
        /// ShaderFloat("Metallic", 0.0f),
        /// ShaderFloat("IndexOfRefraction", 1.0f)
        /// </summary>
        /// <param name="color"></param>
        /// <param name="deferred"></param>
        /// <returns></returns>
        public static XRMaterial CreateLitColorMaterial(ColorF4 color, bool deferred = true)
        {
            XRShader? frag = deferred ? ShaderHelper.LitColorFragDeferred() : ShaderHelper.LitColorFragForward();
            ShaderVar[] parameters =
            [
                new ShaderVector3((ColorF3)color, "BaseColor"),
                new ShaderFloat(color.A, "Opacity"),
                new ShaderFloat(1.0f, "Specular"),
                new ShaderFloat(1.0f, "Roughness"),
                new ShaderFloat(0.0f, "Metallic"),
                new ShaderFloat(1.0f, "IndexOfRefraction"),
            ];

            XRMaterial material = new(parameters, frag!);
            material.RenderPass = deferred
                ? (int)EDefaultRenderPass.OpaqueDeferred
                : (int)EDefaultRenderPass.OpaqueForward;

            return material;
        }
        public enum EOpaque
        {
            /// <summary>
            ///  (the default): Takes the transparency information from the color�s
            ///  alpha channel, where the value 1.0 is opaque.
            /// </summary>
            A_ONE,
            /// <summary>
            /// Takes the transparency information from the color�s red, green,
            /// and blue channels, where the value 0.0 is opaque, with each channel 
            /// modulated independently.
            /// </summary>
            RGB_ZERO,
            /// <summary>
            /// Takes the transparency information from the color�s
            /// alpha channel, where the value 0.0 is opaque.
            /// </summary>
            A_ZERO,
            /// <summary>
            ///  Takes the transparency information from the color�s red, green,
            ///  and blue channels, where the value 1.0 is opaque, with each channel 
            ///  modulated independently.
            /// </summary>
            RGB_ONE,
        }
        /// <summary>
        /// Creates a Blinn lighting model material for a forward renderer.
        /// </summary>
        public static XRMaterial CreateBlinnMaterial_Forward(
            Vector3? emission,
            Vector3? ambient,
            Vector3? diffuse,
            Vector3? specular,
            float shininess,
            float transparency,
            Vector3 transparent,
            EOpaque transparencyMode,
            float reflectivity,
            Vector3 reflective,
            float indexOfRefraction)
        {
            // color = emission + ambient * al + diffuse * max(N * L, 0) + specular * max(H * N, 0) ^ shininess
            // where:
            // � al � A constant amount of ambient light contribution coming from the scene.In the COMMON
            // profile, this is the sum of all the <light><technique_common><ambient> values in the <visual_scene>.
            // � N � Normal vector (normalized)
            // � L � Light vector (normalized)
            // � I � Eye vector (normalized)
            // � H � Half-angle vector, calculated as halfway between the unit Eye and Light vectors, using the equation H = normalize(I + L)

            int count = 0;
            if (emission.HasValue) ++count;
            if (ambient.HasValue) ++count;
            if (diffuse.HasValue) ++count;
            if (specular.HasValue) ++count;
            ShaderVar[] parameters = new ShaderVar[count + 1];
            count = 0;

            string source = "#version 450\n";

            if (emission.HasValue)
            {
                source += "uniform Vector3 Emission;\n";
                parameters[count++] = new ShaderVector3(emission.Value, "Emission");
            }
            else
                source += "uniform sampler2D Emission;\n";

            if (ambient.HasValue)
            {
                source += "uniform Vector3 Ambient;\n";
                parameters[count++] = new ShaderVector3(ambient.Value, "Ambient");
            }
            else
                source += "uniform sampler2D Ambient;\n";

            if (diffuse.HasValue)
            {
                source += "uniform Vector3 Diffuse;\n";
                parameters[count++] = new ShaderVector3(diffuse.Value, "Diffuse");
            }
            else
                source += "uniform sampler2D Diffuse;\n";

            if (specular.HasValue)
            {
                source += "uniform Vector3 Specular;\n";
                parameters[count++] = new ShaderVector3(specular.Value, "Specular");
            }
            else
                source += "uniform sampler2D Specular;\n";

            source += "uniform float Shininess;\n";
            parameters[count++] = new ShaderFloat(shininess, "Shininess");

            if (transparencyMode == EOpaque.RGB_ZERO ||
                transparencyMode == EOpaque.RGB_ONE)
                source += @"
float luminance(in Vector3 color)
{
    return (color.r * 0.212671) + (color.g * 0.715160) + (color.b * 0.072169);
}";

            switch (transparencyMode)
            {
                case EOpaque.A_ONE:
                    source += "\nresult = mix(fb, mat, transparent.a * transparency);";
                    break;
                case EOpaque.RGB_ZERO:
                    source += @"
result.rgb = fb.rgb * (transparent.rgb * transparency) + mat.rgb * (1.0f - transparent.rgb * transparency);
result.a = fb.a * (luminance(transparent.rgb) * transparency) + mat.a * (1.0f - luminance(transparent.rgb) * transparency);";
                    break;
                case EOpaque.A_ZERO:
                    source += "\nresult = mix(mat, fb, transparent.a * transparency);";
                    break;
                case EOpaque.RGB_ONE:
                    source += @"
result.rgb = fb.rgb * (1.0f - transparent.rgb * transparency) + mat.rgb * (transparent.rgb * transparency);
result.a = fb.a * (1.0f - luminance(transparent.rgb) * transparency) + mat.a * (luminance(transparent.rgb) * transparency);";
                    break;
            }


            //#version 450

            //layout (location = 0) out Vector4 OutColor;

            //uniform Vector4 MatColor;
            //uniform float MatSpecularIntensity;
            //uniform float MatShininess;

            //uniform Vector3 CameraPosition;
            //uniform Vector3 CameraForward;

            //in Vector3 FragPos;
            //in Vector3 FragNorm;

            //" + LightingSetupBasic() + @"

            //void main()
            //{
            //    Vector3 normal = normalize(FragNorm);

            //    " + LightingCalcForward() + @"

            //    OutColor = MatColor * Vector4(totalLight, 1.0);
            //}

            return new(parameters, new XRShader(EShaderType.Fragment, source));
        }

        /// <summary>
        /// Helper method to set the material to be transparent.
        /// Sets the render pass to transparent and enables default transparent blending.
        /// The material's shader must output a color with an alpha channel value less than 1.0f for this to do anything.
        /// </summary>
        /// <param name="transparentRenderPass"></param>
        public void EnableTransparency(int transparentRenderPass = (int)EDefaultRenderPass.TransparentForward)
        {
            if (RenderPass != transparentRenderPass && !IsManagedTransparencyRenderPass(RenderPass))
                _opaqueRenderPass = RenderPass;

            TransparencyMode = ETransparencyMode.AlphaBlend;
            RenderPass = transparentRenderPass;
        }
    }
}
