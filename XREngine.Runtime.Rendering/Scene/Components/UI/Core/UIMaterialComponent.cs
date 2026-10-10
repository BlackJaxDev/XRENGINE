using XREngine.Extensions;
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
        private readonly List<(XRMaterial Material, ObjectCacheOwnership Ownership)> _ownedDefaultMaterials = [];
        private bool _renderParametersEscaped;

        private static readonly Lazy<string?> CanonicalImageShaderSource = new(static () =>
            XRShader.EngineShader(Path.Combine("Common", "UiTexturedForward.fs"), EShaderType.Fragment).Source.Text);

        private static bool UseWebGpuBatchOnly =>
            RuntimeEngineMaterialConstructionServices.Target == EngineMaterialConstructionTarget.WebGpuCooked ||
            AbstractRenderer.Current?.BackendId == RendererBackendId.WebGPU;

        public UIMaterialComponent()
        {
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
            _ownedDefaultMaterials.Add((material, publication.CompleteWithOwnership()));
            return material;
        }

        private bool OwnsDefaultMaterial(XRMaterial material)
        {
            for (int index = 0; index < _ownedDefaultMaterials.Count; index++)
                if (ReferenceEquals(_ownedDefaultMaterials[index].Material, material))
                    return true;
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
                    IsSrgb: texture.ImportedColorSpace == ETextureColorSpace.Srgb,
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
        {
            reason = null;
            if (texture.MultiSampleCount != 1 || texture.Width == 0 || texture.Height == 0 ||
                texture.Mipmaps.Length == 0 || texture.AutoGenerateMipmaps ||
                texture.SizedInternalFormat != ESizedInternalFormat.Rgba8)
                reason = "the image must be a single-sample RGBA8 texture with explicit mips";
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
            _flipVerticalUVCoord = flipVerticalUVCoord;
            RenderPass = quadMaterial.RenderPass;
            quadMaterial.RenderOptions = _renderParameters;
            RemakeMesh(quadMaterial);
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

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(FlipVerticalUVCoord):
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
            RemakeMesh();
        }

        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
        }

        protected override void OnDestroying()
        {
            try
            {
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
            if (Material is null)
            {
                var mat = CreateOwnedDefaultQuadMaterial();
                mat.RenderOptions = _renderParameters;
                Material = mat;
            }
            RemakeMesh(Material);
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
            => Mesh?.Parameter<T2>(index);

        /// <summary>
        /// Retrieves the linked material's uniform parameter with the given name.
        /// Use this to set uniform values to be passed to the shader.
        /// </summary>
        public T2? Parameter<T2>(string name) where T2 : ShaderVar
            => Mesh?.Parameter<T2>(name);

        protected override Matrix4x4 GetRenderWorldMatrix(UIBoundableTransform tfm)
        {
            var w = tfm.ActualWidth;
            var h = tfm.ActualHeight;
            return Matrix4x4.CreateScale(w, h, 1.0f) * base.GetRenderWorldMatrix(tfm);
        }

        #region Batched Rendering

        /// <summary>
        /// Material quads support solid batching. Source-free image quads on the
        /// cooked WebGPU target are grouped by their shared 2D texture.
        /// </summary>
        public override bool SupportsBatchedRendering
        {
            get
            {
                XRMaterial? material = Material;
                return !DisableBatching &&
                    (!ClipToBounds || UseWebGpuBatchOnly) &&
                    (material?.Textures is null || material.Textures.Count == 0 ||
                     UseWebGpuBatchOnly && material.Textures.Count == 1 &&
                     material.Textures[0] is XRTexture2D image && TryGetWebGpuImageProfile(image, out _) &&
                     (BoundableTransform.GetCanvasTransform() is not { DrawSpace: not ECanvasDrawSpace.Screen } ||
                      !image.RequiresStorageUsage || !UsesSrgbImage(material, image))) &&
                    (!UseWebGpuBatchOnly ||
                     material is not null &&
                     (material.Textures.Count == 0
                         ? !material.HasEngineSemantic && material.Shaders.Count == 0
                         : material.EngineSemantic == EngineMaterialSemanticIdentity.UIQuadBatchedTextureV1 &&
                           HasCanonicalImageShader(material)) &&
                     material.Parameters.Length == 1 &&
                     material.Parameters[0] is ShaderVector4 { Name: "MatColor" } &&
                     UIBatchCollector.HasWebGpuRasterProfile(material.RenderOptions));
            }
        }

        protected override bool RegisterWithBatchCollector(UIBatchCollector collector, RenderCommandCollection passes)
        {
            var tfm = BoundableTransform;
            var worldMatrix = GetRenderCanvasMatrix(tfm);

            // Read the per-instance color from the material's MatColor parameter
            var colorParam = Material?.Parameter<ShaderVector4>("MatColor");
            var color = colorParam?.Value ?? new Vector4(1.0f, 0.0f, 1.0f, 1.0f);

            var bottomLeft = tfm.ActualLocalBottomLeftTranslation;
            var bounds = new Vector4(bottomLeft.X, bottomLeft.Y, tfm.ActualWidth, tfm.ActualHeight);

            XRTexture2D? texture = UseWebGpuBatchOnly && Material?.Textures.Count == 1
                ? Material.Textures[0] as XRTexture2D : null;
            Vector4 uv = new(0.0f, FlipVerticalUVCoord ? 1.0f : 0.0f,
                1.0f, FlipVerticalUVCoord ? 0.0f : 1.0f);
            collector.AddMaterialQuad(RenderPass, RenderCommand2D.ZIndex, passes, in worldMatrix, in color, in bounds,
                UIClipRegion.ResolveCrop(tfm, ClipToBounds),
                texture, uv, imageSrgb: texture is not null && Material is not null && UsesSrgbImage(Material, texture));
            return true;
        }

        #endregion
    }
}
