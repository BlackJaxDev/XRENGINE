using System.ComponentModel;
using System.Numerics;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using Color = System.Drawing.Color;

namespace XREngine.Rendering
{
    public enum EMeshBillboardMode
    {
        /// <summary>
        /// No billboarding.
        /// </summary>
        None,
        /// <summary>
        /// Billboards facing towards the camera's position.
        /// </summary>
        Perspective,
        /// <summary>
        /// Billboards facing towards the camera's forward vector.
        /// </summary>
        Orthographic,
    }

    public class XRMaterial : XRMaterialBase
    {
        [Browsable(false)]
        public IReadOnlyList<XRShader> FragmentShaders => _fragmentShaders;
        [Browsable(false)]
        public IReadOnlyList<XRShader> GeometryShaders => _geometryShaders;
        [Browsable(false)]
        public IReadOnlyList<XRShader> TessEvalShaders => _tessEvalShaders;
        [Browsable(false)]
        public IReadOnlyList<XRShader> TessCtrlShaders => _tessCtrlShaders;
        [Browsable(false)]
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
            }
        }

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
            => item.Reloaded -= ShaderReloaded;
        private void ShaderAdded(XRShader item)
            => item.Reloaded += ShaderReloaded;

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
        }

        public static XRMaterial CreateUnlitAlphaTextureMaterialForward(XRTexture2D texture)
            => new([texture], ShaderHelper.UnlitAlphaTextureFragForward()) { RenderPass = (int)EDefaultRenderPass.TransparentForward };

        public static XRMaterial CreateUnlitTextureMaterialForward(XRTexture texture)
            => new([texture], ShaderHelper.UnlitTextureFragForward()) { RenderPass = (int)EDefaultRenderPass.OpaqueForward };

        public static XRMaterial CreateUnlitTextureMaterialForward()
            => new(ShaderHelper.UnlitTextureFragForward()) { RenderPass = (int)EDefaultRenderPass.OpaqueForward };

        public static XRMaterial CreateLitTextureMaterial(bool deferred = true)
            => new(deferred ? ShaderHelper.LitTextureFragDeferred() : ShaderHelper.LitTextureFragForward()) { RenderPass = (int)EDefaultRenderPass.OpaqueForward };

        public static XRMaterial CreateLitTextureMaterial(XRTexture2D texture, bool deferred = true)
            => new([texture], deferred ? ShaderHelper.LitTextureFragDeferred() : ShaderHelper.LitTextureFragForward()) { RenderPass = (int)EDefaultRenderPass.OpaqueForward };

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
                RenderPass = (int)EDefaultRenderPass.OpaqueDeferredLit
            };

            return material;
        }

        public static XRMaterial CreateUnlitColorMaterialForward(ColorF4 color)
            => new([new ShaderVector4(color, "MatColor")], ShaderHelper.UnlitColorFragForward()) { RenderPass = (int)EDefaultRenderPass.OpaqueForward };

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
                ? (int)EDefaultRenderPass.OpaqueDeferredLit
                : (int)EDefaultRenderPass.OpaqueForward;

            return material;
        }
        public enum EOpaque
        {
            /// <summary>
            ///  (the default): Takes the transparency information from the color’s
            ///  alpha channel, where the value 1.0 is opaque.
            /// </summary>
            A_ONE,
            /// <summary>
            /// Takes the transparency information from the color’s red, green,
            /// and blue channels, where the value 0.0 is opaque, with each channel 
            /// modulated independently.
            /// </summary>
            RGB_ZERO,
            /// <summary>
            /// Takes the transparency information from the color’s
            /// alpha channel, where the value 0.0 is opaque.
            /// </summary>
            A_ZERO,
            /// <summary>
            ///  Takes the transparency information from the color’s red, green,
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
            // • al – A constant amount of ambient light contribution coming from the scene.In the COMMON
            // profile, this is the sum of all the <light><technique_common><ambient> values in the <visual_scene>.
            // • N – Normal vector (normalized)
            // • L – Light vector (normalized)
            // • I – Eye vector (normalized)
            // • H – Half-angle vector, calculated as halfway between the unit Eye and Light vectors, using the equation H = normalize(I + L)

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
            RenderPass = transparentRenderPass;
            RenderOptions.BlendModeAllDrawBuffers = BlendMode.EnabledTransparent();
        }
    }
}
