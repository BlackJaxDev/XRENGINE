using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Scene.Mesh
{
    public partial class RenderableMesh
    {
        #region Highlight and bounds-debug state

        private readonly object _highlightStateLock = new();
        private RenderingParameters? _highlightRenderOptionsOverride;
        private XRMaterial? _highlightSourceMaterial;
        private int _highlightStencilBits;
        private bool _skinnedCullDiagLogged;
        private bool _gpuSkinnedBoundsDebugFailureLogged;
        private GpuBoundsDebugLineRenderer? _gpuBoundsDebugRenderer;

        #endregion

        #region Utilized bone debug links

        internal void AppendUtilizedBoneDiamondLinks(List<(TransformBase bone, Vector3? fallbackTip)> links)
        {
            Vector3? fallbackTip = ((IOctreeItem)RenderInfo).WorldCullingVolume?.WorldCenter;

            lock (_lodsLock)
            {
                for (LinkedListNode<RenderableLOD>? node = LODs.First; node is not null; node = node.Next)
                {
                    (TransformBase tfm, Matrix4x4 invBindWorldMtx)[]? utilized = node.Value.Renderer.Mesh?.UtilizedBones;
                    if (utilized is not { Length: > 0 })
                        continue;

                    for (int i = 0; i < utilized.Length; i++)
                    {
                        TransformBase bone = utilized[i].tfm;
                        if (bone is null)
                            continue;

                        AddOrUpdateBoneDiamondLink(links, bone, fallbackTip);
                    }
                }
            }
        }

        private static void AddOrUpdateBoneDiamondLink(List<(TransformBase bone, Vector3? fallbackTip)> links, TransformBase bone, Vector3? fallbackTip)
        {
            for (int i = 0; i < links.Count; i++)
            {
                if (!ReferenceEquals(links[i].bone, bone))
                    continue;

                if (!links[i].fallbackTip.HasValue && fallbackTip.HasValue)
                    links[i] = (bone, fallbackTip);

                return;
            }

            links.Add((bone, fallbackTip));
        }

        #endregion

        #region Highlight stencil overrides

        public void SetHighlightStencilBit(int stencilBit, bool enabled)
        {
            lock (_highlightStateLock)
            {
                int nextBits = enabled
                    ? (_highlightStencilBits | stencilBit)
                    : (_highlightStencilBits & ~stencilBit);

                if (nextBits == _highlightStencilBits)
                    return;

                _highlightStencilBits = nextBits;
                ApplyHighlightRenderOptionsOverride_NoLock(CurrentLODRenderer?.Material);
            }
        }

        private void ApplyHighlightRenderOptionsOverride(XRMaterial? sourceMaterial)
        {
            lock (_highlightStateLock)
                ApplyHighlightRenderOptionsOverride_NoLock(sourceMaterial);
        }

        private void ApplyHighlightRenderOptionsOverride_NoLock(XRMaterial? sourceMaterial)
        {
            if (_highlightStencilBits == 0 || sourceMaterial is null)
            {
                _rc.MaterialOverride = null;
                _rc.RenderOptionsOverride = null;
                _rc.ForceCpuRendering = false;
                return;
            }

            if (!ReferenceEquals(_highlightSourceMaterial, sourceMaterial) || _highlightRenderOptionsOverride is null)
            {
                _highlightSourceMaterial = sourceMaterial;
                _highlightRenderOptionsOverride = CloneRenderingParameters(sourceMaterial.RenderOptions);
            }

            ConfigureHighlightStencil(_highlightRenderOptionsOverride, _highlightStencilBits);
            _rc.MaterialOverride = null;
            _rc.RenderOptionsOverride = _highlightRenderOptionsOverride;
            _rc.ForceCpuRendering = true;
        }

        private static RenderingParameters CloneRenderingParameters(RenderingParameters source)
            => new()
            {
                RequiredEngineUniforms = source.RequiredEngineUniforms,
                WriteRed = source.WriteRed,
                WriteGreen = source.WriteGreen,
                WriteBlue = source.WriteBlue,
                WriteAlpha = source.WriteAlpha,
                AlphaToCoverage = source.AlphaToCoverage,
                Winding = source.Winding,
                CullMode = source.CullMode,
                DepthTest = CloneDepthTest(source.DepthTest),
                StencilTest = CloneStencilTest(source.StencilTest),
                BlendModeAllDrawBuffers = CloneBlendMode(source.BlendModeAllDrawBuffers),
                BlendModesPerDrawBuffer = source.BlendModesPerDrawBuffer?.ToDictionary(static kvp => kvp.Key, static kvp => CloneBlendMode(kvp.Value)!),
                ExcludeFromGpuIndirect = source.ExcludeFromGpuIndirect,
            };

        private static DepthTest CloneDepthTest(DepthTest? source)
            => source is null
                ? new DepthTest()
                : new DepthTest
                {
                    Enabled = source.Enabled,
                    UpdateDepth = source.UpdateDepth,
                    Function = source.Function,
                };

        private static StencilTest CloneStencilTest(StencilTest? source)
            => source is null
                ? new StencilTest()
                : new StencilTest
                {
                    Enabled = source.Enabled,
                    FrontFace = CloneStencilTestFace(source.FrontFace),
                    BackFace = CloneStencilTestFace(source.BackFace),
                };

        private static StencilTestFace CloneStencilTestFace(StencilTestFace? source)
            => source is null
                ? new StencilTestFace()
                : new StencilTestFace
                {
                    BothFailOp = source.BothFailOp,
                    StencilPassDepthFailOp = source.StencilPassDepthFailOp,
                    BothPassOp = source.BothPassOp,
                    Function = source.Function,
                    Reference = source.Reference,
                    ReadMask = source.ReadMask,
                    WriteMask = source.WriteMask,
                };

        private static BlendMode? CloneBlendMode(BlendMode? source)
            => source is null
                ? null
                : new BlendMode
                {
                    Enabled = source.Enabled,
                    RgbEquation = source.RgbEquation,
                    AlphaEquation = source.AlphaEquation,
                    RgbSrcFactor = source.RgbSrcFactor,
                    AlphaSrcFactor = source.AlphaSrcFactor,
                    RgbDstFactor = source.RgbDstFactor,
                    AlphaDstFactor = source.AlphaDstFactor,
                };

        private static void ConfigureHighlightStencil(RenderingParameters renderOptions, int stencilBits)
        {
            var stencil = renderOptions.StencilTest ?? new StencilTest();
            stencil.Enabled = ERenderParamUsage.Enabled;
            stencil.FrontFace = CreateHighlightStencilFace(stencil.FrontFace, stencilBits);
            stencil.BackFace = CreateHighlightStencilFace(stencil.BackFace, stencilBits);
            renderOptions.StencilTest = stencil;
        }

        private static StencilTestFace CreateHighlightStencilFace(StencilTestFace? source, int stencilBits)
        {
            source ??= new StencilTestFace();
            return new StencilTestFace
            {
                Function = EComparison.Always,
                Reference = (source.Reference & ~0x3) | (stencilBits & 0x3),
                ReadMask = source.ReadMask | 3u,
                WriteMask = source.WriteMask | 3u,
                BothFailOp = EStencilOp.Keep,
                StencilPassDepthFailOp = EStencilOp.Keep,
                BothPassOp = EStencilOp.Replace,
            };
        }

        #endregion

        #region Bounds and transparency diagnostics

        private void DoRenderBounds()
        {
            if (RuntimeEngine.Rendering.State.IsShadowPass)
                return;

            var debug = RuntimeEngine.EditorPreferences.Debug;
            bool showTransparencyModeOverlay = debug.VisualizeTransparencyModeOverlay;
            bool showTransparencyClassificationOverlay = debug.VisualizeTransparencyClassificationOverlay;

            bool renderMeshBounds = RenderBounds || debug.RenderMesh3DBounds;
            if (!renderMeshBounds && !showTransparencyModeOverlay && !showTransparencyClassificationOverlay)
                return;

            XRMaterial? material = CurrentLODRenderer?.Material;
            ColorF4 boundsColor = RuntimeEngine.EditorPreferences.Theme.Bounds3DColor;

            if (showTransparencyModeOverlay && material is not null)
                boundsColor = GetTransparencyModeColor(material.GetEffectiveTransparencyMode());
            else if (showTransparencyClassificationOverlay && material is not null)
                boundsColor = GetTransparencyClassificationColor(material.GetEffectiveTransparencyMode());

            var box = (RenderInfo as IOctreeItem)?.WorldCullingVolume;
            if (renderMeshBounds && ShouldUseLiveGpuSkinnedBounds())
            {
                if (!TryRenderGpuSkinnedBounds(boundsColor, camera: null))
                    ReportGpuSkinnedBoundsDebugFailure();
            }
            else if (box is not null)
            {
                RenderDebugBox(box.Value, boundsColor);
            }

            if (box is not null)
            {
                if (material is not null && (showTransparencyModeOverlay || showTransparencyClassificationOverlay))
                {
                    string label = showTransparencyModeOverlay
                        ? material.GetEffectiveTransparencyMode().ToString()
                        : GetTransparencyClassificationLabel(material.GetEffectiveTransparencyMode());
                    RuntimeEngine.Rendering.Debug.RenderText(box.Value.LocalCenter, label, boundsColor);
                }
            }

            if (RootBone is not null)
            {
                Vector3 rootTranslation = RootBone.RenderTranslation;
                RuntimeEngine.Rendering.Debug.RenderPoint(rootTranslation, ColorF4.Red);
                if (RootBone.Name is not null)
                    RuntimeEngine.Rendering.Debug.RenderText(rootTranslation, RootBone.Name, ColorF4.Black);
            }
        }

        private static string GetTransparencyClassificationLabel(ETransparencyMode transparencyMode)
            => transparencyMode switch
            {
                ETransparencyMode.Masked or ETransparencyMode.AlphaToCoverage => "Masked",
                ETransparencyMode.Opaque => "Opaque",
                _ => "Blended",
            };

        private static ColorF4 GetTransparencyClassificationColor(ETransparencyMode transparencyMode)
            => transparencyMode switch
            {
                ETransparencyMode.Opaque => new ColorF4(0.6f, 0.6f, 0.6f, 1.0f),
                ETransparencyMode.Masked or ETransparencyMode.AlphaToCoverage => new ColorF4(0.2f, 0.9f, 0.35f, 1.0f),
                _ => new ColorF4(0.2f, 0.75f, 1.0f, 1.0f),
            };

        private static ColorF4 GetTransparencyModeColor(ETransparencyMode transparencyMode)
            => transparencyMode switch
            {
                ETransparencyMode.Opaque => new ColorF4(0.6f, 0.6f, 0.6f, 1.0f),
                ETransparencyMode.Masked => new ColorF4(0.2f, 0.9f, 0.35f, 1.0f),
                ETransparencyMode.AlphaBlend => new ColorF4(0.2f, 0.75f, 1.0f, 1.0f),
                ETransparencyMode.PremultipliedAlpha => new ColorF4(0.95f, 0.75f, 0.2f, 1.0f),
                ETransparencyMode.Additive => new ColorF4(1.0f, 0.55f, 0.15f, 1.0f),
                ETransparencyMode.WeightedBlendedOit => new ColorF4(0.8f, 0.3f, 1.0f, 1.0f),
                ETransparencyMode.PerPixelLinkedList => new ColorF4(1.0f, 0.25f, 0.55f, 1.0f),
                ETransparencyMode.DepthPeeling => new ColorF4(0.9f, 0.2f, 0.2f, 1.0f),
                ETransparencyMode.Stochastic => new ColorF4(0.35f, 1.0f, 0.9f, 1.0f),
                ETransparencyMode.AlphaToCoverage => new ColorF4(0.65f, 1.0f, 0.35f, 1.0f),
                ETransparencyMode.TriangleSorted => new ColorF4(1.0f, 0.9f, 0.25f, 1.0f),
                _ => ColorF4.White,
            };

        // One-shot diagnostic: dumps the first-collect culling state for each skinned mesh so we can
        // confirm whether "missing until I move the root bone" meshes are being frustum-culled because
        // their world culling box is placed away from the actual skinned geometry (bind-pose bounds
        // transformed by an inferred root-bone basis that isn't the model origin).
        private void LogSkinnedCullingDiagnosticsOnce(bool skinnedBoundsOk)
        {
            if (_skinnedCullDiagLogged)
                return;
            _skinnedCullDiagLogged = true;

            Box? worldBox = ((IOctreeItem)RenderInfo).WorldCullingVolume;
            Vector3 boxCenter = worldBox?.WorldCenter ?? Vector3.Zero;
            Vector3 boxHalf = worldBox?.LocalHalfExtents ?? Vector3.Zero;
            Vector3 basisT = GetSkinnedBasisMatrix().Translation;
            Vector3 compT = Component.Transform.RenderTranslation;
            Vector3 rootT = RootBone is null ? Vector3.Zero : GetCurrentCullingBasisMatrix(RootBone).Translation;

            bool hasSkinned;
            lock (_skinnedDataLock)
                hasSkinned = _hasSkinnedBounds;

            RuntimeEngine.LogWarning(
                $"[SkinCull] Mesh='{CurrentLODRenderer?.Mesh?.Name ?? "<null>"}' rootBone='{RootBone?.Name ?? "<null>"}' " +
                $"boundsOk={skinnedBoundsOk} hasSkinnedBounds={hasSkinned} gpuResident={ShouldUseGpuResidentSkinnedBoundsPath()} " +
                $"boxValid={worldBox.HasValue} center=({boxCenter.X:F2},{boxCenter.Y:F2},{boxCenter.Z:F2}) " +
                $"half=({boxHalf.X:F2},{boxHalf.Y:F2},{boxHalf.Z:F2}) basisT=({basisT.X:F2},{basisT.Y:F2},{basisT.Z:F2}) " +
                $"rootT=({rootT.X:F2},{rootT.Y:F2},{rootT.Z:F2}) compT=({compT.X:F2},{compT.Y:F2},{compT.Z:F2})");
        }

        private void QueueCollectedMeshBoundsDebug(RenderCommandCollection passes, IRuntimeRenderCamera? camera)
        {
            if (!ShouldQueueCollectedMeshBoundsDebug(passes, camera))
                return;

            if (ShouldUseLiveGpuSkinnedBounds())
                return;

            Box? box = ((IOctreeItem)RenderInfo).WorldCullingVolume;
            if (box is null)
                return;

            ColorF4 boundsColor = RuntimeEngine.EditorPreferences.Theme.MeshBoundsContainedColor;
            RenderDebugBox(box.Value, boundsColor);
        }

        private bool ShouldUseLiveGpuSkinnedBounds()
            => IsSkinned && RuntimeEngine.Rendering.Settings.CalculateSkinnedBoundsInComputeShader;

        private bool RenderCullingVolumeDebugOverride(RenderInfo info)
        {
            if (!ShouldUseLiveGpuSkinnedBounds())
                return false;

            if (RuntimeEngine.Rendering.State.IsShadowPass ||
                RuntimeEngine.Rendering.State.IsLightProbePass ||
                RuntimeEngine.Rendering.State.IsSceneCapturePass)
            {
                return true;
            }

            ColorF4 boundsColor = RuntimeEngine.EditorPreferences.Theme.Bounds3DColor;
            if (!TryRenderGpuSkinnedBounds(boundsColor, camera: null))
                ReportGpuSkinnedBoundsDebugFailure();
            return true;
        }

        private bool TryRenderGpuSkinnedBounds(ColorF4 boundsColor, XRCamera? camera)
        {
            if (!RuntimeEngine.IsRenderThread ||
                !ShouldUseLiveGpuSkinnedBounds())
            {
                return false;
            }

            if (!TryGetLiveGpuSkinnedBoundsBuffer(
                    out XRDataBuffer? boundsBuffer,
                    out Matrix4x4 boundsToWorld,
                    out uint boundsVec4Offset) ||
                boundsBuffer is null)
            {
                return false;
            }

            _gpuBoundsDebugRenderer ??= new GpuBoundsDebugLineRenderer();
            bool rendered = _gpuBoundsDebugRenderer.Render(
                boundsBuffer,
                boundsToWorld,
                camera ?? ResolveCurrentBoundsDebugCamera(),
                0.0015f,
                new Vector4(boundsColor.R, boundsColor.G, boundsColor.B, boundsColor.A),
                boundsVec4Offset);
            if (rendered)
                _gpuSkinnedBoundsDebugFailureLogged = false;
            return rendered;
        }

        private void ReportGpuSkinnedBoundsDebugFailure()
        {
            if (!_gpuSkinnedBoundsDebugFailureLogged)
            {
                _gpuSkinnedBoundsDebugFailureLogged = true;
                RuntimeEngine.LogWarning(
                    $"[GpuSkinnedBoundsDebug] Mesh='{CurrentLODRenderer?.Mesh?.Name ?? "<null>"}' " +
                    "failed to render live GPU skinned bounds. The bounds buffer is missing, invalid, " +
                    "or the GPU debug-line renderer could not draw it.");
            }

            Box? box = ((IOctreeItem)RenderInfo).WorldCullingVolume;
            if (box is null)
                return;

            Vector3 center = box.Value.WorldCenter;
            RuntimeEngine.Rendering.Debug.RenderPoint(center, ColorF4.Red);
            RuntimeEngine.Rendering.Debug.RenderText(center, "GPU skinned bounds unavailable", ColorF4.Red);
        }

        private bool TryGetLiveGpuSkinnedBoundsBuffer(
            out XRDataBuffer? boundsBuffer,
            out Matrix4x4 boundsToWorld,
            out uint boundsVec4Offset)
        {
            boundsBuffer = null;
            boundsToWorld = Matrix4x4.Identity;
            boundsVec4Offset = 0u;
            if (!ShouldUseLiveGpuSkinnedBounds())
                return false;

            return SkinnedMeshBoundsCalculator.Instance.TryPrepareGpuDebugBounds(
                this,
                out boundsBuffer,
                out boundsToWorld,
                out boundsVec4Offset);
        }

        public bool TryGetLiveGpuSkinnedWorldBounds(out AABB bounds)
        {
            bounds = default;
            if (!ShouldUseLiveGpuSkinnedBounds())
                return false;

            return SkinnedMeshBoundsCalculator.Instance.TryReadGpuDebugBounds(this, out bounds);
        }

        private static XRCamera? ResolveCurrentBoundsDebugCamera()
        {
            var pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
            return RuntimeEngine.Rendering.State.RenderingCamera
                ?? RuntimeEngine.Rendering.State.RenderingPipelineState?.SceneCamera
                ?? pipeline?.LastSceneCamera
                ?? pipeline?.LastRenderingCamera;
        }

        private void DisposeGpuSkinnedBoundsDebugRenderer()
        {
            _gpuBoundsDebugRenderer?.Dispose();
            _gpuBoundsDebugRenderer = null;
        }

        private static void RenderDebugBox(in Box box, ColorF4 color)
        {
            Matrix4x4 orientation = box.Transform;
            orientation.M41 = 0.0f;
            orientation.M42 = 0.0f;
            orientation.M43 = 0.0f;
            RuntimeEngine.Rendering.Debug.RenderBox(box.LocalHalfExtents, box.WorldCenter, orientation, false, color);
        }

        private bool ShouldQueueCollectedMeshBoundsDebug(RenderCommandCollection passes, IRuntimeRenderCamera? camera)
        {
            if (!RenderBounds ||
                passes.IsShadowPass ||
                RuntimeEngine.Rendering.State.IsShadowPass ||
                RuntimeEngine.Rendering.State.IsLightProbePass ||
                RuntimeEngine.Rendering.State.IsSceneCapturePass)
            {
                return false;
            }

            if (camera is not XRCamera xrCamera || !HasNonShadowViewport(xrCamera))
                return false;

            var debug = RuntimeEngine.EditorPreferences.Debug;
            return (RenderBounds || debug.RenderMesh3DBounds) &&
                   !debug.VisualizeTransparencyModeOverlay &&
                   !debug.VisualizeTransparencyClassificationOverlay;
        }

        private static bool HasNonShadowViewport(XRCamera camera)
        {
            for (int i = 0; i < camera.Viewports.Count; i++)
            {
                if (camera.Viewports[i].RenderPipeline?.IsShadowPass != true)
                    return true;
            }

            return false;
        }

        #endregion
    }
}
