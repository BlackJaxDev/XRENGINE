using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI.State;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Applies the nested canvas crop to the live draw of a direct-world element.
    /// </summary>
    /// <remarks>
    /// A direct-world element draws through <see cref="RenderCommand3D"/>, which has no scissor. When the element or a
    /// clipping ancestor has a crop, the component sets an owned clip material as the command material override. The
    /// clip material uses the source shaders with the canonical fragment shader replaced by its crop variant. The
    /// variant discards each fragment outside the crop in canvas-local pixels, the same rule as the retained route.
    /// The clip material owns no shader parameters. Its uniform callback writes the source parameters, the crop, and
    /// the model-to-canvas matrix on each draw, so a program that two materials share always gets current values.
    /// When the source material cannot use the crop contract, the element keeps its unclipped draw and the component
    /// reports a failure code once for each change of state.
    /// </remarks>
    public abstract partial class UIRenderableComponent
    {
        private sealed class LiveWorldClipVariant(long revision, XRShader shader)
        {
            public long Revision { get; } = revision;
            public XRShader Shader { get; } = shader;
        }

        private static readonly ConditionalWeakTable<XRShader, LiveWorldClipVariant> LiveWorldClipVariants = new();

        private const string LiveWorldClipShaderRequired =
            "UI.WorldClip.ShaderRequired: the nested world crop needs a canonical UI shader set; the element draws unclipped.";
        private const string LiveWorldClipOverrideUnsupported =
            "UI.WorldClip.OverrideUnsupported: an authored world material override cannot take the nested crop; the element draws unclipped.";
        private const string LiveWorldClipRange =
            "UI.WorldClip.Range: the nested world crop or its canvas transform is out of range; the element draws unclipped.";
        private const string LiveWorldClipUniformConflict =
            "UI.WorldClip.UniformConflict: the source material uses a reserved crop uniform; the element draws unclipped.";

        private XRMaterial? _liveWorldClipMaterial;
        private XRMaterial? _liveWorldClipSource;
        private XRShader[] _liveWorldClipSourceShaders = [];
        private long[] _liveWorldClipShaderRevisions = [];
        private Vector4 _liveWorldClipMinMax;
        private Matrix4x4 _liveWorldClipModelToCanvas = Matrix4x4.Identity;
        private string? _liveWorldClipFailure;

        /// <summary>Gets the owned clip material that the live direct-world draw uses, or null.</summary>
        internal XRMaterial? LiveWorldClipMaterial => _liveWorldClipMaterial;

        /// <summary>Gets the crop minimum and maximum, in canvas-local pixels, that the clip material writes.</summary>
        internal Vector4 LiveWorldClipMinMax => _liveWorldClipMinMax;

        /// <summary>Gets the model-to-canvas matrix that the clip material writes.</summary>
        internal Matrix4x4 LiveWorldClipModelToCanvas => _liveWorldClipModelToCanvas;

        /// <summary>
        /// Gets whether the 3D command has a material override that the component does not own.
        /// The live clip material is not an authored override.
        /// </summary>
        internal bool HasAuthoredWorldMaterialOverride
            => RenderCommand3D.MaterialOverride is { } material && !ReferenceEquals(material, _liveWorldClipMaterial);

        /// <summary>
        /// Recalculates the nested crop of the live direct-world draw, and sets or removes the clip material.
        /// </summary>
        internal void RefreshLiveWorldClip()
        {
            if (_worldCollectionTerminal || _worldCollectionSuppressed || _registeredDirectWorldCanvas is null ||
                !IsActiveInHierarchy || SceneNode is null || SceneNode.IsTransformNull ||
                Transform is not UIBoundableTransform bounds)
            {
                ReleaseLiveWorldClip();
                _liveWorldClipFailure = null;
                return;
            }

            BoundingRectangle? crop = UIClipRegion.ResolveCrop(bounds, ClipToBounds);
            if (crop is not { } region)
            {
                ReleaseLiveWorldClip();
                _liveWorldClipFailure = null;
                return;
            }

            if (HasAuthoredWorldMaterialOverride)
            {
                ReleaseLiveWorldClip(clearOverride: false);
                ReportLiveWorldClipFailure(LiveWorldClipOverrideUnsupported);
                return;
            }

            XRMaterial? source = RenderCommand3D.Mesh?.Material;
            if (source is null)
            {
                ReleaseLiveWorldClip();
                return;
            }

            if (!UIWorldCropShaderContract.TryCapture(region, GetRenderCanvasMatrix(bounds), out UIWorldCropCapture capture))
            {
                ReleaseLiveWorldClip();
                ReportLiveWorldClipFailure(LiveWorldClipRange);
                return;
            }

            if (!IsLiveWorldClipCurrent(source) && !TryBuildLiveWorldClip(source))
                return;

            _liveWorldClipMinMax = capture.MinMax;
            _liveWorldClipModelToCanvas = capture.ModelToCanvas;
            _liveWorldClipFailure = null;
            if (!ReferenceEquals(RenderCommand3D.MaterialOverride, _liveWorldClipMaterial))
                RenderCommand3D.MaterialOverride = _liveWorldClipMaterial;
        }

        /// <summary>Recalculates the live crop of this component and of each renderable below it.</summary>
        private void RefreshLiveWorldClipInSubtree()
        {
            RefreshLiveWorldClip();
            if (SceneNode is { IsTransformNull: false } && Transform is { } transform)
                RefreshLiveWorldClipBelow(transform);
        }

        private static void RefreshLiveWorldClipBelow(TransformBase transform)
        {
            var children = transform.Children;
            for (int i = 0; i < children.Count; i++)
            {
                TransformBase child = children[i];
                var components = child.SceneNode?.Components;
                if (components is not null)
                    for (int c = 0; c < components.Count; c++)
                        if (components[c] is UIRenderableComponent renderable)
                            renderable.RefreshLiveWorldClip();
                RefreshLiveWorldClipBelow(child);
            }
        }

        private bool IsLiveWorldClipCurrent(XRMaterial source)
        {
            if (_liveWorldClipMaterial is not { } clip || !ReferenceEquals(_liveWorldClipSource, source) ||
                source.Shaders.Count != _liveWorldClipSourceShaders.Length ||
                source.Textures.Count != clip.Textures.Count ||
                !ReferenceEquals(source.RenderOptions, clip.RenderOptions) ||
                source.RenderPass != clip.RenderPass || source.TransparencyMode != clip.TransparencyMode ||
                source.BillboardMode != clip.BillboardMode || source.AlphaCutoff != clip.AlphaCutoff ||
                source.TransparentSortPriority != clip.TransparentSortPriority ||
                source.TransparentTechniqueOverride != clip.TransparentTechniqueOverride)
                return false;
            for (int i = 0; i < _liveWorldClipSourceShaders.Length; i++)
                if (!ReferenceEquals(source.Shaders[i], _liveWorldClipSourceShaders[i]) ||
                    source.Shaders[i].SourceRevision != _liveWorldClipShaderRevisions[i])
                    return false;
            for (int i = 0; i < clip.Textures.Count; i++)
                if (!ReferenceEquals(source.Textures[i], clip.Textures[i]))
                    return false;
            return true;
        }

        private bool TryBuildLiveWorldClip(XRMaterial source)
        {
            ReleaseLiveWorldClip();
            if (!UIWorldCropShaderContract.TrySelectFragment(source, out int fragmentIndex))
            {
                ReportLiveWorldClipFailure(LiveWorldClipShaderRequired);
                return false;
            }
            foreach (ShaderVar parameter in source.Parameters)
                if (parameter.Name is UIWorldCropShaderContract.MinMaxUniform or UIWorldCropShaderContract.MatrixUniform)
                {
                    ReportLiveWorldClipFailure(LiveWorldClipUniformConflict);
                    return false;
                }

            int shaderCount = source.Shaders.Count;
            XRShader[] sourceShaders = new XRShader[shaderCount];
            long[] revisions = new long[shaderCount];
            XRShader[] shaders = new XRShader[shaderCount];
            for (int i = 0; i < shaderCount; i++)
            {
                XRShader shader = source.Shaders[i];
                sourceShaders[i] = shader;
                revisions[i] = shader.SourceRevision;
                if (i != fragmentIndex)
                    shaders[i] = shader;
                else if (TryGetLiveWorldClipVariant(shader) is { } variant)
                    shaders[i] = variant;
                else
                {
                    ReportLiveWorldClipFailure(LiveWorldClipShaderRequired);
                    return false;
                }
            }

            XRTexture?[] textures = new XRTexture?[source.Textures.Count];
            for (int i = 0; i < textures.Length; i++)
                textures[i] = source.Textures[i];

            XRMaterial clip = new([], textures, shaders)
            {
                BillboardMode = source.BillboardMode,
                TransparencyMode = source.TransparencyMode,
                AlphaCutoff = source.AlphaCutoff,
                TransparentSortPriority = source.TransparentSortPriority,
                TransparentTechniqueOverride = source.TransparentTechniqueOverride,
                RenderPass = source.RenderPass,
                // The clip material shares the raster state, so a raster edit on the source applies to both.
                RenderOptions = source.RenderOptions,
            };
            // Constructor shader discovery must not add parameters. The uniform callback writes all values.
            clip.Parameters = [];
            clip.SettingUniforms += OnLiveWorldClipSettingUniforms;
            _liveWorldClipMaterial = clip;
            _liveWorldClipSource = source;
            _liveWorldClipSourceShaders = sourceShaders;
            _liveWorldClipShaderRevisions = revisions;
            return true;
        }

        private static XRShader? TryGetLiveWorldClipVariant(XRShader source)
        {
            long revision = source.SourceRevision;
            if (LiveWorldClipVariants.TryGetValue(source, out LiveWorldClipVariant? cached) && cached.Revision == revision)
                return cached.Shader;
            if (!UIWorldCropShaderContract.TryCreateFragmentVariant(source, out XRShader? variant) || variant is null)
                return null;
            LiveWorldClipVariants.AddOrUpdate(source, new LiveWorldClipVariant(revision, variant));
            return variant;
        }

        /// <summary>
        /// Writes the source parameters, the component bounds, and the crop to the clip program on each draw.
        /// </summary>
        /// <remarks>
        /// Only the handler of this component runs. Other subscribers of a shared source material write their own
        /// bounds, so they must not write to the program of this clip material.
        /// </remarks>
        private void OnLiveWorldClipSettingUniforms(XRMaterialBase material, XRRenderProgram program)
        {
            if (!ReferenceEquals(material, _liveWorldClipMaterial) || _liveWorldClipSource is not { } source)
                return;
            foreach (ShaderVar parameter in source.Parameters)
                parameter.ApplyTo(program);
            // A program can serve more than one clip material, so the bounds uniforms are written on each draw.
            _lastBounds = new Vector4(float.NaN);
            OnMaterialSettingUniforms(source, program);
            program.Uniform(UIWorldCropShaderContract.MinMaxUniform, _liveWorldClipMinMax);
            program.Uniform(UIWorldCropShaderContract.MatrixUniform, _liveWorldClipModelToCanvas);
        }

        private void ReleaseLiveWorldClip(bool clearOverride = true)
        {
            XRMaterial? clip = _liveWorldClipMaterial;
            if (clip is null)
                return;
            _liveWorldClipMaterial = null;
            _liveWorldClipSource = null;
            _liveWorldClipSourceShaders = [];
            _liveWorldClipShaderRevisions = [];
            clip.SettingUniforms -= OnLiveWorldClipSettingUniforms;
            if (clearOverride && ReferenceEquals(RenderCommand3D.MaterialOverride, clip))
                RenderCommand3D.MaterialOverride = null;
            _lastBounds = new Vector4(float.NaN);
        }

        private void ReportLiveWorldClipFailure(string reason)
        {
            if (ReferenceEquals(_liveWorldClipFailure, reason))
                return;
            _liveWorldClipFailure = reason;
            UIFailureDiagnostics.Report(reason);
        }
    }
}
