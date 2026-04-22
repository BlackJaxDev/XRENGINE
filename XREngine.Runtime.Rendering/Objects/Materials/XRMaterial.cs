using System.ComponentModel;
using System.Numerics;
using System.Text.RegularExpressions;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders;
using YamlDotNet.Serialization;
using Color = System.Drawing.Color;

namespace XREngine.Rendering
{
    [XRAssetInspector("XREngine.Editor.AssetEditors.XRMaterialInspector")]
    public partial class XRMaterial : XRMaterialBase
    {
        [YamlIgnore]
        private XRMaterial? _depthNormalPrePassVariant;
        [YamlIgnore]
        private bool _depthNormalPrePassVariantResolved;
        [YamlIgnore]
        private XRMaterial? _shadowCasterVariant;
        [YamlIgnore]
        private bool _shadowCasterVariantResolved;
        [YamlIgnore]
        private XRMaterial? _shadowBindingSourceMaterial;

        private UberMaterialAuthoredState _uberAuthoredState = UberMaterialAuthoredState.Empty;
        [YamlIgnore]
        private UberMaterialVariantRequest _requestedUberVariant = UberMaterialVariantRequest.Empty;
        [YamlIgnore]
        private UberMaterialVariantBindingState _activeUberVariant = UberMaterialVariantBindingState.Empty;
        [YamlIgnore]
        private UberMaterialVariantStatus _uberVariantStatus = UberMaterialVariantStatus.Empty;
        [YamlIgnore]
        private long _uberStateRevision = 1;
        [YamlIgnore]
        private long _shaderStateRevision = 1;

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
        [Browsable(false)]
        [YamlIgnore]
        public IReadOnlyList<XRShader> MeshShaders => _meshShaders;
        [Browsable(false)]
        [YamlIgnore]
        public IReadOnlyList<XRShader> TaskShaders => _taskShaders;
        [Browsable(false)]
        [YamlIgnore]
        public IReadOnlyList<XRShader> ComputeShaders => _computeShaders;

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

        public UberMaterialAuthoredState UberAuthoredState
        {
            get => _uberAuthoredState;
            set => SetField(ref _uberAuthoredState, value ?? UberMaterialAuthoredState.Empty);
        }

        [Browsable(false)]
        [YamlIgnore]
        public UberMaterialVariantRequest RequestedUberVariant
        {
            get => _requestedUberVariant;
            private set => SetField(ref _requestedUberVariant, value ?? UberMaterialVariantRequest.Empty);
        }

        [Browsable(false)]
        [YamlIgnore]
        public UberMaterialVariantBindingState ActiveUberVariant
        {
            get => _activeUberVariant;
            private set => SetField(ref _activeUberVariant, value ?? UberMaterialVariantBindingState.Empty);
        }

        [Browsable(false)]
        [YamlIgnore]
        public UberMaterialVariantStatus UberVariantStatus
        {
            get => _uberVariantStatus;
            private set => SetField(ref _uberVariantStatus, value ?? UberMaterialVariantStatus.Empty);
        }

        [Browsable(false)]
        [YamlIgnore]
        public long UberStateRevision
        {
            get => _uberStateRevision;
            private set => SetField(ref _uberStateRevision, value <= 0 ? 1 : value);
        }

        [Browsable(false)]
        [YamlIgnore]
        public long ShaderStateRevision
        {
            get => _shaderStateRevision;
            private set => SetField(ref _shaderStateRevision, value <= 0 ? 1 : value);
        }

        [Browsable(false)]
        [YamlIgnore]
        public bool HasFragmentShader => _fragmentShaders.Count > 0;

        public XRShader? GetShader(EShaderType shaderType)
            => GetShaderList(shaderType).LastOrDefault();

        public int GetShaderCount(EShaderType shaderType)
            => GetShaderList(shaderType).Count;

        public void SetShader(EShaderType shaderType, XRShader? shader, bool coerceShaderType = false)
        {
            if (shader is not null)
            {
                if (coerceShaderType && shader.Type != shaderType)
                    shader.Type = shaderType;

                if (shader.Type != shaderType)
                    throw new ArgumentException($"Shader '{shader.Name ?? shader.FilePath ?? shader.GetType().Name}' is {shader.Type} but was assigned to {shaderType}.", nameof(shader));
            }

            var updated = new List<XRShader>(Shaders.Count + (shader is null ? 0 : 1));
            bool changed = false;

            foreach (XRShader existing in Shaders)
            {
                if (existing is null)
                {
                    changed = true;
                    continue;
                }

                if (existing.Type == shaderType || ReferenceEquals(existing, shader))
                {
                    changed = true;
                    continue;
                }

                updated.Add(existing);
            }

            if (shader is not null)
            {
                updated.Add(shader);
                changed = true;
            }

            if (!changed)
                return;

            Shaders = new EventList<XRShader>(updated);
            MarkDirty();
        }

        public int NormalizeShaderStages(bool preferLast = true)
        {
            if (Shaders.Count <= 1)
                return 0;

            var seenTypes = new HashSet<EShaderType>();
            var normalized = new List<XRShader>(Shaders.Count);
            int removedCount = 0;

            if (preferLast)
            {
                for (int i = Shaders.Count - 1; i >= 0; i--)
                {
                    XRShader shader = Shaders[i];
                    if (shader is null)
                    {
                        removedCount++;
                        continue;
                    }

                    if (seenTypes.Add(shader.Type))
                        normalized.Add(shader);
                    else
                        removedCount++;
                }

                normalized.Reverse();
            }
            else
            {
                foreach (XRShader shader in Shaders)
                {
                    if (shader is null)
                    {
                        removedCount++;
                        continue;
                    }

                    if (seenTypes.Add(shader.Type))
                        normalized.Add(shader);
                    else
                        removedCount++;
                }
            }

            if (removedCount == 0)
                return 0;

            Shaders = new EventList<XRShader>(normalized);
            MarkDirty();
            return removedCount;
        }

        public void RefreshShaderState()
            => ShadersChanged();

        public bool SetUberFeatureEnabled(string featureId, bool enabled)
            => UpdateUberAuthoredState(static (state, args) => state.SetFeature(args.featureId, args.enabled), (featureId, enabled));

        public bool SetUberPropertyMode(string propertyName, EShaderUiPropertyMode mode)
        {
            string? capturedStaticLiteral = mode == EShaderUiPropertyMode.Static
                ? TryCaptureUberStaticLiteral(propertyName)
                : null;

            return UpdateUberAuthoredState(static (state, args) =>
            {
                UberMaterialAuthoredState next = state.SetPropertyMode(args.propertyName, args.mode);
                if (args.mode == EShaderUiPropertyMode.Static && args.capturedStaticLiteral is not null)
                    next = next.SetPropertyStaticLiteral(args.propertyName, args.capturedStaticLiteral);

                return next;
            }, (propertyName, mode, capturedStaticLiteral));
        }

        public bool SetUberPropertyStaticLiteral(string propertyName, string? staticLiteral)
            => UpdateUberAuthoredState(static (state, args) => state.SetPropertyStaticLiteral(args.propertyName, args.staticLiteral), (propertyName, staticLiteral));

        public bool RefreshUberPropertyStaticLiteral(string propertyName)
        {
            string? literal = TryCaptureUberStaticLiteral(propertyName);
            return literal is not null && SetUberPropertyStaticLiteral(propertyName, literal);
        }

        public void SetRequestedUberVariant(UberMaterialVariantRequest? request)
            => RequestedUberVariant = request ?? UberMaterialVariantRequest.Empty;

        public void SetActiveUberVariant(UberMaterialVariantBindingState? bindingState)
            => ActiveUberVariant = bindingState ?? UberMaterialVariantBindingState.Empty;

        public void SetUberVariantStatus(UberMaterialVariantStatus? status)
            => UberVariantStatus = status ?? UberMaterialVariantStatus.Empty;

        public void ClearUberVariantRuntimeState()
        {
            SetRequestedUberVariant(null);
            SetActiveUberVariant(null);
            SetUberVariantStatus(null);
        }

        private bool UpdateUberAuthoredState<TArgs>(Func<UberMaterialAuthoredState, TArgs, UberMaterialAuthoredState> update, TArgs args)
        {
            UberMaterialAuthoredState current = UberAuthoredState ?? UberMaterialAuthoredState.Empty;
            UberMaterialAuthoredState next = update(current, args) ?? UberMaterialAuthoredState.Empty;
            if (current.Equals(next))
                return false;

            UberAuthoredState = next;
            ClearUberVariantRuntimeState();
            BumpUberStateRevision();
            MarkDirty();
            return true;
        }

        private void BumpUberStateRevision()
        {
            long next = unchecked(UberStateRevision + 1);
            if (next <= 0)
                next = 1;

            UberStateRevision = next;
        }

        private void BumpShaderStateRevision()
        {
            long next = unchecked(ShaderStateRevision + 1);
            if (next <= 0)
                next = 1;

            ShaderStateRevision = next;
        }

        private string? TryCaptureUberStaticLiteral(string propertyName)
        {
            if (!TryGetUberMaterialState(out _, out ShaderUiManifest manifest) ||
                !manifest.PropertyLookup.TryGetValue(propertyName, out ShaderUiProperty? property) ||
                property.IsSampler)
                return null;

            return UberShaderVariantBuilder.TryFormatStaticLiteral(this, property, out string literal)
                ? literal
                : null;
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
        public static XRMaterial? InvalidMaterial => RuntimeRenderingHostServices.Current.InvalidMaterial;

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
            InvalidateDepthNormalPrePassVariant();
            InvalidateShadowCasterVariant();

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

        [YamlIgnore]
        public XRMaterial? DepthNormalPrePassVariant
        {
            get
            {
                if (_depthNormalPrePassVariantResolved)
                    return _depthNormalPrePassVariant;

                _depthNormalPrePassVariant = ForwardDepthNormalVariantFactory.CreateMaterialVariant(this);
                _depthNormalPrePassVariantResolved = true;
                return _depthNormalPrePassVariant;
            }
        }

        [YamlIgnore]
        public XRMaterial? ShadowCasterVariant
        {
            get
            {
                if (_shadowCasterVariantResolved)
                    return _shadowCasterVariant;

                _shadowCasterVariant = ShadowCasterVariantFactory.CreateMaterialVariant(this);
                _shadowCasterVariantResolved = true;
                return _shadowCasterVariant;
            }
        }

        [Browsable(false)]
        [YamlIgnore]
        public XRMaterial? ShadowBindingSourceMaterial
        {
            get => _shadowBindingSourceMaterial;
            internal set => SetField(ref _shadowBindingSourceMaterial, value);
        }

        public void InvalidateDepthNormalPrePassVariant()
        {
            _depthNormalPrePassVariant?.Destroy();
            _depthNormalPrePassVariant = null;
            _depthNormalPrePassVariantResolved = false;
        }

        public void InvalidateShadowCasterVariant()
        {
            _shadowCasterVariant?.Destroy();
            _shadowCasterVariant = null;
            _shadowCasterVariantResolved = false;
        }

        private static bool IsManagedTransparencyRenderPass(int renderPass)
            => renderPass == (int)EDefaultRenderPass.MaskedForward ||
               renderPass == (int)EDefaultRenderPass.TransparentForward ||
               renderPass == (int)EDefaultRenderPass.WeightedBlendedOitForward ||
               renderPass == (int)EDefaultRenderPass.PerPixelLinkedListForward ||
               renderPass == (int)EDefaultRenderPass.DepthPeelingForward;

        private void PreShadersSet()
        {
            _shaders.PostModified -= ShadersChanged;
            _shaders.PostAnythingAdded -= ShaderAdded;
            _shaders.PostAnythingRemoved -= ShaderRemoved;
        }

        private IReadOnlyList<XRShader> GetShaderList(EShaderType shaderType)
            => shaderType switch
            {
                EShaderType.Vertex => _vertexShaders,
                EShaderType.Fragment => _fragmentShaders,
                EShaderType.Geometry => _geometryShaders,
                EShaderType.TessControl => _tessCtrlShaders,
                EShaderType.TessEvaluation => _tessEvalShaders,
                EShaderType.Mesh => _meshShaders,
                EShaderType.Task => _taskShaders,
                EShaderType.Compute => _computeShaders,
                _ => Array.Empty<XRShader>()
            };

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
            InvalidateDepthNormalPrePassVariant();
            InvalidateShadowCasterVariant();
            BumpShaderStateRevision();

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
            ShaderPipelineProgram = RuntimeRenderingHostServices.Current.AllowShaderPipelines
                ? new XRRenderProgram(true, true, Shaders.Where(x => x.Type != EShaderType.Vertex))
                : null;

            SyncParametersToShaderUniforms();
            SyncRequiredEngineUniforms();
            SyncAlphaCutoffParameter();
            EnsureUberStateInitialized();
        }

        /// <summary>
        /// Lazily creates the <see cref="XRMaterialBase.ShaderPipelineProgram"/> when it was
        /// not created at material init time (because <c>AllowShaderPipelines</c> was false).
        /// Called by passes that force-enable pipeline mode (e.g., forward depth-normal prepass).
        /// </summary>
        public void EnsureShaderPipelineProgram()
        {
            if (ShaderPipelineProgram is not null)
                return;

            var nonVertexShaders = Shaders.Where(x => x.Type != EShaderType.Vertex);
            if (!nonVertexShaders.Any())
                return;

            ShaderPipelineProgram = new XRRenderProgram(true, true, nonVertexShaders);
        }

        public ETransparencyMode GetEffectiveTransparencyMode()
            => TransparentTechniqueOverride ?? TransparencyMode;

        public ETransparencyMode InferTransparencyMode()
        {
            var blend = RenderOptions?.BlendModeAllDrawBuffers;
            bool blendEnabled = blend?.Enabled == ERenderParamUsage.Enabled;
            bool hasAlphaCutoff = Parameter<ShaderFloat>("AlphaCutoff") is not null;
            bool depthWrites = RenderOptions?.DepthTest?.UpdateDepth ?? true;
            bool alphaToCoverage = RenderOptions?.AlphaToCoverage == ERenderParamUsage.Enabled;

            if (blendEnabled)
            {
                if (blend!.RgbDstFactor == EBlendingFactor.One && blend.RgbSrcFactor == EBlendingFactor.SrcAlpha)
                    return ETransparencyMode.Additive;

                if (blend.RgbSrcFactor == EBlendingFactor.One && blend.RgbDstFactor == EBlendingFactor.OneMinusSrcAlpha)
                    return ETransparencyMode.PremultipliedAlpha;

                return ETransparencyMode.AlphaBlend;
            }

            if (alphaToCoverage && hasAlphaCutoff && depthWrites)
                return ETransparencyMode.AlphaToCoverage;

            if (hasAlphaCutoff && depthWrites)
                return ETransparencyMode.Masked;

            return ETransparencyMode.Opaque;
        }

        public bool CanUseSharedOpaqueShadowMaterial()
        {
            if (GetEffectiveTransparencyMode() != ETransparencyMode.Opaque)
                return false;

            if (Parameter<ShaderFloat>("AlphaCutoff") is not null)
                return false;

            if (InferTransparencyMode() != ETransparencyMode.Opaque)
                return false;

            if (HasSettingUniformsHandlers)
                return false;

            return !VertexShadersRequireMaterialBindings()
                && GeometryShaders.Count == 0
                && TessEvalShaders.Count == 0
                && TessCtrlShaders.Count == 0
                && MeshShaders.Count == 0
                && TaskShaders.Count == 0;
        }

        private bool VertexShadersRequireMaterialBindings()
            => ShaderStageRequiresMaterialBindings(VertexShaders, EngineVertexUniformNames);

        private static bool ShaderStageRequiresMaterialBindings(IEnumerable<XRShader> shaders, HashSet<string> engineManagedUniformNames)
        {
            foreach (XRShader shader in shaders)
            {
                string? source = shader?.Source?.Text;
                if (string.IsNullOrWhiteSpace(source))
                    return true;

                foreach (Match match in StageUniformDeclRegex.Matches(source))
                {
                    string uniformName = match.Groups[2].Value;
                    if (engineManagedUniformNames.Contains(uniformName))
                        continue;

                    return true;
                }
            }

            return false;
        }

        public bool IsTransparentLike(ETransparencyMode? mode = null)
        {
            ETransparencyMode value = mode ?? GetEffectiveTransparencyMode();
            return value is not ETransparencyMode.Opaque and not ETransparencyMode.Masked and not ETransparencyMode.AlphaToCoverage;
        }

        private static bool ExactTransparencyEnabled
            => RuntimeRenderingHostServices.Current.EnableExactTransparencyTechniques;

        private const int UberBlendModeOpaque = 0;
        private const int UberBlendModeCutout = 1;
        private const int UberBlendModeTransparent = 3;
        private const int UberBlendModeAdditive = 4;

        private ETransparencyMode GetRuntimeTransparencyMode(ETransparencyMode effectiveMode)
            => !ExactTransparencyEnabled && effectiveMode is ETransparencyMode.PerPixelLinkedList or ETransparencyMode.DepthPeeling
                ? ETransparencyMode.WeightedBlendedOit
                : effectiveMode;

        private void ApplyTransparencyState()
        {
            ETransparencyMode effectiveMode = GetEffectiveTransparencyMode();
            ETransparencyMode runtimeMode = GetRuntimeTransparencyMode(effectiveMode);
            RenderOptions ??= new RenderingParameters();
            RenderOptions.DepthTest ??= new DepthTest();

            if (runtimeMode is ETransparencyMode.Masked or ETransparencyMode.AlphaToCoverage)
                EnsureAlphaCutoffParameter();

            SettingUniforms -= ConfigureMaterialProgram;
            NormalizeTransparencyShaders(runtimeMode);

            switch (runtimeMode)
            {
                case ETransparencyMode.Opaque:
                    RenderOptions.BlendModeAllDrawBuffers = BlendMode.Disabled();
                    RenderOptions.BlendModesPerDrawBuffer = null;
                    RenderOptions.AlphaToCoverage = ERenderParamUsage.Disabled;
                    RenderOptions.DepthTest.Enabled = ERenderParamUsage.Enabled;
                    RenderOptions.DepthTest.UpdateDepth = true;
                    if (IsManagedTransparencyRenderPass(RenderPass))
                        RenderPass = _opaqueRenderPass;
                    break;
                case ETransparencyMode.Masked:
                    RenderOptions.BlendModeAllDrawBuffers = BlendMode.Disabled();
                    RenderOptions.BlendModesPerDrawBuffer = null;
                    RenderOptions.AlphaToCoverage = ERenderParamUsage.Disabled;
                    RenderOptions.DepthTest.Enabled = ERenderParamUsage.Enabled;
                    RenderOptions.DepthTest.UpdateDepth = true;
                    // Deferred shaders support alpha cutoff natively; keep the deferred pass.
                    if (_opaqueRenderPass != (int)EDefaultRenderPass.OpaqueDeferred)
                        RenderPass = (int)EDefaultRenderPass.MaskedForward;
                    break;
                case ETransparencyMode.AlphaToCoverage:
                    RenderOptions.BlendModeAllDrawBuffers = BlendMode.Disabled();
                    RenderOptions.BlendModesPerDrawBuffer = null;
                    RenderOptions.AlphaToCoverage = ERenderParamUsage.Enabled;
                    RenderOptions.DepthTest.Enabled = ERenderParamUsage.Enabled;
                    RenderOptions.DepthTest.UpdateDepth = true;
                    // Deferred shaders support alpha cutoff natively; keep the deferred pass.
                    if (_opaqueRenderPass != (int)EDefaultRenderPass.OpaqueDeferred)
                        RenderPass = (int)EDefaultRenderPass.MaskedForward;
                    break;
                case ETransparencyMode.WeightedBlendedOit:
                    RenderOptions.BlendModeAllDrawBuffers = null;
                    RenderOptions.BlendModesPerDrawBuffer = CreateWeightedBlendedOitBlendModes();
                    RenderOptions.AlphaToCoverage = ERenderParamUsage.Disabled;
                    RenderOptions.DepthTest.Enabled = ERenderParamUsage.Enabled;
                    RenderOptions.DepthTest.UpdateDepth = false;
                    RenderPass = (int)EDefaultRenderPass.WeightedBlendedOitForward;
                    break;
                case ETransparencyMode.PerPixelLinkedList:
                    RenderOptions.BlendModeAllDrawBuffers = BlendMode.Disabled();
                    RenderOptions.BlendModesPerDrawBuffer = null;
                    RenderOptions.AlphaToCoverage = ERenderParamUsage.Disabled;
                    RenderOptions.DepthTest.Enabled = ERenderParamUsage.Enabled;
                    RenderOptions.DepthTest.UpdateDepth = false;
                    RenderPass = (int)EDefaultRenderPass.PerPixelLinkedListForward;
                    SettingUniforms += ConfigureMaterialProgram;
                    break;
                case ETransparencyMode.DepthPeeling:
                    RenderOptions.BlendModeAllDrawBuffers = BlendMode.Disabled();
                    RenderOptions.BlendModesPerDrawBuffer = null;
                    RenderOptions.AlphaToCoverage = ERenderParamUsage.Disabled;
                    RenderOptions.DepthTest.Enabled = ERenderParamUsage.Enabled;
                    RenderOptions.DepthTest.UpdateDepth = true;
                    RenderPass = (int)EDefaultRenderPass.DepthPeelingForward;
                    SettingUniforms += ConfigureMaterialProgram;
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
                    RenderOptions.BlendModesPerDrawBuffer = null;
                    RenderOptions.AlphaToCoverage = ERenderParamUsage.Disabled;
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
                    RenderOptions.BlendModesPerDrawBuffer = null;
                    RenderOptions.AlphaToCoverage = ERenderParamUsage.Disabled;
                    RenderOptions.DepthTest.Enabled = ERenderParamUsage.Enabled;
                    RenderOptions.DepthTest.UpdateDepth = false;
                    RenderPass = (int)EDefaultRenderPass.TransparentForward;
                    break;
                default:
                    RenderOptions.BlendModeAllDrawBuffers = BlendMode.EnabledTransparent();
                    RenderOptions.BlendModesPerDrawBuffer = null;
                    RenderOptions.AlphaToCoverage = ERenderParamUsage.Disabled;
                    RenderOptions.DepthTest.Enabled = ERenderParamUsage.Enabled;
                    RenderOptions.DepthTest.UpdateDepth = false;
                    RenderPass = (int)EDefaultRenderPass.TransparentForward;
                    break;
            }

            SyncUberTransparencyParameters(runtimeMode);
            SyncAlphaCutoffParameter();
        }

        private void SyncUberTransparencyParameters(ETransparencyMode runtimeMode)
        {
            var uberMode = Parameter<ShaderInt>("_Mode");
            if (uberMode is not null)
            {
                int modeValue = runtimeMode switch
                {
                    ETransparencyMode.Opaque => UberBlendModeOpaque,
                    ETransparencyMode.Masked or ETransparencyMode.AlphaToCoverage => UberBlendModeCutout,
                    ETransparencyMode.Additive => UberBlendModeAdditive,
                    _ => UberBlendModeTransparent,
                };

                uberMode.SetValue(modeValue);
            }

            var alphaForceOpaque = Parameter<ShaderFloat>("_AlphaForceOpaque");
            if (alphaForceOpaque is not null)
                alphaForceOpaque.SetValue(runtimeMode == ETransparencyMode.Opaque ? 1.0f : 0.0f);
        }

        private static void ConfigureMaterialProgram(XRMaterialBase material, XRRenderProgram program)
            => RuntimeRenderingHostServices.Current.ConfigureMaterialProgram(material, program);

        private static Dictionary<uint, BlendMode> CreateWeightedBlendedOitBlendModes()
            => new()
            {
                [0u] = new BlendMode
                {
                    Enabled = ERenderParamUsage.Enabled,
                    RgbSrcFactor = EBlendingFactor.One,
                    RgbDstFactor = EBlendingFactor.One,
                    AlphaSrcFactor = EBlendingFactor.One,
                    AlphaDstFactor = EBlendingFactor.One,
                },
                [1u] = new BlendMode
                {
                    Enabled = ERenderParamUsage.Enabled,
                    RgbSrcFactor = EBlendingFactor.Zero,
                    RgbDstFactor = EBlendingFactor.OneMinusSrcColor,
                    AlphaSrcFactor = EBlendingFactor.Zero,
                    AlphaDstFactor = EBlendingFactor.OneMinusSrcAlpha,
                },
            };

        private void NormalizeTransparencyShaders(ETransparencyMode effectiveMode)
        {
            if (Shaders.Count == 0)
                return;

            bool keepDeferredBase = _opaqueRenderPass == (int)EDefaultRenderPass.OpaqueDeferred &&
                effectiveMode is ETransparencyMode.Opaque or ETransparencyMode.Masked or ETransparencyMode.AlphaToCoverage;

            for (int i = 0; i < Shaders.Count; i++)
            {
                XRShader? shader = Shaders[i];
                if (shader is null || shader.Type != EShaderType.Fragment)
                    continue;

                XRShader? replacement = effectiveMode switch
                {
                    ETransparencyMode.WeightedBlendedOit => ShaderHelper.GetWeightedBlendedOitForwardVariant(shader),
                    ETransparencyMode.PerPixelLinkedList => ShaderHelper.GetPerPixelLinkedListForwardVariant(shader),
                    ETransparencyMode.DepthPeeling => ShaderHelper.GetDepthPeelingForwardVariant(shader),
                    _ when keepDeferredBase => ShaderHelper.GetDeferredVariantOfShader(shader),
                    _ => ShaderHelper.GetStandardForwardVariant(shader),
                };

                if (replacement is not null)
                    Shaders[i] = replacement;
            }
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

            var uberCutoff = Parameter<ShaderFloat>("_Cutoff");
            if (uberCutoff is not null)
                uberCutoff.SetValue(AlphaCutoff);
        }

        /// <summary>
        /// Regex matching simple uniform declarations (no arrays, no blocks).
        /// Captures group 1 = GLSL type, group 2 = uniform name.
        /// Handles optional precision qualifiers (lowp / mediump / highp).
        /// </summary>
        private static readonly Regex UniformDeclRegex = new(
            @"\buniform\s+(?:(?:lowp|mediump|highp)\s+)?(\w+)\s+(\w+)\s*[;=]",
            RegexOptions.Compiled);

        private static readonly Regex StageUniformDeclRegex = new(
            @"^\s*(?:layout\s*\([^\)]*\)\s*)?uniform\s+(?:(?:lowp|mediump|highp)\s+)?(\w+)\s+(\w+)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>
        /// Engine-managed uniform names that should never appear in the material
        /// <see cref="XRMaterialBase.Parameters"/> array (they are set automatically
        /// by the render pipeline each frame).
        /// </summary>
        private static readonly HashSet<string> EngineUniformNames =
            new(Enum.GetNames<EEngineUniform>(), StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> EngineVertexUniformNames = CreateEngineVertexUniformNames();

        private static HashSet<string> CreateEngineVertexUniformNames()
        {
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (EEngineUniform uniform in Enum.GetValues<EEngineUniform>())
            {
                names.Add(uniform.ToStringFast());
                names.Add(uniform.ToVertexUniformName());
            }

            return names;
        }

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

            if (IsUberShaderMaterial())
            {
                HashSet<string> mergedNames = new(merged.Select(static x => x.Name), StringComparer.Ordinal);
                foreach (ShaderVar existing in existingByName.Values)
                {
                    if (!ShouldPreserveUberParameter(existing.Name) || !mergedNames.Add(existing.Name))
                        continue;

                    merged.Add(existing);
                }
            }

            // Only reassign if the set actually changed (avoids unnecessary change-notification noise)
            ShaderVar[] mergedArray = [.. merged];
            ShaderVar[]? current = Parameters;
            if (current is null || current.Length != mergedArray.Length || !current.SequenceEqual(mergedArray))
                Parameters = mergedArray;
        }

        private bool IsUberShaderMaterial()
        {
            XRShader? fragmentShader = GetShader(EShaderType.Fragment);
            string? shaderPath = fragmentShader?.Source?.FilePath ?? fragmentShader?.FilePath;
            return string.Equals(Path.GetFileName(shaderPath), "UberShader.frag", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldPreserveUberParameter(string? parameterName)
        {
            if (string.IsNullOrWhiteSpace(parameterName))
                return false;

            return parameterName.StartsWith("_", StringComparison.Ordinal) ||
                   string.Equals(parameterName, "AlphaCutoff", StringComparison.Ordinal);
        }

        /// <summary>
        /// Scans all shader sources for engine-managed uniform names and automatically
        /// sets <see cref="XRMaterialBase.RenderOptions"/>.<see cref="RenderingParameters.RequiredEngineUniforms"/>
        /// to match. Called from <see cref="ShadersChanged"/> after <see cref="SyncParametersToShaderUniforms"/>.
        /// </summary>
        private void SyncRequiredEngineUniforms()
        {
            if (Shaders.Count == 0)
                return;

            var flags = EUniformRequirements.None;
            bool anySourceParsed = false;

            foreach (var shader in Shaders)
            {
                if (shader?.Source?.Text is not { Length: > 0 } source)
                    continue;

                anySourceParsed = true;
                flags |= UniformRequirementsDetection.DetectFromSource(source);
            }

            if (!anySourceParsed)
                return;

            // Merge: auto-detection adds flags but never removes ones set explicitly
            // (e.g., by the model importer or the user). This prevents #include-hidden
            // uniforms from losing their Lights/Camera flags after ShadersChanged().
            var merged = RenderOptions.RequiredEngineUniforms | flags;
            if (RenderOptions.RequiredEngineUniforms != merged)
                RenderOptions.RequiredEngineUniforms = merged;
        }

        public static XRMaterial CreateUnlitAlphaTextureMaterialForward(XRTexture2D texture)
            => new([texture], ShaderHelper.UnlitAlphaTextureFragForward()!) { RenderPass = (int)EDefaultRenderPass.TransparentForward };

        public static XRMaterial CreateUnlitTextureMaterialForward(XRTexture texture)
            => new([texture], ShaderHelper.UnlitTextureFragForward()!) { RenderPass = (int)EDefaultRenderPass.OpaqueForward };

        public static XRMaterial CreateUnlitTextureMaterialForward()
            => new(ShaderHelper.UnlitTextureFragForward()!) { RenderPass = (int)EDefaultRenderPass.OpaqueForward };

        private static ShaderVar[] CreateDeferredLitDefaults(ColorF4 color, float specular = 0.2f, float roughness = 0.0f, float metallic = 0.0f, float emission = 0.0f)
            =>
            [
                new ShaderVector3((ColorF3)color, "BaseColor"),
                new ShaderFloat(color.A, "Opacity"),
                new ShaderFloat(specular, "Specular"),
                new ShaderFloat(roughness, "Roughness"),
                new ShaderFloat(metallic, "Metallic"),
                new ShaderFloat(emission, "Emission"),
            ];

        public static XRMaterial CreateLitTextureMaterial(bool deferred = true)
        {
            XRShader fragmentShader = (deferred ? ShaderHelper.LitTextureFragDeferred() : ShaderHelper.LitTextureFragForward())!;
            XRMaterial material = deferred
                ? new(CreateDeferredLitDefaults(ColorF4.White), fragmentShader)
                : new(fragmentShader);

            material.RenderPass = deferred
                ? (int)EDefaultRenderPass.OpaqueDeferred
                : (int)EDefaultRenderPass.OpaqueForward;

            return material;
        }

        public static XRMaterial CreateLitTextureMaterial(XRTexture2D texture, bool deferred = true)
        {
            XRShader fragmentShader = (deferred ? ShaderHelper.LitTextureFragDeferred() : ShaderHelper.LitTextureFragForward())!;
            XRMaterial material = deferred
                ? new(CreateDeferredLitDefaults(ColorF4.White), [texture], fragmentShader)
                : new([texture], fragmentShader);

            material.RenderPass = deferred
                ? (int)EDefaultRenderPass.OpaqueDeferred
                : (int)EDefaultRenderPass.OpaqueForward;

            return material;
        }

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
            XRMaterial material = new(CreateDeferredLitDefaults(color), ShaderHelper.LitColorFragDeferred()!)
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

        /// <summary>
        /// Creates a transparent forward water material that combines GPU tessellation subdivision,
        /// procedural ocean waves, grab-pass depth-aware blur refraction, fake caustics, foam controls,
        /// and sphere/capsule driven eddy interaction masks.
        /// Texture layout:
        /// Texture0 = scene color grab, Texture1 = scene depth grab.
        /// Interactors are populated through ShaderVector4 uniforms:
        /// - InteractorSphereN: xyz=center, w=radius.
        /// - InteractorCapsuleStartN / InteractorCapsuleEndN: xyz=endpoints, w=radius on start.
        /// </summary>
        public static XRMaterial CreateDynamicWaterMaterialForward(
            XRTexture2D? sceneColorGrab = null,
            XRTexture2D? sceneDepthGrab = null,
            ColorF4? shallowColor = null,
            ColorF4? deepColor = null)
        {
            ShaderVar[] parameters =
            [
                new ShaderVector4(shallowColor ?? new ColorF4(0.10f, 0.56f, 0.74f, 1.0f), "WaterShallowColor"),
                new ShaderVector4(deepColor ?? new ColorF4(0.01f, 0.09f, 0.19f, 1.0f), "WaterDeepColor"),
                new ShaderFloat(0.55f, "WaterTransparency"),
                new ShaderFloat(0.04f, "RefractionStrength"),
                new ShaderFloat(2.0f, "DepthBlurRadius"),
                new ShaderFloat(0.8f, "CausticIntensity"),
                new ShaderFloat(2.4f, "CausticScale"),
                new ShaderFloat(0.65f, "FoamIntensity"),
                new ShaderFloat(0.40f, "FoamThreshold"),
                new ShaderFloat(0.18f, "FoamSoftness"),
                new ShaderFloat(1.0f, "OceanWaveIntensity"),
                new ShaderFloat(0.22f, "WaveScale"),
                new ShaderFloat(0.7f, "WaveSpeed"),
                new ShaderFloat(0.40f, "WaveHeight"),
                new ShaderInt(4, "WaterSubdivision"),
                new ShaderFloat(1.0f, "EddyIntensity"),
                new ShaderFloat(0.9f, "EddyRadius"),

                new ShaderInt(0, "InteractorSphereCount"),
                new ShaderVector4(Vector4.Zero, "InteractorSphere0"),
                new ShaderVector4(Vector4.Zero, "InteractorSphere1"),
                new ShaderVector4(Vector4.Zero, "InteractorSphere2"),
                new ShaderVector4(Vector4.Zero, "InteractorSphere3"),

                new ShaderInt(0, "InteractorCapsuleCount"),
                new ShaderVector4(Vector4.Zero, "InteractorCapsuleStart0"),
                new ShaderVector4(Vector4.Zero, "InteractorCapsuleEnd0"),
                new ShaderVector4(Vector4.Zero, "InteractorCapsuleStart1"),
                new ShaderVector4(Vector4.Zero, "InteractorCapsuleEnd1"),
                new ShaderVector4(Vector4.Zero, "InteractorCapsuleStart2"),
                new ShaderVector4(Vector4.Zero, "InteractorCapsuleEnd2"),
                new ShaderVector4(Vector4.Zero, "InteractorCapsuleStart3"),
                new ShaderVector4(Vector4.Zero, "InteractorCapsuleEnd3"),
            ];

            XRTexture?[] textures = [sceneColorGrab, sceneDepthGrab];
            XRMaterial material = new(
                parameters,
                textures,
                ShaderHelper.DynamicWaterTessCtrlForward(),
                ShaderHelper.DynamicWaterTessEvalForward(),
                ShaderHelper.DynamicWaterFragForward());
            material.EnableTransparency((int)EDefaultRenderPass.TransparentForward);
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
