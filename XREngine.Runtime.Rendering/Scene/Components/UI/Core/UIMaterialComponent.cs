using XREngine.Extensions;
using XREngine.Rendering.UI.State;
using System.Drawing;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// A basic UI component that renders a quad with a material.
    /// </summary>
    [XRComponentEditor("XREngine.Editor.ComponentEditors.UIMaterialComponentEditor")]
    public class UIMaterialComponent : UIRenderableComponent
    {
        /// <summary>Material access materializes a private legacy draw; intrinsic batches keep instance values only.</summary>
        public override XRMaterial? Material
        {
            get
            {
                if (_intrinsicDefault && base.Material is null)
                    EnsureLegacyMaterial();
                return base.Material;
            }
            set => base.Material = value;
        }

        private readonly List<(XRMaterial Material, ObjectCacheOwnership Ownership, XRShader[] BatchShaders)> _ownedDefaultMaterials = [];
        private readonly HashSet<Action<XRMaterialBase, XRRenderProgram>> _allowedBatchUniformHandlers = [];
        private bool _renderParametersEscaped;
        private bool _reportedMissingBatchColor;
        private int _batchSolidRenderPass = (int)EDefaultRenderPass.TransparentForward;
        private ulong _batchSolidRenderPassIntent;

        /// <summary>Selects the pass used by the next solid color write.</summary>
        public int BatchSolidRenderPass
        {
            get => _batchSolidRenderPass;
            set
            {
                if (value != (int)EDefaultRenderPass.TransparentForward && value != (int)EDefaultRenderPass.OnTopForward)
                    throw new ArgumentOutOfRangeException(nameof(value));
                SetField(ref _batchSolidRenderPassIntent, checked(_batchSolidRenderPassIntent + 1),
                    publishNotifications: false);
                ulong intent = _batchSolidRenderPassIntent;
                SetFieldValidated(ref _batchSolidRenderPass, value, (Owner: this, Intent: intent),
                    static (state, original, current, _) =>
                    {
                        if (state.Owner._batchSolidRenderPassIntent != state.Intent || original != current)
                            throw new InvalidOperationException("A newer solid pass request replaced this write.");
                    }, propertyName: nameof(BatchSolidRenderPass));
                if (_batchSolidRenderPassIntent != intent || _batchSolidRenderPass != value)
                    throw new InvalidOperationException("A newer solid pass request replaced this write.");
            }
        }
        private ShaderVector4? _observedMatColor;
        private readonly bool _intrinsicDefault;
        private Vector4 _intrinsicColor = Color.Magenta.ToVector4();
        private XRTexture2D? _intrinsicImage;
        private Vector4 _batchImageUv = new(0, 0, 1, 1);

        private void ObserveMatColor()
        {
            ShaderVector4? next = IsActiveInHierarchy
                ? base.Material?.Parameter<ShaderVector4>("MatColor") : null;
            if (ReferenceEquals(next, _observedMatColor))
                return;
            if (_observedMatColor is not null)
                _observedMatColor.ValueChanged -= OnMatColorValueChanged;
            _observedMatColor = next;
            if (next is not null)
                next.ValueChanged += OnMatColorValueChanged;
        }

        private void OnMatColorValueChanged(ShaderVar _)
        {
            if (SceneNode is not null && !SceneNode.IsTransformNull && Transform is UITransform transform)
                transform.RecordUIChange(UIChangeDomain.Visual);
        }
        private bool _batchBorderEnabled;
        private Vector4 _batchBorderColor;
        private float _batchBorderWidth;
        private UITextureSnapshot? _capturedImageSnapshot;
        private XRTexture2D? _capturedImageSource;

        private void ReleaseCapturedImageSnapshot()
        {
            if (_capturedImageSnapshot is not null)
                _capturedImageSnapshot.SourceChanged -= OnCapturedImageSourceChanged;
            _capturedImageSnapshot?.Release();
            _capturedImageSnapshot = null;
            _capturedImageSource = null;
        }

        private void OnCapturedImageSourceChanged()
        {
            if (SceneNode is not null && !SceneNode.IsTransformNull && Transform is UITransform transform)
                transform.RecordUIChange(UIChangeDomain.Visual);
        }

        /// <summary>Sets the fill of the component's shared solid-quad primitive.</summary>
        public void SetBatchSolidColor(Vector4 color)
        {
            ulong passIntent = _batchSolidRenderPassIntent;
            if (_intrinsicDefault && !TryDetachOwnedDefaultMaterial())
                throw new InvalidOperationException("Batch solids cannot replace an authored custom UI material.");
            if (_intrinsicDefault && base.Material is null)
            {
                if (_intrinsicImage is not null)
                    ReleaseCapturedImageSnapshot();
                SetField(ref _intrinsicColor, color);
                SetField(ref _intrinsicImage, null);
                SetField(ref _batchImageUv, new Vector4(0, 0, 1, 1));
                SetField(ref _batchBorderEnabled, false);
                if (passIntent != _batchSolidRenderPassIntent)
                    throw new InvalidOperationException("A newer solid pass request replaced this color write.");
                RenderPass = _batchSolidRenderPass;
                RecordQuadVisualChange();
                return;
            }
            if (Material is not { } material || !OwnsDefaultMaterial(material))
                throw new InvalidOperationException("Batch solids require the component's default UI quad material.");
            ConfigureOwnedBatchTransparency(material);
            if (passIntent != _batchSolidRenderPassIntent || !ReferenceEquals(base.Material, material))
                throw new InvalidOperationException("The solid color material or pass changed during this write.");
            RenderPass = _batchSolidRenderPass;
            material.SetVector4("MatColor", color);
            SetField(ref _batchBorderEnabled, false);
            RecordQuadVisualChange();
        }

        /// <summary>Updates a quad color while preserving custom material uniform names.</summary>
        public void SetQuadColor(string uniformName, Vector4 color)
        {
            ArgumentException.ThrowIfNullOrEmpty(uniformName);
            if (uniformName == "MatColor" && _intrinsicDefault && base.Material is null)
            {
                SetBatchSolidColor(color);
                return;
            }
            if (uniformName == "MatColor" && Material is { } material && OwnsDefaultMaterial(material))
            {
                SetBatchSolidColor(color);
                return;
            }
            Material?.SetVector4(uniformName, color);
            RecordQuadVisualChange();
        }

        /// <summary>Uses the shared desktop border shader with per-instance fill, outline and width.</summary>
        public void SetBatchBorderStyle(Vector4 fillColor, Vector4 outlineColor, float width)
        {
            if (UseWebGpuBatchOnly)
                throw new NotSupportedException("The shared border primitive requires a desktop shader backend.");
            if (!float.IsFinite(width) || width < 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (_intrinsicDefault && !TryDetachOwnedDefaultMaterial())
                throw new InvalidOperationException("Batch borders cannot replace an authored custom UI material.");
            if (_intrinsicDefault && base.Material is null)
            {
                if (_intrinsicImage is not null)
                    ReleaseCapturedImageSnapshot();
                SetField(ref _intrinsicColor, fillColor);
                SetField(ref _intrinsicImage, null);
                SetField(ref _batchImageUv, new Vector4(0, 0, 1, 1));
                SetField(ref _batchBorderEnabled, true);
                SetField(ref _batchBorderColor, outlineColor);
                SetField(ref _batchBorderWidth, width);
                RenderPass = (int)EDefaultRenderPass.TransparentForward;
                RecordQuadVisualChange();
                return;
            }
            if (Material is not { } material || !OwnsDefaultMaterial(material))
                throw new InvalidOperationException("Batch borders require the component's default UI quad material.");
            ConfigureOwnedBatchTransparency(material);
            material.SetVector4("MatColor", fillColor);
            SetField(ref _batchBorderEnabled, true);
            SetField(ref _batchBorderColor, outlineColor);
            SetField(ref _batchBorderWidth, width);
            RecordQuadVisualChange();
        }

        /// <summary>Uses an owned canvas image batch with tint and normalized source UV bounds.</summary>
        public void SetBatchImage(XRTexture2D texture, Vector4 tint, Vector4 uvBounds)
        {
            ArgumentNullException.ThrowIfNull(texture);
            ValidateBatchImageRegion(uvBounds);
            if (!_intrinsicDefault || UseWebGpuBatchOnly)
                throw new InvalidOperationException("Batch images require the desktop intrinsic UI quad; authored materials use SetQuadMaterial.");
            if (!TryGetDesktopImageProfile(texture, texture.ImportedColorSpace == ETextureColorSpace.Srgb, out string? reason))
                throw new NotSupportedException($"UI.ImageBatchUnsupported: {reason}");
            if (!TryDetachOwnedDefaultMaterial())
                throw new InvalidOperationException("Batch images cannot replace an authored custom UI material.");
            if (!ReferenceEquals(_intrinsicImage, texture))
                ReleaseCapturedImageSnapshot();
            SetField(ref _intrinsicImage, texture);
            SetField(ref _intrinsicColor, tint);
            SetField(ref _batchImageUv, uvBounds);
            SetField(ref _batchBorderEnabled, false);
            RenderPass = (int)EDefaultRenderPass.TransparentForward;
            RecordQuadVisualChange();
        }

        /// <summary>Current normalized source UV bounds for the intrinsic image primitive.</summary>
        public Vector4 BatchImageRegion => _batchImageUv;

        /// <summary>Changes only the per-instance UV rectangle of a batched image.</summary>
        public void SetBatchImageRegion(Vector4 uvBounds)
        {
            ValidateBatchImageRegion(uvBounds);
            if (!SetField(ref _batchImageUv, uvBounds)) return;
            RecordQuadVisualChange();
        }

        /// <summary>Clears only an image still borrowed from the expected atlas generation.</summary>
        public bool ClearBatchImage(XRTexture2D expected)
        {
            ArgumentNullException.ThrowIfNull(expected);
            if (!_intrinsicDefault)
                return false;
            XRMaterial? active = base.Material;
            if (active is not null && !OwnsDefaultMaterial(active) &&
                active.Textures.Any(texture => ReferenceEquals(texture, expected)))
                throw new InvalidOperationException("UI.AtlasUnbindFailed: a custom UI material still samples the atlas image.");
            if (!ReferenceEquals(_intrinsicImage, expected))
                return false;
            if (active is not null && !OwnsDefaultMaterial(active))
            {
                SetField(ref _intrinsicImage, (XRTexture2D?)null);
                if (ReferenceEquals(_intrinsicImage, expected) ||
                    base.Material is { } custom && custom.Textures.Any(texture => ReferenceEquals(texture, expected)))
                    throw new InvalidOperationException("UI.AtlasUnbindFailed: the atlas image remains bound after clearing.");
                ReleaseCapturedImageSnapshot();
                SetField(ref _batchImageUv, new Vector4(0, 0, 1, 1));
                RecordQuadVisualChange();
                return true;
            }
            if (!TryDetachOwnedDefaultMaterial())
                throw new InvalidOperationException("UI.AtlasUnbindFailed: the owned image material could not be detached.");
            SetBatchSolidColor(Vector4.Zero);
            if (ReferenceEquals(_intrinsicImage, expected) ||
                base.Material is { } remaining && remaining.Textures.Any(texture => ReferenceEquals(texture, expected)))
                throw new InvalidOperationException("UI.AtlasUnbindFailed: the atlas image remains bound after clearing.");
            return true;
        }

        private bool TryDetachOwnedDefaultMaterial()
        {
            XRMaterial? material = base.Material;
            if (material is null)
                return true;
            if (!OwnsDefaultMaterial(material))
                return false;
            base.Material = null;
            if (base.Material is not null)
                return false;
            Mesh?.Destroy();
            Mesh = null;
            return true;
        }

        private static void ValidateBatchImageRegion(Vector4 uv)
        {
            if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y) ||
                !float.IsFinite(uv.Z) || !float.IsFinite(uv.W) ||
                uv.X < 0 || uv.Y < 0 || uv.Z > 1 || uv.W > 1 ||
                uv.X >= uv.Z || uv.Y >= uv.W)
                throw new ArgumentOutOfRangeException(nameof(uv), "Image UV bounds must be finite ordered coordinates in [0,1].");
        }

        private void ConfigureOwnedBatchTransparency(XRMaterial material)
        {
            // The generic material helper enables depth and may replace the
            // owned default shader. Batched UI has its own exact raster profile.
            _renderParameters.BlendModeAllDrawBuffers = BlendMode.EnabledTransparent();
            _renderParameters.BlendModesPerDrawBuffer = null;
            _renderParameters.AlphaToCoverage = ERenderParamUsage.Disabled;
            _renderParameters.DepthTest.Enabled = ERenderParamUsage.Disabled;
            _renderParameters.DepthTest.UpdateDepth = false;
            material.RenderPass = (int)EDefaultRenderPass.TransparentForward;
            RenderPass = material.RenderPass;
        }

        public Vector4 BatchBorderColor
        {
            get => _batchBorderColor;
            set
            {
                if (!SetField(ref _batchBorderColor, value)) return;
                RecordQuadVisualChange();
            }
        }

        private void RecordQuadVisualChange()
        {
            if (SceneNode is not null && !SceneNode.IsTransformNull && Transform is UITransform transform)
                transform.RecordUIChange(UIChangeDomain.Visual);
        }

        private static readonly Lazy<string?> CanonicalImageShaderSource = new(static () =>
            XRShader.EngineShader(Path.Combine("Common", "UiTexturedForward.fs"), EShaderType.Fragment).Source.Text);

        private static bool UseWebGpuBatchOnly =>
            RuntimeEngineMaterialConstructionServices.Target == EngineMaterialConstructionTarget.WebGpuCooked ||
            AbstractRenderer.Current?.BackendId == RendererBackendId.WebGPU;

        public UIMaterialComponent()
        {
            if (RuntimeEngineMaterialConstructionServices.Target == EngineMaterialConstructionTarget.DesktopGlsl)
            {
                _intrinsicDefault = true;
                _allowedBatchUniformHandlers.Add(OnMaterialSettingUniforms);
                return;
            }
            try
            {
                InitializeQuadMaterial(CreateOwnedDefaultQuadMaterial(), false);
            }
            catch (Exception initializationFailure)
            {
                List<Exception>? cleanupFailures = null;
                for (int index = _ownedDefaultMaterials.Count - 1; index >= 0; index--)
                {
                    try { _ownedDefaultMaterials[index].Ownership.Dispose(); }
                    catch (Exception ex) { (cleanupFailures ??= []).Add(ex); }
                }
                try
                {
                    _renderParameters.Destroy(true);
                    if (!_renderParameters.IsDestroyed)
                        throw new InvalidOperationException("UI render options construction cleanup was vetoed.");
                }
                catch (Exception ex) { (cleanupFailures ??= []).Add(ex); }
                try { RenderInfo3D.Dispose(); }
                catch (Exception ex) { (cleanupFailures ??= []).Add(ex); }
                try { RenderInfo2D.Dispose(); }
                catch (Exception ex) { (cleanupFailures ??= []).Add(ex); }
                if (cleanupFailures is not null)
                {
                    cleanupFailures.Insert(0, initializationFailure);
                    throw new AggregateException("UI quad construction and cleanup failed.", cleanupFailures);
                }
                throw;
            }
        }

        private XRMaterial CreateOwnedDefaultQuadMaterial()
        {
            using ObjectCachePublicationScope publication = XRObjectBase.BeginIndependentObjectCachePublication();
            XRMaterial material = CreateDefaultQuadMaterial();
            _ownedDefaultMaterials.Add((material, publication.CompleteWithOwnership(), [.. material.Shaders]));
            return material;
        }

        private bool OwnsDefaultMaterial(XRMaterial material)
        {
            for (int index = 0; index < _ownedDefaultMaterials.Count; index++)
                if (ReferenceEquals(_ownedDefaultMaterials[index].Material, material))
                    return true;
            return false;
        }

        private bool HasOriginalDefaultShaders(XRMaterial material)
        {
            for (int index = 0; index < _ownedDefaultMaterials.Count; index++)
            {
                var owned = _ownedDefaultMaterials[index];
                if (!ReferenceEquals(owned.Material, material))
                    continue;
                if (material.Shaders.Count != owned.BatchShaders.Length)
                    return false;
                for (int shader = 0; shader < owned.BatchShaders.Length; shader++)
                    if (!ReferenceEquals(material.Shaders[shader], owned.BatchShaders[shader]))
                        return false;
                return true;
            }
            return false;
        }

        private static XRMaterial CreateDefaultQuadMaterial()
        {
            if (!UseWebGpuBatchOnly)
                return XRMaterial.CreateUnlitColorMaterialForward(Color.Magenta);

            // The shared screen batch owns the cooked draw material. This source-free
            // component material retains the authored MatColor used by buttons/panels.
            return new XRMaterial([new ShaderVector4(Color.Magenta.ToVector4(), "MatColor")], Array.Empty<XRShader>())
            {
                RenderPass = (int)EDefaultRenderPass.OpaqueForward
            };
        }

        /// <summary>Creates the shared tinted image material for desktop and cooked canvas UI.</summary>
        public static XRMaterial CreateImageMaterial(XRTexture2D texture, Vector4 tint)
        {
            ArgumentNullException.ThrowIfNull(texture);
            bool srgb = texture.ImportedColorSpace == ETextureColorSpace.Srgb;
            XRShader[] shaders = RuntimeEngineMaterialConstructionServices.Target switch
            {
                EngineMaterialConstructionTarget.DesktopGlsl =>
                    [XRShader.EngineShader(Path.Combine("Common", "UiTexturedForward.fs"), EShaderType.Fragment)],
                EngineMaterialConstructionTarget.WebGpuCooked => [],
                _ => throw new InvalidOperationException("Unsupported UI image material construction target."),
            };
            return new XRMaterial([new ShaderVector4(tint, "MatColor")], [texture], shaders)
            {
                RenderPass = (int)EDefaultRenderPass.TransparentForward,
                EngineSemantic = EngineMaterialSemanticIdentity.UIQuadBatchedTextureV1,
                SurfaceTextureBindings = [new(EMaterialTextureSemantic.BaseColor, texture,
                    IsSrgb: srgb,
                    WrapU: texture.UWrap, WrapV: texture.VWrap)]
            };
        }

        /// <summary>Resolves image encoding from material metadata retained alongside the cooked texture payload.</summary>
        public static bool UsesSrgbImage(XRMaterial material, XRTexture2D image)
        {
            MaterialSurfaceTextureBinding[] bindings = material.SurfaceTextureBindings;
            for (int index = 0; index < bindings.Length; index++)
            {
                MaterialSurfaceTextureBinding binding = bindings[index];
                if (binding.Semantic == EMaterialTextureSemantic.BaseColor && ReferenceEquals(binding.Texture, image))
                    return binding.IsSrgb;
            }
            return image.ImportedColorSpace == ETextureColorSpace.Srgb;
        }

        private static bool HasCanonicalImageBinding(XRMaterial material, XRTexture2D image)
        {
            MaterialSurfaceTextureBinding[] bindings = material.SurfaceTextureBindings;
            if (bindings.Length != 1)
                return false;
            MaterialSurfaceTextureBinding binding = bindings[0];
            return binding.Semantic == EMaterialTextureSemantic.BaseColor &&
                ReferenceEquals(binding.Texture, image) && binding.TexCoordSet == 0 &&
                binding.Channel == 0 && binding.WrapU == image.UWrap &&
                binding.WrapV == image.VWrap &&
                binding.UvScaleOffset == new Vector4(1, 1, 0, 0) &&
                binding.UvRotation == 0.0f;
        }

        /// <summary>Checks that an authored desktop stage is exactly the engine's image shader.</summary>
        public static bool HasCanonicalImageShader(XRMaterial material)
        {
            if (material.Shaders.Count == 0)
                return true;
            if (material.Shaders.Count != 1 || material.Shaders[0] is not XRShader shader ||
                shader.Type != EShaderType.Fragment || shader.SourceLanguage != ShaderSourceLanguage.Glsl ||
                shader.EntryPoint != "main")
                return false;
            string? canonical = CanonicalImageShaderSource.Value;
            return !string.IsNullOrEmpty(canonical) &&
                string.Equals(shader.Source.Text, canonical, StringComparison.Ordinal);
        }

        /// <summary>
        /// Validates the ordinary solid quad's source data before an offline target projection.
        /// Shader provenance is checked by the publisher; only the listed engine bounds callbacks may be omitted.
        /// </summary>
        public static bool TryGetWebGpuSolidCookProfile(XRMaterial material,
            IReadOnlyCollection<UIMaterialComponent> consumers, out string? reason)
        {
            reason = "solid UI requires the exact ordinary material, one finite MatColor, and the shared UI raster profile";
            if (material.GetType() != typeof(XRMaterial) || material.HasEngineSemantic ||
                material.Parameters.Length != 1 || material.Parameters[0] is not ShaderVector4 { Name: "MatColor" } color ||
                !float.IsFinite(color.Value.X) || !float.IsFinite(color.Value.Y) ||
                !float.IsFinite(color.Value.Z) || !float.IsFinite(color.Value.W) ||
                material.Textures.Count != 0 || material.SurfaceTextureBindings.Length != 0 ||
                !UIBatchCollector.HasWebGpuRasterProfile(material.RenderOptions))
                return false;
            reason = "solid UI cannot discard authored material features, pass overrides, or binding publishers";
            if (material.BillboardMode != EMeshBillboardMode.None || material.HasSettingVertexUniformHandlers ||
                material.HasSettingShadowUniformHandlers || material.BindingPublishers.Count != 0 ||
                material.PassSet.Passes.Length != 0 || material.PassSet.DisabledSourcePasses.Length != 0 ||
                material.PassSet.SourceRenderQueue != -1 || material.PassSet.QueuePriority != 0 ||
                material.PassSet.ForwardAddRenderOptions is not null ||
                material.PassSet.ForwardAddPolicy != EMaterialForwardAddPolicy.FoldedIntoForwardPlusBase ||
                material.UberAuthoredState.Features.Length != 0 || material.UberAuthoredState.Properties.Length != 0 ||
                !material.RequestedUberVariant.IsEmpty || material.TransparentTechniqueOverride is not null ||
                material.TransparencyMode != ETransparencyMode.Opaque ||
                material.EmissiveColor.HasValue || material.EmissionStrength.HasValue || material.Transmission != 0 ||
                material.TransmissionColor != Vector3.One || material.NormalScale != 1)
                return false;

            HashSet<Action<XRMaterialBase, XRRenderProgram>> handlers = [];
            reason = "solid UI lowering requires ordinary UIMaterialComponent consumers with only their engine bounds callbacks";
            if (consumers.Count == 0)
                return false;
            foreach (UIMaterialComponent consumer in consumers)
            {
                // Exact components cannot override OnMaterialSettingUniforms or batch behavior.
                if (consumer.GetType() != typeof(UIMaterialComponent) || !ReferenceEquals(consumer.Material, material))
                    return false;
                handlers.Add(consumer.OnMaterialSettingUniforms);
            }
            // Empty surface metadata can install this engine publisher during YAML reload.
            // The feature guard above proves it has no authored emission to preserve.
            if (!material.HasOnlyStandardSurfaceAndUniformHandlers(handlers))
                return false;
            reason = null;
            return true;
        }

        /// <summary>Checks the authored image and sampler against the sampled WebGPU UI binding.</summary>
        public static bool TryGetWebGpuImageProfile(XRTexture2D texture, out string? reason)
            => TryGetImageProfile(texture, allowHardwareSrgb: false, out reason);

        /// <summary>Requires desktop image metadata to match hardware sampling on both GL and Vulkan.</summary>
        public static bool TryGetDesktopImageProfile(XRTexture2D texture, bool isSrgb, out string? reason)
        {
            if (!TryGetImageProfile(texture, allowHardwareSrgb: true, out reason))
                return false;
            return TryGetDesktopImageColorStorage(texture, isSrgb, out reason);
        }

        private static bool TryGetDesktopImageColorStorage(XRTexture2D texture, bool isSrgb, out string? reason)
        {
            reason = null;
            ESizedInternalFormat required = isSrgb
                ? ESizedInternalFormat.Srgb8Alpha8 : ESizedInternalFormat.Rgba8;
            bool hasKnownColorStorage = texture.SizedInternalFormat is
                ESizedInternalFormat.Rgba8 or ESizedInternalFormat.Srgb8Alpha8;
            if (hasKnownColorStorage && texture.SizedInternalFormat != required)
            {
                reason = "the image's sRGB binding must match its Rgba8 or Srgb8Alpha8 GPU storage";
                return false;
            }
            if (hasKnownColorStorage && texture.Resizable && texture.Mipmaps.Length > 0 &&
                texture.Mipmaps[0].InternalFormat is EPixelInternalFormat.Rgba8 or EPixelInternalFormat.Srgb8Alpha8 &&
                texture.Mipmaps[0].InternalFormat !=
                    (isSrgb ? EPixelInternalFormat.Srgb8Alpha8 : EPixelInternalFormat.Rgba8))
            {
                reason = "a resizable image's first mip format must match its GPU storage for Vulkan sampling";
                return false;
            }
            return true;
        }

        private static bool TryGetImageProfile(XRTexture2D texture, bool allowHardwareSrgb, out string? reason)
        {
            reason = null;
            if (texture.MultiSampleCount != 1 || texture.Width == 0 || texture.Height == 0 ||
                texture.Mipmaps.Length == 0 || texture.AutoGenerateMipmaps ||
                (texture.SizedInternalFormat != ESizedInternalFormat.Rgba8 &&
                 (!allowHardwareSrgb || texture.SizedInternalFormat != ESizedInternalFormat.Srgb8Alpha8)))
                reason = allowHardwareSrgb
                    ? "the image must be a single-sample RGBA8 or sRGB8-alpha8 texture with explicit mips"
                    : "the image must be a single-sample RGBA8 texture with explicit mips";
            else if (texture.EnableComparison || texture.SamplerName is not (null or "Texture0") || texture.LodBias != 0 ||
                texture.UWrap is not (ETexWrapMode.Repeat or ETexWrapMode.MirroredRepeat or ETexWrapMode.ClampToEdge) ||
                texture.VWrap is not (ETexWrapMode.Repeat or ETexWrapMode.MirroredRepeat or ETexWrapMode.ClampToEdge) ||
                texture.MinFilter is < ETexMinFilter.Nearest or > ETexMinFilter.LinearMipmapLinear ||
                texture.MagFilter is not (ETexMagFilter.Nearest or ETexMagFilter.Linear))
                reason = "the image requires the indexed Texture0 binding and an ordinary WebGPU-compatible sampler";
            else if (texture.LargestMipmapLevel < 0 ||
                texture.LargestMipmapLevel > Math.Min(texture.Mipmaps.Length - 1, texture.SmallestAllowedMipmapLevel) ||
                texture.MinLOD > 32 || texture.MaxLOD < 0 || texture.MinLOD > texture.MaxLOD)
                reason = "the image sampled mip or LOD range is empty";
            else if (!float.IsFinite(texture.MaxAnisotropy) || texture.MaxAnisotropy < 1 ||
                texture.MaxAnisotropy > 16 || texture.MaxAnisotropy != MathF.Truncate(texture.MaxAnisotropy) ||
                texture.MaxAnisotropy > 1 && (texture.MinFilter != ETexMinFilter.LinearMipmapLinear ||
                    texture.MagFilter != ETexMagFilter.Linear))
                reason = "image anisotropy must be an integer from one to sixteen";
            else
            {
                bool mipmapped = texture.MinFilter is not (ETexMinFilter.Nearest or ETexMinFilter.Linear);
                if (mipmapped ? Math.Max(texture.MinLOD, 0) > Math.Min(texture.MaxLOD, 32) :
                    texture.MinLOD > 0 || texture.MaxLOD < 0)
                    reason = "the image sampler LOD clamp excludes its sampled levels";
                for (int mip = 0; reason is null && mip < texture.Mipmaps.Length; mip++)
                {
                    Mipmap2D level = texture.Mipmaps[mip];
                    if (level.Width != Math.Max(1u, texture.Width >> mip) ||
                        level.Height != Math.Max(1u, texture.Height >> mip))
                    {
                        reason = "the image mips must form a complete 2D chain";
                        break;
                    }
                    if (level.Data is null)
                        continue;
                    if (level.PixelFormat != EPixelFormat.Rgba || level.PixelType != EPixelType.UnsignedByte)
                        reason = "the image mip bytes require the exact RGBA8 upload format";
                    else if (level.Data.Address == VoidPtr.Zero ||
                        level.Data.Length != (ulong)level.Width * level.Height * 4u)
                        reason = "the image mip bytes are missing or do not match the declared extent";
                }
            }
            return reason is null;
        }

        public UIMaterialComponent(XRMaterial quadMaterial, bool flipVerticalUVCoord = false)
        {
            _renderParametersEscaped = true;
            InitializeQuadMaterial(quadMaterial, flipVerticalUVCoord);
        }

        private void InitializeQuadMaterial(XRMaterial quadMaterial, bool flipVerticalUVCoord)
        {
            _allowedBatchUniformHandlers.Add(OnMaterialSettingUniforms);
            _flipVerticalUVCoord = flipVerticalUVCoord;
            RenderPass = quadMaterial.RenderPass;
            quadMaterial.RenderOptions = _renderParameters;
            Material = quadMaterial;
            RemakeMesh(quadMaterial);
            ObserveMatColor();
        }

        private bool _flipVerticalUVCoord = false;
        public bool FlipVerticalUVCoord
        {
            get => _flipVerticalUVCoord;
            set => SetField(ref _flipVerticalUVCoord, value);
        }

        private static readonly Lazy<XRMesh> SharedQuadMesh = new(static () => CreateSharedQuadMesh(false));
        private static readonly Lazy<XRMesh> SharedFlippedQuadMesh = new(static () => CreateSharedQuadMesh(true));

        private static XRMesh CreateSharedQuadMesh(bool flipVerticalUVCoord)
        {
            // Lazy shared geometry belongs to the process, not the first catalog/component
            // whose construction happens to request it.
            using ObjectCachePublicationScope publication = XRObjectBase.BeginIndependentObjectCachePublication();
            XRMesh mesh = XRMesh.Create(VertexQuad.PosZ(1.0f, true, 0.0f, flipVerticalUVCoord));
            publication.Complete();
            return mesh;
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change && propName == nameof(Material) && _observedMatColor is not null)
            {
                _observedMatColor.ValueChanged -= OnMatColorValueChanged;
                _observedMatColor = null;
            }
            return change;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Material):
                    if (base.Material is { } assigned && Mesh is null)
                        RemakeMesh(assigned);
                    ObserveMatColor();
                    break;
                case nameof(FlipVerticalUVCoord):
                    if (base.Material is not null)
                        RemakeMesh();
                    break;
                case nameof(DisableBatching):
                    RenderCommand2D.MarkDirty();
                    RenderCommand3D.MarkDirty();
                    break;
            }
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            if (!_intrinsicDefault || base.Material is not null)
                RemakeMesh();
            ObserveMatColor();
        }

        protected override void OnComponentDeactivated()
        {
            if (_observedMatColor is not null)
            {
                _observedMatColor.ValueChanged -= OnMatColorValueChanged;
                _observedMatColor = null;
            }
            ReleaseCapturedImageSnapshot();
            base.OnComponentDeactivated();
        }

        protected override void OnDestroying()
        {
            try
            {
                ReleaseCapturedImageSnapshot();
                if (_observedMatColor is not null)
                {
                    _observedMatColor.ValueChanged -= OnMatColorValueChanged;
                    _observedMatColor = null;
                }
                Mesh?.Destroy();
                Mesh = null;
                base.OnDestroying();
            }
            finally
            {
                List<Exception>? failures = null;
                for (int index = _ownedDefaultMaterials.Count - 1; index >= 0; index--)
                {
                    try { _ownedDefaultMaterials[index].Ownership.Dispose(); }
                    catch (Exception ex) { (failures ??= []).Add(ex); }
                }
                if (!_renderParametersEscaped)
                {
                    try
                    {
                        _renderParameters.Destroy(true);
                        if (!_renderParameters.IsDestroyed)
                            throw new InvalidOperationException("UI render options destruction was vetoed.");
                    }
                    catch (Exception ex) { (failures ??= []).Add(ex); }
                }
                if (failures is not null)
                    throw new AggregateException("UI default material storage could not be released.", failures);
            }
        }

        public void SetQuadMaterial(XRMaterial material)
        {
            if (!OwnsDefaultMaterial(material))
                _renderParametersEscaped = true;
            RenderPass = material.RenderPass;
            material.RenderOptions = _renderParameters;
            if (Mesh is null)
                RemakeMesh(material);
            else
            {
                Mesh.Material = material;
                Material = material;
            }
            RenderCommand2D.MarkDirty();
            RenderCommand3D.MarkDirty();
        }

        public void SetBlendModeAllDrawBuffers(BlendMode? blendMode)
        {
            if (_intrinsicDefault && base.Material is null)
                EnsureLegacyMaterial();
            if (SceneNode is not null && !SceneNode.IsTransformNull && Transform is UITransform transform)
                transform.RecordUIChange(UIChangeDomain.Visual | UIChangeDomain.Topology);
            _renderParameters.BlendModeAllDrawBuffers = blendMode;
            if (Material is not null)
            {
                if (!OwnsDefaultMaterial(Material))
                    _renderParametersEscaped = true;
                Material.RenderOptions = _renderParameters;
            }

            RenderCommand2D.MarkDirty();
            RenderCommand3D.MarkDirty();
        }

        private bool _disableBatching;
        public bool DisableBatching
        {
            get => _disableBatching;
            set => SetField(ref _disableBatching, value);
        }

        private void RemakeMesh()
        {
            if (base.Material is null)
            {
                var mat = CreateOwnedDefaultQuadMaterial();
                mat.RenderOptions = _renderParameters;
                mat.SetVector4("MatColor", _intrinsicColor);
                Material = mat;
            }
            else
                RemakeMesh(base.Material);
        }

        private void EnsureLegacyMaterial()
        {
            if (base.Material is not null)
                return;
            if (_batchBorderEnabled || _intrinsicImage is not null && _batchImageUv != new Vector4(0, 0, 1, 1))
                throw new NotSupportedException("UI.ImageRegionRequiresBatch: intrinsic borders and image subregions have no exact single-draw legacy material.");
            if (_intrinsicImage is not null)
            {
                using ObjectCachePublicationScope publication = XRObjectBase.BeginIndependentObjectCachePublication();
                XRMaterial imageMaterial = CreateImageMaterial(_intrinsicImage, _intrinsicColor);
                _ownedDefaultMaterials.Add((imageMaterial, publication.CompleteWithOwnership(), [.. imageMaterial.Shaders]));
                imageMaterial.RenderOptions = _renderParameters;
                Material = imageMaterial;
            }
            else
                RemakeMesh();
        }

        private void RemakeMesh(XRMaterial material)
        {
            Mesh?.Destroy();
            Mesh = new XRMeshRenderer(FlipVerticalUVCoord ? SharedFlippedQuadMesh.Value : SharedQuadMesh.Value, material)
            {
                GenerationPriority = EMeshGenerationPriority.RenderPipeline,
                CaptureUniformsOnRender = true
            };
        }

        private readonly RenderingParameters _renderParameters = new()
        {
            CullMode = ECullMode.None,
            DepthTest = new()
            {
                Enabled = ERenderParamUsage.Disabled,
                Function = EComparison.Always
            },
            StencilTest = new()
            {
                Enabled = ERenderParamUsage.Disabled
            },
            BlendModeAllDrawBuffers = BlendMode.EnabledTransparent(),
        };

        public XRTexture? Texture(int index)
            => (Material?.Textures?.IndexInRange(index) ?? false)
                ? Material.Textures[index]
                : null;

        public T? Texture<T>(int index) where T : XRTexture
            => (Material?.Textures?.IndexInRange(index) ?? false)
                ? Material.Textures[index] as T
                : null;

        /// <summary>
        /// Retrieves the linked material's uniform parameter at the given index.
        /// Use this to set uniform values to be passed to the shader.
        /// </summary>
        public T2? Parameter<T2>(int index) where T2 : ShaderVar
        {
            if (_intrinsicDefault && Mesh is null)
                EnsureLegacyMaterial();
            return Mesh?.Parameter<T2>(index);
        }

        /// <summary>
        /// Retrieves the linked material's uniform parameter with the given name.
        /// Use this to set uniform values to be passed to the shader.
        /// </summary>
        public T2? Parameter<T2>(string name) where T2 : ShaderVar
        {
            if (_intrinsicDefault && Mesh is null)
                EnsureLegacyMaterial();
            return Mesh?.Parameter<T2>(name);
        }

        protected override Matrix4x4 GetRenderWorldMatrix(UIBoundableTransform tfm)
        {
            var w = tfm.ActualWidth;
            var h = tfm.ActualHeight;
            return Matrix4x4.CreateScale(w, h, 1.0f) * base.GetRenderWorldMatrix(tfm);
        }

        protected override Matrix4x4 CapturePublicationModelMatrix(
            in UILayoutNodeOutput output, in Matrix4x4 canvasMatrix)
            => Matrix4x4.CreateScale(output.ActualSize.X, output.ActualSize.Y, 1.0f) * canvasMatrix;

        #region Batched Rendering

        protected override bool CanCapturePublicationUniforms => GetType() == typeof(UIMaterialComponent);

        /// <summary>Captures the quad source values for one canvas publication.</summary>
        public override UIRenderRecord CapturePublicationRenderRecord(in UILayoutNodeOutput output)
        {
            UIBoundableTransform bounds = BoundableTransform;
            UICanvasComponent? canvas = bounds.ParentCanvas?.SceneNode?.GetComponent<UICanvasComponent>();
            bool directWorld = bounds.ParentCanvas is { DrawSpace: not ECanvasDrawSpace.Screen } &&
                canvas is not null && !canvas.UseOffscreenRenderingForNonScreenSpaces();
            bool requiresLegacy = directWorld || canvas?.BatchCollector.Enabled == false;
            if (_intrinsicDefault && base.Material is null && requiresLegacy &&
                (_batchBorderEnabled || _intrinsicImage is not null && _batchImageUv != new Vector4(0, 0, 1, 1)))
            {
                UIRenderRecord unsupported = base.CapturePublicationRenderRecord(in output);
                return unsupported with
                {
                    Visible = output.Visible && IsActiveInHierarchy,
                    CaptureFailure = "UI.ImageRegionRequiresBatch: intrinsic borders and image subregions need an admitted batch or an exact custom draw adapter.",
                };
            }
            if (_intrinsicDefault && base.Material is null &&
                requiresLegacy)
                EnsureLegacyMaterial();
            XRMaterial? material = base.Material;
            XRTexture2D? texture = material is null
                ? (_intrinsicDefault ? _intrinsicImage : null)
                : material.Textures.Count == 1 ? material.Textures[0] as XRTexture2D : null;
            Vector4 uv = FlipVerticalUVCoord
                ? new(_batchImageUv.X, _batchImageUv.W, _batchImageUv.Z, _batchImageUv.Y)
                : _batchImageUv;
            UIRenderRecord record = base.CapturePublicationRenderRecord(in output) with
            {
                QuadColor = material?.Parameter<ShaderVector4>("MatColor")?.Value ?? _intrinsicColor,
                Texture = texture,
                UV = uv,
                ImageSrgb = texture is not null && (material is not null
                    ? UsesSrgbImage(material, texture)
                    : texture.ImportedColorSpace == ETextureColorSpace.Srgb),
                IsBorder = _batchBorderEnabled,
                BorderColor = _batchBorderColor,
                BorderWidth = _batchBorderWidth,
            };
            if (material is null && _intrinsicImage is not null && output.Visible && IsActiveInHierarchy &&
                !TryGetDesktopImageProfile(_intrinsicImage, record.ImageSrgb, out string? imageFailure))
            {
                ReleaseCapturedImageSnapshot();
                return record with { Visible = true, CaptureFailure = $"UI.ImageBatchUnsupported: {imageFailure}" };
            }
            if (record.Visible && !record.SupportsBatch &&
                (_batchBorderEnabled || _batchImageUv != new Vector4(0, 0, 1, 1)))
                return record with { CaptureFailure = "UI.ImageRegionRequiresBatch: border and cropped image primitives require an admitted batch or an explicit custom material adapter." };
            if (record.Visible && !UseWebGpuBatchOnly && material is not null && texture is not null &&
                material.EngineSemantic == EngineMaterialSemanticIdentity.UIQuadBatchedTextureV1 &&
                !TryGetDesktopImageColorStorage(texture, UsesSrgbImage(material, texture), out string? colorFailure))
                return record with { CaptureFailure = $"UI.ImageColorSpaceUnsupported: {colorFailure}" };
            if (!record.Visible || !record.SupportsBatch || texture is null)
                return record;

            UITextureSnapshot? snapshot = _capturedImageSnapshot;
            if (!ReferenceEquals(_capturedImageSource, texture) || snapshot is null || !snapshot.Matches(texture))
            {
                if (!UITextureSnapshot.TryAcquire(texture, out UITextureSnapshot? replacement, out string? failure))
                {
                    ReleaseCapturedImageSnapshot();
                    return record with { CaptureFailure = failure };
                }
                ReleaseCapturedImageSnapshot();
                snapshot = replacement;
                _capturedImageSnapshot = snapshot;
                _capturedImageSource = texture;
                snapshot!.SourceChanged += OnCapturedImageSourceChanged;
            }
            return record with { Texture = snapshot!.Texture, TextureSnapshot = snapshot };
        }

        /// <summary>
        /// Material quads support solid batching. Source-free image quads on the
        /// cooked WebGPU target are grouped by their shared 2D texture.
        /// </summary>
        public override bool SupportsBatchedRendering
        {
            get
            {
                XRMaterial? material = base.Material;
                if (DisableBatching)
                    return false;
                if (material is null)
                    return _intrinsicDefault && !UseWebGpuBatchOnly &&
                        GetType() == typeof(UIMaterialComponent) &&
                        (_intrinsicImage is null || TryGetDesktopImageProfile(_intrinsicImage,
                            _intrinsicImage.ImportedColorSpace == ETextureColorSpace.Srgb, out _));
                if (_batchBorderEnabled && UseWebGpuBatchOnly)
                    return false;

                if (material.Parameter<ShaderVector4>("MatColor") is null)
                {
                    if (!_reportedMissingBatchColor)
                    {
                        _reportedMissingBatchColor = true;
                        Debug.Log(ELogCategory.UI, "UI quad '{0}' has no MatColor. Use an explicit color material or a normal render command.", SceneNode?.Name ?? GetType().Name);
                    }
                    return false;
                }

                bool ownedSolidSemantic = !material.HasEngineSemantic ||
                    UseWebGpuBatchOnly && OwnsDefaultMaterial(material) &&
                    material.EngineSemantic == EngineMaterialSemanticIdentity.UnlitColorV1;
                bool sourceFreeSolid = HasOriginalDefaultShaders(material) && material.Textures.Count == 0 &&
                    material.SurfaceTextureBindings.Length == 0 && ownedSolidSemantic;
                XRTexture2D? image = material.Textures.Count == 1
                    ? material.Textures[0] as XRTexture2D : null;
                bool canonicalImage = image is not null &&
                    (UseWebGpuBatchOnly ? TryGetWebGpuImageProfile(image, out _) :
                        TryGetDesktopImageProfile(image, UsesSrgbImage(material, image), out _)) &&
                    material.EngineSemantic == EngineMaterialSemanticIdentity.UIQuadBatchedTextureV1 &&
                    HasCanonicalImageShader(material) &&
                    (UseWebGpuBatchOnly || material.Shaders.Count == 1) &&
                    HasCanonicalImageBinding(material, image);
                return
                    GetType() == typeof(UIMaterialComponent) &&
                    material.HasOnlyStandardSurfaceAndUniformHandlers(_allowedBatchUniformHandlers) &&
                    (sourceFreeSolid || canonicalImage &&
                     (SceneNode?.Transform is not UIBoundableTransform imageBounds ||
                      imageBounds.GetCanvasTransform() is not { DrawSpace: not ECanvasDrawSpace.Screen } ||
                      !image!.RequiresStorageUsage || !UsesSrgbImage(material, image))) &&
                    (!UseWebGpuBatchOnly || material.Textures.Count != 0 || material.Shaders.Count == 0) &&
                    material.Parameters.Length == 1 &&
                    material.Parameters[0] is ShaderVector4 { Name: "MatColor" } &&
                    material.BillboardMode == EMeshBillboardMode.None &&
                    !material.HasSettingVertexUniformHandlers &&
                    !material.HasSettingShadowUniformHandlers &&
                    material.BindingPublishers.Count == 0 &&
                    material.PassSet.Passes.Length == 0 &&
                    material.PassSet.DisabledSourcePasses.Length == 0 &&
                    material.PassSet.SourceRenderQueue == -1 &&
                    material.PassSet.QueuePriority == 0 &&
                    material.PassSet.ForwardAddRenderOptions is null &&
                    material.UberAuthoredState.Features.Length == 0 &&
                    material.UberAuthoredState.Properties.Length == 0 &&
                    material.RequestedUberVariant.IsEmpty &&
                    material.TransparentTechniqueOverride is null &&
                    UIBatchCollector.HasWebGpuRasterProfile(material.RenderOptions);
            }
        }

        protected override bool RegisterWithBatchCollector(UIBatchCollector collector, RenderCommandCollection passes)
        {
            UIBoundableTransform tfm = BoundableTransform;
            UICanvasComponent? canvas = tfm.ParentCanvas?.SceneNode?.GetComponent<UICanvasComponent>();
            if (canvas is null || !canvas.TryGetCollectingRenderRecord(RenderInfo2D.MembershipId, out UIRenderRecord record) ||
                !record.Visible || !record.SupportsBatch)
                return false;
            Matrix4x4 worldMatrix = record.WorldMatrix;
            Vector4 color = record.QuadColor;
            Vector4 bounds = record.Bounds;
            collector.AddMaterialQuad(record.RenderPass, record.ZIndex, passes, in worldMatrix, in color, in bounds,
                record.Crop, record.Texture, record.UV, record.ImageSrgb, record.IsBorder,
                record.BorderColor, record.BorderWidth);
            return true;
        }

        #endregion
    }
}
