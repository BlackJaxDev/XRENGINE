using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Transforms;
using SimpleScene.Util.ssBVH;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.Timers;

namespace XREngine.Components.Scene.Mesh
{
    public class RenderableMesh : XRBase, IDisposable
    {
        private static readonly ConcurrentQueue<RenderableMesh> _pendingRenderMatrixUpdates = new();
        private readonly record struct SkinnedBoundsCpuSnapshot(
            Vertex[] Vertices,
            Dictionary<TransformBase, Matrix4x4> SkinMatrices,
            IReadOnlyDictionary<TransformBase, TransformBase>? BoneReferenceRemap,
            Matrix4x4 FallbackMatrix,
            Matrix4x4 Basis);

        private readonly record struct SkinnedBoundsRefreshResult(
            int Revision,
            SkinnedMeshBoundsCalculator.Result Result,
            bool Succeeded,
            long QueueWaitTicks,
            long CpuJobTicks);

        public RenderInfo3D RenderInfo { get; }

        private readonly RenderCommandMesh3D _rc;
        private readonly object _highlightStateLock = new();
        private RenderingParameters? _highlightRenderOptionsOverride;
        private XRMaterial? _highlightSourceMaterial;
        private int _highlightStencilBits;

        private readonly Dictionary<TransformBase, int> _trackedSkinnedBones = new();
        private readonly Dictionary<TransformBase, Matrix4x4> _relativeBoneMatrices = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        private readonly HashSet<XRMesh> _ownedRuntimeMeshes = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        private readonly object _relativeCacheLock = new();
        private readonly object _skinnedDataLock = new();
        private bool _skinnedBoundsDirty = true;
        private bool _hasSkinnedBounds;
        private bool _skinnedBoundsAreWorldSpace;
        private AABB _skinnedLocalBounds;
        private Vector3[]? _skinnedVertexPositions;
        private int _skinnedVertexCount;
        // Scratch buffer for Path A GPU-AABB direct-write dispatch; sized lazily.
        private readonly List<uint> _pathAScratchIndices = new(8);
        private Task<SkinnedBoundsRefreshResult>? _skinnedBoundsRefreshTask;
        private int _skinnedBoundsRevision;
        private BVH<Triangle>? _skinnedBvh;
        private Task<SkinnedMeshBvhScheduler.Result>? _skinnedBvhTask;
        private int _skinnedBvhVersion = 0;
        private bool _skinnedBvhScheduledOnce;
        private AABB _bindPoseBounds;
        private Matrix4x4 _skinnedRootRenderMatrix = Matrix4x4.Identity;
        private Matrix4x4 _skinnedRootRenderMatrixInverse = Matrix4x4.Identity;
        private bool _lastRenderSkinningEnabled;

        // The renderer seeds its bone palette and draw matrix during scene load, before every
        // transform has necessarily published a non-stale RenderMatrix. Re-seed once from the
        // current transform state on the first collect, mirroring the skinning toggle path.
        private bool _initialRenderStateSeeded;

        // Vertex-skinning pose-settle latch. The compute path settles its CPU-built skin palette in
        // SkinningPrepassDispatcher; the vertex draw path has no equivalent, so it drives the same
        // shared pose-settle re-seed (XRMeshRenderer.ReseedSkinPaletteUntilPoseStable) each frame
        // until the runtime-imported skeleton's pose stabilizes. Without this the palette can latch
        // an intermediate startup pose and render exploded until a bone is manually moved.
        private bool _vertexSkinSeedSettled;

        /// <summary>
        /// Minimum interval (in seconds) between expensive skinned bounds recomputations.
        /// The root bone matrix is updated every frame regardless, so culling remains correct;
        /// only the local AABB shape is recalculated at this cadence.
        /// </summary>
        private const float SkinnedBoundsRefreshInterval = 5.0f;
        private static readonly long SkinnedBoundsRefreshIntervalTicks = RuntimeTiming.SecondsToStopwatchTicks(SkinnedBoundsRefreshInterval);
        private long _lastSkinnedBoundsRefreshTicks = long.MinValue;

        internal static bool ShouldReuseSkinnedBounds(long nowTicks, long lastRefreshTicks)
            => lastRefreshTicks != long.MinValue && Math.Max(0L, nowTicks - lastRefreshTicks) < SkinnedBoundsRefreshIntervalTicks;

        internal static bool AllowsInitialRuntimeSkinnedBoundsBuild(
            ESkinnedBoundsRecomputePolicy policy,
            bool allowInitialBuildWhenNever)
            => policy != ESkinnedBoundsRecomputePolicy.Never || allowInitialBuildWhenNever;

        internal static bool ShouldScheduleSkinnedBoundsRefresh(
            ESkinnedBoundsRecomputePolicy policy,
            bool allowInitialBuildWhenNever,
            bool hasCachedBounds,
            bool skinnedBoundsDirty,
            bool refreshInFlight,
            long nowTicks,
            long lastRefreshTicks)
        {
            if (!skinnedBoundsDirty || refreshInFlight)
                return false;

            return policy switch
            {
                ESkinnedBoundsRecomputePolicy.Never => allowInitialBuildWhenNever && !hasCachedBounds,
                ESkinnedBoundsRecomputePolicy.Always => true,
                ESkinnedBoundsRecomputePolicy.Selective => !hasCachedBounds || !ShouldReuseSkinnedBounds(nowTicks, lastRefreshTicks),
                _ => false,
            };
        }

        public Matrix4x4 SkinnedBvhLocalToWorldMatrix => _skinnedRootRenderMatrix;
        public Matrix4x4 SkinnedBvhWorldToLocalMatrix => _skinnedRootRenderMatrixInverse;

        private void SetSkinnedRootRenderMatrix(Matrix4x4 matrix)
        {
            _skinnedRootRenderMatrix = matrix;
            _skinnedRootRenderMatrixInverse = Matrix4x4.Invert(matrix, out var inv) ? inv : Matrix4x4.Identity;
        }

        private readonly object _lodsLock = new();

        public XRMeshRenderer? CurrentLODRenderer
        {
            get
            {
                lock (_lodsLock)
                    return _currentLOD?.Value?.Renderer;
            }
        }
        public XRMesh? CurrentLODMesh
        {
            get
            {
                lock (_lodsLock)
                    return _currentLOD?.Value?.Renderer?.Mesh;
            }
        }

        private LinkedListNode<RenderableLOD>? _currentLOD = null;
        public LinkedListNode<RenderableLOD>? CurrentLOD
        {
            get => _currentLOD;
            private set => SetField(ref _currentLOD, value);
        }
        public IRuntimeRenderWorld? World => Component.SceneNode.World as IRuntimeRenderWorld;
        public LinkedList<RenderableLOD> LODs { get; private set; } = new();

        private bool _renderBounds = RuntimeEngine.EditorPreferences.Debug.RenderMesh3DBounds;
        public bool RenderBounds
        {
            get => _renderBounds;
            set => SetField(ref _renderBounds, value);
        }

        private TransformBase? _rootBone;
        public TransformBase? RootBone
        {
            get => _rootBone;
            set => SetField(ref _rootBone, value);
        }

        private readonly TransformBase? _skinnedBoundsRootTransform;

        private RenderableComponent _component;
        /// <summary>
        /// The transform that owns this mesh.
        /// </summary>
        public RenderableComponent Component
        {
            get => _component;
            private set => SetField(ref _component, value);
        }

        private readonly RenderCommandMethod3D _renderBoundsCommand;

        private readonly object _pendingRenderMatrixLock = new();
        private Matrix4x4 _pendingComponentRenderMatrix = Matrix4x4.Identity;
        private int _pendingComponentRenderMatrixVersion;
        private Matrix4x4 _pendingRootBoneRenderMatrix = Matrix4x4.Identity;
        private int _pendingRootBoneRenderMatrixVersion;
        private int _pendingRenderMatrixQueued;

        public bool IsSkinned
            => (CurrentLODRenderer?.Mesh?.HasSkinning ?? false) && RuntimeEngine.Rendering.Settings.AllowSkinning;

        void ComponentPropertyChanged(object? s, IXRPropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RenderableComponent.Transform) && !Component.SceneNode.IsTransformNull)
            {
                Component.Transform.WorldMatrixChanged += Component_WorldMatrixPreviewChanged;
                Component.Transform.RenderMatrixChanged += Component_WorldMatrixChanged;
            }
        }
        void ComponentPropertyChanging(object? s, IXRPropertyChangingEventArgs e)
        {
            if (e.PropertyName == nameof(RenderableComponent.Transform) && !Component.SceneNode.IsTransformNull)
            {
                Component.Transform.WorldMatrixChanged -= Component_WorldMatrixPreviewChanged;
                Component.Transform.RenderMatrixChanged -= Component_WorldMatrixChanged;
            }
        }

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
        public RenderableMesh(SubMesh mesh, RenderableComponent component)
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
        {
            Component = component;
            TransformBase? referenceSearchRoot = GetTransformReferenceSearchRoot();
            TransformBase? serializedRootBone = ResolveTransformReference(mesh.RootBone, referenceSearchRoot);
            _skinnedBoundsRootTransform = ResolveTransformReference(mesh.RootTransform, referenceSearchRoot);

            lock (_lodsLock)
            {
                foreach (var lod in mesh.LODs)
                {
                    var renderer = lod.NewRenderer();
                    renderer.Mesh = CreateRuntimeMesh(lod.Mesh, referenceSearchRoot);
                    renderer.SourceSubMeshAsset = mesh;
                    renderer.SettingUniforms += SettingUniforms;
                    void UpdateReferences(object? s, IXRPropertyChangedEventArgs e)
                    {
                        if (e.PropertyName == nameof(SubMeshLOD.Mesh))
                        {
                            XRMesh? previousMesh = renderer.Mesh;
                            TrackBones(previousMesh, false);
                            ReleaseOwnedRuntimeMesh(previousMesh);
                            renderer.Mesh = CreateRuntimeMesh(lod.Mesh, GetTransformReferenceSearchRoot());
                            TrackBones(renderer.Mesh, true);
                            MarkSkinnedDataDirty();
                        }
                        else if (e.PropertyName == nameof(SubMeshLOD.Material))
                            renderer.Material = lod.Material;
                    }
                    lod.PropertyChanged += UpdateReferences;
                    LODs.AddLast(new RenderableLOD(renderer, lod.MaxVisibleDistance, lod.MinProjectedScreenRadiusPixels));
                    TrackBones(renderer.Mesh, true);
                }

            }

            RootBone = ResolveSkinnedRootBoneTransform(serializedRootBone, DetermineRootBoneFromRenderers());

            _renderBoundsCommand = new RenderCommandMethod3D((int)EDefaultRenderPass.OpaqueForward, DoRenderBounds);
            RenderInfo = RenderInfo3D.New(component, _rc = new RenderCommandMesh3D(0));
            if (RenderBounds)
                RenderInfo.RenderCommands.Add(_renderBoundsCommand);
            RenderInfo.LocalCullingVolume = mesh.CullingBounds ?? mesh.Bounds;
            _bindPoseBounds = RenderInfo.LocalCullingVolume ?? mesh.Bounds;
            RenderInfo.PreCollectCommandsCallback = BeforeAdd;
            RenderInfo.PropertyChanged += RenderInfoPropertyChanged;
            PublishRenderCommandCullingVolume();

            lock (_lodsLock)
            {
                if (LODs.Count > 0)
                    CurrentLOD = LODs.First;
            }
            
            // Set initial mesh renderer for GPU scene (will be updated in BeforeAdd if needed)
            _rc.Mesh = CurrentLODRenderer;
            var mat = CurrentLODRenderer?.Material;
            if (mat is not null)
                _rc.RenderPass = mat.RenderPass;

            // Seed startup transform state now that the render command and render info exist.
            // This avoids the first registration frame depending on a later queued matrix update.
            if (IsSkinned)
            {
                TransformBase basisTransform = GetSkinnedBoundsBasisTransform();
                SetSkinnedRootRenderMatrix(basisTransform.RenderMatrix);
                RenderInfo.CullingOffsetMatrix = basisTransform.WorldMatrix;
                QueuePendingRenderMatrixUpdate();
            }
            else
            {
                _rc.WorldMatrix = Component.Transform.RenderMatrix;
                RenderInfo.CullingOffsetMatrix = Component.Transform.WorldMatrix;
            }

            _lastRenderSkinningEnabled = IsSkinned;
            RuntimeEngine.Rendering.SettingsChanged += Rendering_SettingsChanged;
        }

        private void RenderInfoPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(RenderInfo3D.LocalCullingVolume) or nameof(RenderInfo3D.CullingOffsetMatrix))
                PublishRenderCommandCullingVolume();
        }

        private void PublishRenderCommandCullingVolume()
        {
            Box? worldBox = ((IOctreeItem)RenderInfo).WorldCullingVolume;
            _rc.WorldCullingVolumeOverride = worldBox?.GetAABB(transformed: true);
        }

        internal RenderableLOD[] GetLodSnapshot()
        {
            lock (_lodsLock)
                return [.. LODs];
        }

        internal XRMeshRenderer? GetCurrentOrFirstLodRenderer()
        {
            lock (_lodsLock)
                return _currentLOD?.Value?.Renderer ?? LODs.First?.Value?.Renderer;
        }

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

        private void DoRenderBounds()
        {
            if (RuntimeEngine.Rendering.State.IsShadowPass)
                return;

            var debug = RuntimeEngine.EditorPreferences.Debug;
            bool showTransparencyModeOverlay = debug.VisualizeTransparencyModeOverlay;
            bool showTransparencyClassificationOverlay = debug.VisualizeTransparencyClassificationOverlay;

            if (debug.RenderMesh3DBounds && !showTransparencyModeOverlay && !showTransparencyClassificationOverlay)
                return;

            XRMaterial? material = CurrentLODRenderer?.Material;
            ColorF4 boundsColor = RuntimeEngine.EditorPreferences.Theme.Bounds3DColor;

            if (showTransparencyModeOverlay && material is not null)
                boundsColor = GetTransparencyModeColor(material.GetEffectiveTransparencyMode());
            else if (showTransparencyClassificationOverlay && material is not null)
                boundsColor = GetTransparencyClassificationColor(material.GetEffectiveTransparencyMode());

            var box = (RenderInfo as IOctreeItem)?.WorldCullingVolume;
            if (box is not null)
            {
                RenderDebugBox(box.Value, boundsColor);

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

        private void SettingUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
        {
            //vertexProgram.Uniform(EEngineUniform.RootInvModelMatrix.ToString(), /*RootTransform?.InverseWorldMatrix ?? */Matrix4x4.Identity);
        }

        private bool BeforeAdd(RenderInfo info, RenderCommandCollection passes, IRuntimeRenderCamera? camera)
        {
            var rend = CurrentLODRenderer;
            bool skinned = (rend?.Mesh?.HasSkinning ?? false) && RuntimeEngine.Rendering.Settings.AllowSkinning;
            TransformBase tfm = skinned ? RootBone ?? Component.Transform : Component.Transform;
            float distance = camera?.DistanceFromRenderNearPlane(tfm.RenderTranslation) ?? 0.0f;

            if (!passes.IsShadowPass)
                UpdateLOD(distance);

            rend = CurrentLODRenderer;
            skinned = (rend?.Mesh?.HasSkinning ?? false) && RuntimeEngine.Rendering.Settings.AllowSkinning;

            // One-shot: construction-time palette/draw-matrix seeds can capture stale RenderMatrix
            // values. The toggle path fixes that by re-reading current transform state; do the same
            // here before the first real draw.
            if (!_initialRenderStateSeeded)
            {
                _initialRenderStateSeeded = true;
                if (rend?.Mesh?.HasSkinning == true && rend.EnsureSkinningBuffers(logWarnings: false))
                    rend.RefreshBoneMatricesFromRenderState();
                QueueCurrentRenderMatrixUpdate();
            }

            // Vertex draw path pose-settle: keep re-seeding the CPU-built skin palette from current
            // bone render state until the skeleton pose stabilizes. The compute path does this in
            // SkinningPrepassDispatcher; the vertex shader reads the same palette but has no settle
            // loop of its own, so a runtime-imported avatar that publishes intermediate startup poses
            // can otherwise latch a wrong pose and render exploded until a bone is manually moved.
            // Only the vertex path runs this -- when compute skinning is enabled the dispatcher owns
            // the shared re-seed and double-driving it here would corrupt its pose tracking.
            if (!_vertexSkinSeedSettled
                && rend?.Mesh?.HasSkinning == true
                && RuntimeEngine.Rendering.Settings.AllowSkinning
                && !RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader
                && rend.EnsureSkinningBuffers(logWarnings: false))
            {
                _vertexSkinSeedSettled = rend.ReseedSkinPaletteUntilPoseStable();
            }

            if (skinned)
            {
                bool skinnedBoundsOk = EnsureSkinnedBounds();
                if (!skinnedBoundsOk)
                {
                    RenderInfo.LocalCullingVolume = _bindPoseBounds;
                    RenderInfo.CullingOffsetMatrix = GetSkinnedBasisMatrix();
                }
                LogSkinnedCullingDiagnosticsOnce(skinnedBoundsOk);
            }
            else
            {
                RenderInfo.LocalCullingVolume = _bindPoseBounds;
                RenderInfo.CullingOffsetMatrix = GetCurrentCullingBasisMatrix(Component.Transform);
            }

            _rc.Mesh = rend;
            _rc.RenderDistance = distance;

            var mat = rend?.Material;
            if (mat is not null)
            {
                if (ShouldRecordImportedTextureStreamingUsage(passes.IsShadowPass, RuntimeEngine.Rendering.State.IsMainPass))
                    XRTexture2D.RecordImportedTextureStreamingUsage(mat, BuildImportedTextureStreamingUsage(rend?.Mesh, camera as XRCamera, distance));
                _rc.RenderPass = mat.RenderPass;
            }

            ApplyHighlightRenderOptionsOverride(mat);
            ModelRenderDiagnostics.LogCommandCollect(this, _rc, passes, camera, distance);
            QueueCollectedMeshBoundsDebug(passes, camera);

            return true;
        }

        private bool _skinnedCullDiagLogged;

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

            Box? box = ((IOctreeItem)RenderInfo).WorldCullingVolume;
            if (box is null)
                return;

            ColorF4 boundsColor = RuntimeEngine.EditorPreferences.Theme.MeshBoundsContainedColor;
            RenderDebugBox(box.Value, boundsColor);
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
            return debug.RenderMesh3DBounds &&
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

        internal static bool ShouldRecordImportedTextureStreamingUsage(bool isShadowPass, bool isMainPass)
            => !isShadowPass && isMainPass;

        private ImportedTextureStreamingUsage BuildImportedTextureStreamingUsage(XRMesh? mesh, XRCamera? camera, float distanceFromCamera)
        {
            MeshTextureStreamingMetrics metrics = MeshTextureStreamingMetricsCache.Get(mesh);
            float projectedPixelSpan = 0.0f;
            float screenCoverage = 0.0f;
            if (camera is not null && TryGetCurrentStreamingBounds(out AABB worldBounds))
                CalculateTextureStreamingScreenMetrics(camera, worldBounds, distanceFromCamera, out projectedPixelSpan, out screenCoverage);

            return new ImportedTextureStreamingUsage(
                distanceFromCamera,
                projectedPixelSpan,
                screenCoverage,
                metrics.UvDensityHint,
                metrics.PageSelection);
        }

        private bool TryGetCurrentStreamingBounds(out AABB worldBounds)
        {
            worldBounds = default;
            RenderInfo3D renderInfo = RenderInfo;
            AABB localBounds = renderInfo.LocalCullingVolume ?? _bindPoseBounds;
            if (!localBounds.IsValid)
                return TryGetWorldBounds(out worldBounds);

            worldBounds = TransformBounds(localBounds, renderInfo.CullingOffsetMatrix);
            return worldBounds.IsValid;
        }

        private static void CalculateTextureStreamingScreenMetrics(XRCamera camera, AABB worldBounds, float distanceFromCamera, out float projectedPixelSpan, out float screenCoverage)
        {
            projectedPixelSpan = 0.0f;
            screenCoverage = 0.0f;

            float distance = MathF.Max(distanceFromCamera, camera.Parameters.NearZ + 0.001f);
            Vector2 frustumSize = camera.Parameters.GetFrustumSizeAtDistance(distance);
            if (!(frustumSize.X > 0.0f) || !(frustumSize.Y > 0.0f))
                return;

            float diameter = MathF.Max(0.001f, worldBounds.HalfExtents.Length() * 2.0f);
            for (int viewportIndex = 0; viewportIndex < camera.Viewports.Count; viewportIndex++)
            {
                XRViewport viewport = camera.Viewports[viewportIndex];
                int viewportWidth = Math.Max(1, viewport.InternalWidth > 0 ? viewport.InternalWidth : viewport.Width);
                int viewportHeight = Math.Max(1, viewport.InternalHeight > 0 ? viewport.InternalHeight : viewport.Height);

                float projectedWidth = Math.Clamp(diameter / frustumSize.X * viewportWidth, 0.0f, viewportWidth);
                float projectedHeight = Math.Clamp(diameter / frustumSize.Y * viewportHeight, 0.0f, viewportHeight);
                projectedPixelSpan = MathF.Max(projectedPixelSpan, MathF.Max(projectedWidth, projectedHeight));
                screenCoverage = MathF.Max(screenCoverage, Math.Clamp((projectedWidth * projectedHeight) / (viewportWidth * viewportHeight), 0.0f, 1.0f));
            }
        }

        private sealed class MeshTextureStreamingMetrics(float uvDensityHint, SparseTextureStreamingPageSelection pageSelection)
        {
            public static MeshTextureStreamingMetrics Default { get; } = new(1.0f, SparseTextureStreamingPageSelection.Full);

            public float UvDensityHint { get; } = uvDensityHint;
            public SparseTextureStreamingPageSelection PageSelection { get; } = pageSelection.Normalize();
        }

        private static class MeshTextureStreamingMetricsCache
        {
            private static readonly ConditionalWeakTable<XRMesh, MeshTextureStreamingMetrics> Cache = new();

            public static MeshTextureStreamingMetrics Get(XRMesh? mesh)
                => mesh is null ? MeshTextureStreamingMetrics.Default : Cache.GetValue(mesh, static value => Create(value));

            private static MeshTextureStreamingMetrics Create(XRMesh mesh)
            {
                Vertex[] vertices = mesh.Vertices;
                if (vertices.Length == 0)
                    return MeshTextureStreamingMetrics.Default;

                bool hasUvBounds = false;
                bool uvOutOfRange = false;
                Vector2 minUv = new(float.PositiveInfinity, float.PositiveInfinity);
                Vector2 maxUv = new(float.NegativeInfinity, float.NegativeInfinity);
                for (int vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
                {
                    if (!TryGetPrimaryTexCoord(vertices[vertexIndex], out Vector2 uv))
                        continue;

                    hasUvBounds = true;
                    if (uv.X < -0.01f || uv.X > 1.01f || uv.Y < -0.01f || uv.Y > 1.01f)
                    {
                        uvOutOfRange = true;
                        break;
                    }

                    minUv = Vector2.Min(minUv, uv);
                    maxUv = Vector2.Max(maxUv, uv);
                }

                SparseTextureStreamingPageSelection pageSelection = hasUvBounds && !uvOutOfRange
                    ? SparseTextureStreamingPageSelection.Partial(minUv.X, minUv.Y, maxUv.X, maxUv.Y).Normalize()
                    : SparseTextureStreamingPageSelection.Full;

                float surfaceArea = 0.0f;
                float uvArea = 0.0f;
                if (mesh.Triangles is { Count: > 0 } triangles)
                {
                    for (int triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
                    {
                        IndexTriangle triangle = triangles[triangleIndex];
                        if (triangle.Point0 < 0 || triangle.Point1 < 0 || triangle.Point2 < 0
                            || triangle.Point0 >= vertices.Length || triangle.Point1 >= vertices.Length || triangle.Point2 >= vertices.Length)
                        {
                            continue;
                        }

                        Vertex v0 = vertices[triangle.Point0];
                        Vertex v1 = vertices[triangle.Point1];
                        Vertex v2 = vertices[triangle.Point2];
                        if (!TryGetPrimaryTexCoord(v0, out Vector2 uv0)
                            || !TryGetPrimaryTexCoord(v1, out Vector2 uv1)
                            || !TryGetPrimaryTexCoord(v2, out Vector2 uv2))
                        {
                            continue;
                        }

                        float triangleSurfaceArea = Vector3.Cross(v1.Position - v0.Position, v2.Position - v0.Position).Length() * 0.5f;
                        float triangleUvArea = MathF.Abs((uv1.X - uv0.X) * (uv2.Y - uv0.Y) - (uv1.Y - uv0.Y) * (uv2.X - uv0.X)) * 0.5f;
                        if (!float.IsFinite(triangleSurfaceArea) || !float.IsFinite(triangleUvArea) || triangleSurfaceArea <= 1.0e-6f || triangleUvArea <= 1.0e-6f)
                            continue;

                        surfaceArea += triangleSurfaceArea;
                        uvArea += triangleUvArea;
                    }
                }

                float uvDensityHint = 1.0f;
                if (surfaceArea > 1.0e-6f && uvArea > 1.0e-6f)
                {
                    Vector3 halfExtents = mesh.Bounds.HalfExtents;
                    float maxExtent = MathF.Max(1.0e-3f, MathF.Max(halfExtents.X, MathF.Max(halfExtents.Y, halfExtents.Z)) * 2.0f);
                    uvDensityHint = Math.Clamp(MathF.Sqrt(uvArea / surfaceArea) * maxExtent, 0.5f, 2.0f);
                }

                return new MeshTextureStreamingMetrics(uvDensityHint, pageSelection);
            }

            private static bool TryGetPrimaryTexCoord(Vertex vertex, out Vector2 uv)
            {
                List<Vector2>? textureCoordinateSets = vertex.TextureCoordinateSets;
                if (textureCoordinateSets is null || textureCoordinateSets.Count == 0)
                {
                    uv = default;
                    return false;
                }

                uv = textureCoordinateSets[0];
                return float.IsFinite(uv.X) && float.IsFinite(uv.Y);
            }
        }

        public record RenderableLOD(
            XRMeshRenderer Renderer,
            float MaxVisibleDistance,
            float MinProjectedScreenRadiusPixels);

        private void TrackBones(XRMesh? mesh, bool subscribe)
        {
            if (mesh?.HasSkinning != true)
                return;

            foreach (var (bone, _) in mesh.UtilizedBones)
            {
                if (bone is null)
                    continue;

                if (subscribe)
                {
                    if (_trackedSkinnedBones.TryGetValue(bone, out int count))
                        _trackedSkinnedBones[bone] = count + 1;
                    else
                    {
                        _trackedSkinnedBones.Add(bone, 1);
                        bone.RenderMatrixChanged += Bone_RenderMatrixChanged;
                        UpdateRelativeBoneMatrix(bone, initialize: true);
                    }
                }
                else if (_trackedSkinnedBones.TryGetValue(bone, out int count))
                {
                    if (count <= 1)
                    {
                        _trackedSkinnedBones.Remove(bone);
                        bone.RenderMatrixChanged -= Bone_RenderMatrixChanged;
                        lock (_relativeCacheLock)
                            _relativeBoneMatrices.Remove(bone);
                    }
                    else
                        _trackedSkinnedBones[bone] = count - 1;
                }
            }
        }

        internal static TransformBase? ResolveSkinnedRootBoneTransform(
            TransformBase? serializedRootBone,
            TransformBase? inferredRootBone)
            => serializedRootBone ?? inferredRootBone;

        internal static TransformBase? ResolveSkinnedBoundsBasisTransform(
            TransformBase? rootBone,
            TransformBase? rootTransform)
            => rootBone ?? rootTransform;

        private TransformBase GetSkinnedBoundsBasisTransform()
            => ResolveSkinnedBoundsBasisTransform(RootBone, _skinnedBoundsRootTransform) ?? Component.Transform;

        private Matrix4x4 GetSkinnedBasisMatrix()
            => GetCurrentCullingBasisMatrix(GetSkinnedBoundsBasisTransform());

        internal Matrix4x4 GetSkinnedBoundsBasisMatrix()
            => GetSkinnedBasisMatrix();

        private static Matrix4x4 GetCurrentTransformMatrix(TransformBase transform)
        {
            if (!RuntimeEngine.IsRenderThread)
                return transform.WorldMatrix;

            Matrix4x4 renderMatrix = transform.RenderMatrix;
            if (!renderMatrix.Equals(Matrix4x4.Identity))
                return renderMatrix;

            Matrix4x4 worldMatrix = transform.WorldMatrix;
            return worldMatrix.Equals(Matrix4x4.Identity) ? renderMatrix : worldMatrix;
        }

        private static Matrix4x4 GetCurrentCullingBasisMatrix(TransformBase transform)
            => GetCurrentTransformMatrix(transform);

        private static Vector3 TransformPosition(in Vector3 position, in Matrix4x4 matrix)
            => AffineMatrix4x3.TryFromMatrix4x4(matrix, out AffineMatrix4x3 affine)
                ? affine.TransformPosition(position)
                : Vector3.Transform(position, matrix);

        private static AABB TransformBounds(in AABB bounds, in Matrix4x4 matrix)
        {
            if (!AffineMatrix4x3.TryFromMatrix4x4(matrix, out AffineMatrix4x3 affine))
            {
                Matrix4x4 matrixCopy = matrix;
                return bounds.Transformed(p => Vector3.Transform(p, matrixCopy));
            }

            bounds.GetCorners(
                out Vector3 tbl,
                out Vector3 tbr,
                out Vector3 tfl,
                out Vector3 tfr,
                out Vector3 bbl,
                out Vector3 bbr,
                out Vector3 bfl,
                out Vector3 bfr);

            Vector3 min = affine.TransformPosition(tbl);
            Vector3 max = min;

            ExpandAffineBounds(ref min, ref max, affine.TransformPosition(tbr));
            ExpandAffineBounds(ref min, ref max, affine.TransformPosition(tfl));
            ExpandAffineBounds(ref min, ref max, affine.TransformPosition(tfr));
            ExpandAffineBounds(ref min, ref max, affine.TransformPosition(bbl));
            ExpandAffineBounds(ref min, ref max, affine.TransformPosition(bbr));
            ExpandAffineBounds(ref min, ref max, affine.TransformPosition(bfl));
            ExpandAffineBounds(ref min, ref max, affine.TransformPosition(bfr));

            return new AABB(min, max);
        }

        private static void ExpandAffineBounds(ref Vector3 min, ref Vector3 max, in Vector3 point)
        {
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        internal SkinnedMeshBoundsCalculator.Result EnsureLocalBounds(SkinnedMeshBoundsCalculator.Result result)
        {
            if (!IsSkinned || !result.IsWorldSpace)
                return result;

            var basis = GetSkinnedBasisMatrix();
            var invBasis = Matrix4x4.Invert(basis, out var inv) ? inv : Matrix4x4.Identity;
            var worldPositions = result.Positions ?? Array.Empty<Vector3>();
            if (worldPositions.Length == 0)
                return new SkinnedMeshBoundsCalculator.Result(worldPositions, result.Bounds, basis);

            var localPositions = new Vector3[worldPositions.Length];
            for (int i = 0; i < worldPositions.Length; i++)
                localPositions[i] = TransformPosition(worldPositions[i], invBasis);

            var localBounds = SkinnedMeshBoundsCalculator.CalculateBounds(localPositions);
            return new SkinnedMeshBoundsCalculator.Result(localPositions, localBounds, basis);
        }

        private bool UpdateRelativeBoneMatrix(TransformBase bone, bool initialize = false)
        {
            Matrix4x4 relative;
            if (RootBone is not null && ReferenceEquals(bone, RootBone))
                relative = bone.LocalMatrix;
            else
            {
                var inverseBasis = Matrix4x4.Invert(GetSkinnedBasisMatrix(), out var inv)
                    ? inv
                    : Matrix4x4.Identity;
                relative = bone.RenderMatrix * inverseBasis;
            }

            lock (_relativeCacheLock)
            {
                if (!_relativeBoneMatrices.TryGetValue(bone, out var previous) || initialize)
                {
                    _relativeBoneMatrices[bone] = relative;
                    return !initialize;
                }

                if (!MatrixEqual(previous, relative))
                {
                    _relativeBoneMatrices[bone] = relative;
                    return true;
                }
            }

            return false;
        }

        private static bool MatrixEqual(in Matrix4x4 a, in Matrix4x4 b)
        {
            return a.M11 == b.M11 &&
                   a.M12 == b.M12 &&
                   a.M13 == b.M13 &&
                   a.M14 == b.M14 &&
                   a.M21 == b.M21 &&
                   a.M22 == b.M22 &&
                   a.M23 == b.M23 &&
                   a.M24 == b.M24 &&
                   a.M31 == b.M31 &&
                   a.M32 == b.M32 &&
                   a.M33 == b.M33 &&
                   a.M34 == b.M34 &&
                   a.M41 == b.M41 &&
                   a.M42 == b.M42 &&
                   a.M43 == b.M43 &&
                   a.M44 == b.M44;
        }

/*
        private static bool MatrixNearlyEqual(in Matrix4x4 a, in Matrix4x4 b, float epsilon = 1e-4f)
        {
            return MathF.Abs(a.M11 - b.M11) <= epsilon &&
                   MathF.Abs(a.M12 - b.M12) <= epsilon &&
                   MathF.Abs(a.M13 - b.M13) <= epsilon &&
                   MathF.Abs(a.M14 - b.M14) <= epsilon &&
                   MathF.Abs(a.M21 - b.M21) <= epsilon &&
                   MathF.Abs(a.M22 - b.M22) <= epsilon &&
                   MathF.Abs(a.M23 - b.M23) <= epsilon &&
                   MathF.Abs(a.M24 - b.M24) <= epsilon &&
                   MathF.Abs(a.M31 - b.M31) <= epsilon &&
                   MathF.Abs(a.M32 - b.M32) <= epsilon &&
                   MathF.Abs(a.M33 - b.M33) <= epsilon &&
                   MathF.Abs(a.M34 - b.M34) <= epsilon &&
                   MathF.Abs(a.M41 - b.M41) <= epsilon &&
                   MathF.Abs(a.M42 - b.M42) <= epsilon &&
                   MathF.Abs(a.M43 - b.M43) <= epsilon &&
                   MathF.Abs(a.M44 - b.M44) <= epsilon;
        }
*/

        private void UntrackAllBones()
        {
            foreach (var pair in _trackedSkinnedBones.ToArray())
                pair.Key.RenderMatrixChanged -= Bone_RenderMatrixChanged;
            _trackedSkinnedBones.Clear();
            lock (_relativeCacheLock)
                _relativeBoneMatrices.Clear();
        }

        private void Bone_RenderMatrixChanged(TransformBase bone, Matrix4x4 renderMatrix)
        {
            if (!IsSkinned)
                return;

            if (!UpdateRelativeBoneMatrix(bone))
                return;

            MarkSkinnedDataDirty();
        }

        private void MarkSkinnedDataDirty()
        {
            _skinnedBoundsDirty = true;
            _skinnedBoundsRevision++;
            _skinnedBoundsAreWorldSpace = false;
            QueuePendingRenderMatrixUpdate();
            // Do NOT set _skinnedBvhDirty or increment _skinnedBvhVersion here.
            // Bone transform changes happen every frame during animation and the
            // version mismatch causes every in-flight BVH build to be discarded,
            // producing an infinite loop of wasted GenerateBvhJob invocations
            // (severe frame drops). The BVH is built once at setup and reused.
        }

        private bool EnsureSkinnedBounds()
        {
            if (!IsSkinned)
                return false;

            lock (_skinnedDataLock)
            {
                TryFinalizeSkinnedBoundsRefreshLocked();

                if (_hasSkinnedBounds)
                {
                    ApplyCachedSkinnedBoundsLocked();
                    return true;
                }

                if (CurrentLODRenderer?.HasGpuDrivenBoneSource == true)
                {
                    // Keep the initial GPU/readback refresh eligible. Treating the bind-pose
                    // placeholder as a cached skinned result pins debug/culling bounds to the
                    // import-time box and prevents AllowInitialSkinnedBoundsBuildWhenNever from
                    // doing its one real build.
                    if (_skinnedBoundsDirty)
                        return false;

                    Matrix4x4 basis = GetSkinnedBasisMatrix();
                    SetSkinnedRootRenderMatrix(basis);
                    _skinnedLocalBounds = _bindPoseBounds;
                    _skinnedBoundsDirty = false;
                    _hasSkinnedBounds = true;
                    _skinnedBoundsAreWorldSpace = false;
                    if (RenderInfo is not null)
                    {
                        RenderInfo.LocalCullingVolume = _skinnedLocalBounds;
                        RenderInfo.CullingOffsetMatrix = _skinnedRootRenderMatrix;
                    }
                    return true;
                }

                return false;
            }
        }

        private void ApplyCachedSkinnedBoundsLocked()
        {
            Matrix4x4 basis = GetSkinnedBasisMatrix();
            SetSkinnedRootRenderMatrix(basis);
            if (RenderInfo is not null)
            {
                RenderInfo.LocalCullingVolume = _skinnedLocalBounds;
                RenderInfo.CullingOffsetMatrix = _skinnedRootRenderMatrix;
            }
        }

        private bool TryComputeSkinnedBoundsOnGpu(out SkinnedMeshBoundsCalculator.Result result)
            => SkinnedMeshBoundsCalculator.Instance.TryCompute(this, out result);

        private static bool ShouldUseGpuResidentSkinnedBoundsPath()
            => RuntimeEngine.Rendering.Settings.SkinnedBoundsGpuDirectAabbWrite ||
               RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy().IsGpuZeroReadbackStrategy();

        private bool ApplySkinnedBoundsResult(SkinnedMeshBoundsCalculator.Result result, bool markBvhDirty)
        {
            var positions = result.Positions ?? [];
            if (!HasUsableSkinnedBoundsResult(result))
            {
                _skinnedVertexPositions = [];
                _skinnedVertexCount = 0;
                _hasSkinnedBounds = false;
                _skinnedBoundsAreWorldSpace = false;
                return false;
            }

            // The GPU prepass reducer can return an AABB without a CPU vertex snapshot.
            // That is still enough for culling/debug bounds; CPU BVH rebuilds will simply
            // skip until a path with positions is available.
            _skinnedVertexPositions = positions;
            _skinnedVertexCount = positions.Length;
            _skinnedLocalBounds = result.Bounds;
            _skinnedBoundsDirty = false;
            _hasSkinnedBounds = true;
            _skinnedBoundsAreWorldSpace = result.IsWorldSpace;

            SetSkinnedRootRenderMatrix(result.Basis);
            if (RenderInfo is not null)
            {
                RenderInfo.LocalCullingVolume = _skinnedLocalBounds;
                // Bounds are in root bone local space. Use the basis (root bone world matrix)
                // to transform them to world space for culling.
                RenderInfo.CullingOffsetMatrix = _skinnedRootRenderMatrix;
            }
            return true;
        }

        internal static bool HasUsableSkinnedBoundsResult(SkinnedMeshBoundsCalculator.Result result)
        {
            if (!result.Bounds.IsValid)
                return false;

            if (result.Positions is { Length: > 0 })
                return true;

            Vector3 halfExtents = result.Bounds.HalfExtents;
            return halfExtents.LengthSquared() > 1.0e-12f;
        }

        private bool ApplyGpuResidentSkinnedBoundsDispatchLocked()
        {
            var visualScene = World?.VisualScene;
            if (visualScene is null)
                return false;

            if (!SkinnedMeshBoundsCalculator.Instance.DispatchPathADirectWrite(
                this,
                visualScene.GPUCommands,
                _pathAScratchIndices))
            {
                return false;
            }

            SkinnedMeshBoundsCalculator.Instance.RegisterSkinnedMesh(this);

            if (TryComputeSkinnedBoundsOnGpu(out var previewBounds) &&
                ApplySkinnedBoundsResult(previewBounds, markBvhDirty: false))
            {
                return true;
            }

            Matrix4x4 basis = GetSkinnedBasisMatrix();
            SetSkinnedRootRenderMatrix(basis);
            _skinnedVertexPositions = [];
            _skinnedVertexCount = 0;
            _skinnedLocalBounds = _bindPoseBounds;
            _skinnedBoundsDirty = false;
            _hasSkinnedBounds = true;
            _skinnedBoundsAreWorldSpace = false;
            if (RenderInfo is not null)
            {
                RenderInfo.LocalCullingVolume = _skinnedLocalBounds;
                RenderInfo.CullingOffsetMatrix = _skinnedRootRenderMatrix;
            }

            return true;
        }

        private static bool TryComputeSkinnedBoundsOnCpu(SkinnedBoundsCpuSnapshot snapshot, out SkinnedMeshBoundsCalculator.Result result)
        {
            Vertex[] vertices = snapshot.Vertices;
            if (vertices.Length == 0)
            {
                result = default;
                return false;
            }

            bool initialized = false;
            Vector3 min = Vector3.Zero;
            Vector3 max = Vector3.Zero;
            Matrix4x4 fallbackMatrix = snapshot.FallbackMatrix;
            Matrix4x4 basis = snapshot.Basis;
            Matrix4x4 invBasis = Matrix4x4.Invert(basis, out var basisInv) ? basisInv : Matrix4x4.Identity;
            var localPositions = new Vector3[vertices.Length];

            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 worldPos = ComputeSkinnedPosition(vertices[i], fallbackMatrix, snapshot.SkinMatrices, snapshot.BoneReferenceRemap);
                Vector3 localPos = TransformPosition(worldPos, invBasis);
                localPositions[i] = localPos;

                if (!initialized)
                {
                    min = max = localPos;
                    initialized = true;
                }
                else
                {
                    min = Vector3.Min(min, localPos);
                    max = Vector3.Max(max, localPos);
                }
            }

            if (!initialized)
            {
                result = default;
                return false;
            }

            var localBounds = new AABB(min, max);
            result = new SkinnedMeshBoundsCalculator.Result(localPositions, localBounds, basis);
            return true;
        }

        private static Vector3 ComputeSkinnedPosition(
            Vertex vertex,
            Matrix4x4 fallbackMatrix,
            IReadOnlyDictionary<TransformBase, Matrix4x4> skinMatrices,
            IReadOnlyDictionary<TransformBase, TransformBase>? boneReferenceRemap)
        {
            if (vertex.Weights is not { Count: > 0 })
                return TransformPosition(vertex.Position, fallbackMatrix);

            Vector3 result = Vector3.Zero;
            foreach (var (bone, data) in vertex.Weights)
            {
                TransformBase resolvedBone = boneReferenceRemap is not null && boneReferenceRemap.TryGetValue(bone, out TransformBase? reboundBone)
                    ? reboundBone
                    : bone;

                if (!skinMatrices.TryGetValue(resolvedBone, out Matrix4x4 boneMatrix))
                    boneMatrix = data.bindInvWorldMatrix * resolvedBone.RenderMatrix;
                result += TransformPosition(vertex.Position, boneMatrix) * data.weight;
            }
            return result;
        }

        private bool TryFinalizeSkinnedBoundsRefreshLocked()
        {
            if (_skinnedBoundsRefreshTask is null || !_skinnedBoundsRefreshTask.IsCompleted)
                return false;

            long queueWaitTicks = 0L;
            long cpuJobTicks = 0L;
            long applyTicks = 0L;
            bool succeeded = false;

            try
            {
                SkinnedBoundsRefreshResult refresh = _skinnedBoundsRefreshTask.GetAwaiter().GetResult();
                queueWaitTicks = refresh.QueueWaitTicks;
                cpuJobTicks = refresh.CpuJobTicks;
                if (refresh.Succeeded)
                {
                    long applyStartTicks = Stopwatch.GetTimestamp();
                    if (ApplySkinnedBoundsResult(refresh.Result, markBvhDirty: true))
                    {
                        _lastSkinnedBoundsRefreshTicks = RuntimeEngine.ElapsedTicks;
                        _skinnedBoundsDirty = refresh.Revision != _skinnedBoundsRevision;
                        ApplyCachedSkinnedBoundsLocked();
                        succeeded = true;
                    }
                    else if (!_hasSkinnedBounds)
                    {
                        _skinnedBoundsDirty = AllowsInitialRuntimeSkinnedBoundsBuild(
                            RuntimeEngine.EffectiveSettings.SkinnedBoundsRecomputePolicy,
                            RuntimeEngine.EffectiveSettings.AllowInitialSkinnedBoundsBuildWhenNever);
                    }

                    applyTicks = Math.Max(0L, Stopwatch.GetTimestamp() - applyStartTicks);
                }
                else if (!_hasSkinnedBounds)
                {
                    _skinnedBoundsDirty = AllowsInitialRuntimeSkinnedBoundsBuild(
                        RuntimeEngine.EffectiveSettings.SkinnedBoundsRecomputePolicy,
                        RuntimeEngine.EffectiveSettings.AllowInitialSkinnedBoundsBuildWhenNever);
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.RenderingException(ex, "Deferred skinned bounds refresh failed.");
                return false;
            }
            finally
            {
                RuntimeEngine.Rendering.Stats.SkinnedBounds.RecordSkinnedBoundsRefreshDeferredFinished(queueWaitTicks, cpuJobTicks, applyTicks, succeeded);
                _skinnedBoundsRefreshTask = null;
            }
        }

        private SkinnedBoundsCpuSnapshot? CreateSkinnedBoundsCpuSnapshotLocked()
        {
            XRMesh? mesh = CurrentLODRenderer?.Mesh;
            Vertex[]? vertices = mesh?.Vertices;
            if (mesh is null || vertices is null || vertices.Length == 0)
                return null;

            var skinMatrices = new Dictionary<TransformBase, Matrix4x4>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
            foreach (var (bone, invBind) in mesh.UtilizedBones)
            {
                if (bone is null)
                    continue;
                skinMatrices[bone] = invBind * bone.RenderMatrix;
            }

            return new SkinnedBoundsCpuSnapshot(
                vertices,
                skinMatrices,
                mesh.RuntimeBoneReferenceRemap,
                Component.Transform.RenderMatrix,
                GetSkinnedBasisMatrix());
        }

        private static Task<SkinnedBoundsRefreshResult> RunSkinnedBoundsJobAsync(SkinnedBoundsCpuSnapshot snapshot, int revision)
        {
            var tcs = new TaskCompletionSource<SkinnedBoundsRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            long queuedTicks = Stopwatch.GetTimestamp();
            RuntimeEngine.Rendering.Stats.SkinnedBounds.RecordSkinnedBoundsRefreshDeferredScheduled();
            RuntimeEngine.Jobs.Schedule(() => RunSkinnedBoundsJob(snapshot, revision, queuedTicks, tcs), priority: JobPriority.Low);
            return tcs.Task;
        }

        private static System.Collections.IEnumerable RunSkinnedBoundsJob(
            SkinnedBoundsCpuSnapshot snapshot,
            int revision,
            long queuedTicks,
            TaskCompletionSource<SkinnedBoundsRefreshResult> tcs)
        {
            try
            {
                long startedTicks = Stopwatch.GetTimestamp();
                bool succeeded = TryComputeSkinnedBoundsOnCpu(snapshot, out var result);
                long completedTicks = Stopwatch.GetTimestamp();
                tcs.TrySetResult(new SkinnedBoundsRefreshResult(
                    revision,
                    result,
                    succeeded,
                    Math.Max(0L, startedTicks - queuedTicks),
                    Math.Max(0L, completedTicks - startedTicks)));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }

            yield break;
        }

        private void ProcessSkinnedBoundsRefresh()
        {
            if (!IsSkinned)
                return;

            bool requeue = false;

            lock (_skinnedDataLock)
            {
                TryFinalizeSkinnedBoundsRefreshLocked();

                ESkinnedBoundsRecomputePolicy policy = RuntimeEngine.EffectiveSettings.SkinnedBoundsRecomputePolicy;
                bool allowInitialBuildWhenNever = RuntimeEngine.EffectiveSettings.AllowInitialSkinnedBoundsBuildWhenNever;
                bool allowMissingBoundsRefresh = AllowsInitialRuntimeSkinnedBoundsBuild(policy, allowInitialBuildWhenNever);
                bool refreshInFlight = _skinnedBoundsRefreshTask is not null;
                long nowTicks = RuntimeEngine.ElapsedTicks;
                if (ShouldScheduleSkinnedBoundsRefresh(
                    policy,
                    allowInitialBuildWhenNever,
                    _hasSkinnedBounds,
                    _skinnedBoundsDirty,
                    refreshInFlight,
                    nowTicks,
                    _lastSkinnedBoundsRefreshTicks))
                {
                    if (RuntimeEngine.Rendering.Settings.CalculateSkinnedBoundsInComputeShader && RuntimeEngine.IsRenderThread)
                    {
                        long gpuStartTicks = Stopwatch.GetTimestamp();
                        int revision = _skinnedBoundsRevision;
                        bool useGpuResidentBounds = ShouldUseGpuResidentSkinnedBoundsPath();
                        if (useGpuResidentBounds)
                        {
                            if (ApplyGpuResidentSkinnedBoundsDispatchLocked())
                            {
                                _lastSkinnedBoundsRefreshTicks = nowTicks;
                                _skinnedBoundsDirty = revision != _skinnedBoundsRevision;
                                long gpuTicks = Math.Max(0L, Stopwatch.GetTimestamp() - gpuStartTicks);
                                RuntimeEngine.Rendering.Stats.SkinnedBounds.RecordSkinnedBoundsRefreshGpuCompleted(gpuTicks, applyTicks: 0L);
                            }
                            else if (!_hasSkinnedBounds)
                            {
                                _skinnedBoundsDirty = allowMissingBoundsRefresh;
                            }
                        }
                        else if (TryComputeSkinnedBoundsOnGpu(out var gpuResult))
                        {
                            long applyStartTicks = Stopwatch.GetTimestamp();
                            if (ApplySkinnedBoundsResult(gpuResult, markBvhDirty: true))
                            {
                                _lastSkinnedBoundsRefreshTicks = nowTicks;
                                _skinnedBoundsDirty = revision != _skinnedBoundsRevision;
                                ApplyCachedSkinnedBoundsLocked();
                                long applyTicks = Math.Max(0L, Stopwatch.GetTimestamp() - applyStartTicks);
                                long gpuTicks = Math.Max(0L, applyStartTicks - gpuStartTicks);
                                RuntimeEngine.Rendering.Stats.SkinnedBounds.RecordSkinnedBoundsRefreshGpuCompleted(gpuTicks, applyTicks);
                            }
                            else if (!_hasSkinnedBounds)
                            {
                                _skinnedBoundsDirty = allowMissingBoundsRefresh;
                            }
                        }
                    }
                    else
                    {
                        SkinnedBoundsCpuSnapshot? snapshot = CreateSkinnedBoundsCpuSnapshotLocked();
                        if (snapshot.HasValue)
                            _skinnedBoundsRefreshTask = RunSkinnedBoundsJobAsync(snapshot.Value, _skinnedBoundsRevision);
                    }
                }

                if (_skinnedBoundsRefreshTask is not null || (_skinnedBoundsDirty && allowMissingBoundsRefresh && !_hasSkinnedBounds))
                    requeue = true;
            }

            if (requeue)
                QueuePendingRenderMatrixUpdate();
        }

        public BVH<Triangle>? GetSkinnedBvh(bool allowRebuild = true)
        {
            if (!IsSkinned)
                return CurrentLODRenderer?.Mesh?.BVHTree;

            lock (_skinnedDataLock)
            {
                // Try to finalize any pending background build first.
                if (_skinnedBvhTask is not null && TryFinalizeSkinnedBvhJob(out var readyTree))
                    return readyTree;

                // Return existing BVH immediately. Skinned BVH is built once at
                // setup; continuous rebuilds on every bone change during animation
                // cause severe frame drops (GenerateBvhJob infinite-loop).
                if (_skinnedBvh is not null)
                    return _skinnedBvh;

                if (!allowRebuild)
                    return null;

                if (_skinnedBoundsDirty)
                {
                    TryFinalizeSkinnedBoundsRefreshLocked();
                    if (!_hasSkinnedBounds)
                        return null;
                }

                if (!EnsureSkinnedBounds())
                    return null;

                // Schedule BVH build once so raycasting works on skinned meshes.
                // Continuous rebuilds during animation cause severe frame drops,
                // so we only do this once and reuse the cached tree.
                if (!_skinnedBvhScheduledOnce)
                {
                    _skinnedBvhScheduledOnce = true;
                    ScheduleSkinnedBvhJobIfNeeded();
                }
                return null;
            }
        }


        private void ScheduleSkinnedBvhJobIfNeeded()
        {
            if (_skinnedBvhTask is not null)
                return;

            if (RuntimeEngine.Rendering.Settings.CalculateSkinnedBoundsInComputeShader)
            {
                _skinnedBvhTask = SkinnedMeshBvhScheduler.Instance.Schedule(this, _skinnedBvhVersion);
            }
            else if (_skinnedVertexPositions is { Length: > 0 })
            {
                _skinnedBvhTask = SkinnedMeshBvhScheduler.Instance.Schedule(
                    this,
                    _skinnedBvhVersion,
                    _skinnedVertexPositions,
                    _skinnedLocalBounds,
                    _skinnedRootRenderMatrix);
            }
        }

        private bool TryFinalizeSkinnedBvhJob(out BVH<Triangle>? tree)
        {
            tree = null;
            if (_skinnedBvhTask is null || !_skinnedBvhTask.IsCompleted)
                return false;

            try
            {
                var result = _skinnedBvhTask.GetAwaiter().GetResult();
                if (result.Version != _skinnedBvhVersion)
                    return false;

                ApplySkinnedBoundsResult(result.Bounds, markBvhDirty: false);
                _skinnedBvh = result.Tree;
                tree = _skinnedBvh;
                return true;
            }
            catch (Exception ex)
            {
                Debug.RenderingException(ex, "Skinned BVH compute path failed.");
                _skinnedBvh = null;
                return true;
            }
            finally
            {
                _skinnedBvhTask = null;
            }
        }

        public void UpdateLOD(XRCamera camera)
            => UpdateLOD(camera.DistanceFromRenderNearPlane(Component.Transform.RenderTranslation));
        public void UpdateLOD(float distanceToCamera)
        {
            lock (_lodsLock)
            {
                if (LODs.Count == 0)
                    return;

                if (_currentLOD is null)
                {
                    CurrentLOD = LODs.First;
                    return;
                }

                while (_currentLOD.Next is not null && distanceToCamera > _currentLOD.Value.MaxVisibleDistance)
                    CurrentLOD = _currentLOD.Next;

                if (_currentLOD.Previous is not null && distanceToCamera < _currentLOD.Previous.Value.MaxVisibleDistance)
                    CurrentLOD = _currentLOD.Previous;
            }
        }

        public void Dispose()
        {
            RuntimeEngine.Rendering.SettingsChanged -= Rendering_SettingsChanged;
            RenderInfo.PropertyChanged -= RenderInfoPropertyChanged;
            UntrackAllBones();
            SkinnedMeshBoundsCalculator.Instance.UnregisterSkinnedMesh(this, World?.VisualScene?.GPUCommands);
            RenderableLOD[] lods;
            lock (_lodsLock)
            {
                lods = [.. LODs];
                CurrentLOD = null;
                LODs.Clear();
            }

            foreach (RenderableLOD lod in lods)
                lod.Renderer.Destroy();

            foreach (XRMesh mesh in _ownedRuntimeMeshes)
                mesh.Destroy(now: true);
            _ownedRuntimeMeshes.Clear();

            lock (_highlightStateLock)
            {
                _rc.MaterialOverride = null;
                _rc.RenderOptionsOverride = null;
                _rc.ForceCpuRendering = false;
                _highlightRenderOptionsOverride = null;
                _highlightSourceMaterial = null;
                _highlightStencilBits = 0;
            }

            GC.SuppressFinalize(this);
        }

        private TransformBase? GetTransformReferenceSearchRoot()
        {
            SceneNode? node = Component.SceneNode;
            if (node is null)
                return null;

            Guid prefabAssetId = node.Prefab?.PrefabAssetId ?? Guid.Empty;
            while (node.Parent is SceneNode parent)
            {
                if (prefabAssetId != Guid.Empty && (parent.Prefab?.PrefabAssetId ?? Guid.Empty) != prefabAssetId)
                    break;

                node = parent;
            }

            return node.Transform;
        }

        private static TransformBase? ResolveTransformReference(TransformBase? source, TransformBase? searchRoot)
        {
            if (source is null || searchRoot is null)
                return source;

            if (IsSelfOrDescendantOf(searchRoot, source))
                return source;

            Guid referenceId = source.EffectiveSerializedReferenceId;
            if (referenceId == Guid.Empty)
                return source;

            return searchRoot.FindSelfOrDescendantBySerializedReferenceId(referenceId) ?? source;
        }

        private XRMesh? CreateRuntimeMesh(XRMesh? sourceMesh, TransformBase? searchRoot)
        {
            if (sourceMesh is null || searchRoot is null || !sourceMesh.NeedsSerializedTransformRebind(searchRoot))
                return sourceMesh;

            XRMesh reboundMesh = sourceMesh.CloneForRuntimeTransformRebind();
            if (!reboundMesh.RebindSerializedTransformReferences(searchRoot, remapVertexWeights: false))
            {
                reboundMesh.Destroy(now: true);
                return sourceMesh;
            }

            _ownedRuntimeMeshes.Add(reboundMesh);
            return reboundMesh;
        }

        private static bool IsSelfOrDescendantOf(TransformBase root, TransformBase candidate)
        {
            for (TransformBase? current = candidate; current is not null; current = current.Parent)
            {
                if (ReferenceEquals(current, root))
                    return true;
            }

            return false;
        }

        private void ReleaseOwnedRuntimeMesh(XRMesh? mesh)
        {
            if (mesh is null || !_ownedRuntimeMeshes.Remove(mesh))
                return;

            mesh.Destroy(now: true);
        }

        private void Rendering_SettingsChanged()
        {
            bool isSkinned = IsSkinned;
            if (isSkinned == _lastRenderSkinningEnabled)
                return;

            _lastRenderSkinningEnabled = isSkinned;
            if (isSkinned)
            {
                XRMeshRenderer? renderer = CurrentLODRenderer;
                if (renderer?.EnsureSkinningBuffers(logWarnings: false) == true)
                    renderer.RefreshBoneMatricesFromRenderState();
                MarkSkinnedDataDirty();

                if (RootBone is not null)
                    MarkPendingRootBoneRenderMatrix(GetCurrentTransformMatrix(RootBone));
                else
                    SetSkinnedRootRenderMatrix(GetCurrentTransformMatrix(Component.Transform));
            }

            MarkPendingComponentRenderMatrix(GetCurrentTransformMatrix(Component.Transform));
        }

        private TransformBase? DetermineRootBoneFromRenderers()
        {
            var bones = new HashSet<TransformBase>(System.Collections.Generic.ReferenceEqualityComparer.Instance);

            lock (_lodsLock)
            {
                foreach (RenderableLOD lod in LODs)
                {
                    XRMesh? mesh = lod.Renderer.Mesh;
                    if (mesh?.HasSkinning != true)
                        continue;

                    foreach (var (bone, _) in mesh.UtilizedBones)
                    {
                        if (bone is not null)
                            bones.Add(bone);
                    }
                }
            }

            if (bones.Count == 0)
                return null;

            return TransformBase.FindCommonAncestor([.. bones]);
        }

        [RequiresDynamicCode("")]
        public float? Intersect(Segment localSpaceSegment, out Triangle? triangle)
        {
            triangle = null;
            return CurrentLODRenderer?.Mesh?.Intersect(localSpaceSegment, out triangle);
        }

        public Segment GetLocalSegment(Segment worldSegment, bool skinnedMesh)
        {
            Segment localSegment;
            if (skinnedMesh)
            {
                localSegment = worldSegment.TransformedBy(SkinnedBvhWorldToLocalMatrix);
            }
            else
            {
                localSegment = worldSegment.TransformedBy(Component.Transform.InverseWorldMatrix);
            }

            return localSegment;
        }

        /// <summary>
        /// Attempts to retrieve the current world-space bounds for this mesh, preferring skinned bounds when available.
        /// </summary>
        public bool TryGetWorldBounds(out AABB worldBounds)
        {
            // Default to an invalid box so callers can check IsValid before use.
            worldBounds = default;

            // Prefer the live skinned bounds when skinning is active and successfully computed.
            if (IsSkinned && EnsureSkinnedBounds())
            {
                worldBounds = TransformBounds(_skinnedLocalBounds, _skinnedRootRenderMatrix);
                return worldBounds.IsValid;
            }

            // Fall back to the bind-pose/local culling bounds.
            AABB localBounds = RenderInfo?.LocalCullingVolume ?? _bindPoseBounds;
            if (!localBounds.IsValid)
                return false;

            Matrix4x4 basis = Component.Transform.RenderMatrix;
            worldBounds = TransformBounds(localBounds, basis);
            return worldBounds.IsValid;
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(RootBone):
                        if (RootBone is not null)
                        {
                            RootBone.WorldMatrixChanged -= RootBone_WorldMatrixPreviewChanged;
                            RootBone.RenderMatrixChanged -= RootBone_WorldMatrixChanged;
                        }
                        break;

                    case nameof(Component):
                        if (Component is not null)
                        {
                            Component.Transform.WorldMatrixChanged -= Component_WorldMatrixPreviewChanged;
                            Component.Transform.RenderMatrixChanged -= Component_WorldMatrixChanged;
                            Component.PropertyChanged -= ComponentPropertyChanged;
                            Component.PropertyChanging -= ComponentPropertyChanging;
                        }
                        break;
                }
            }
            return change;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(RootBone):
                    if (RootBone is not null)
                    {
                        RootBone.WorldMatrixChanged += RootBone_WorldMatrixPreviewChanged;
                        RootBone.RenderMatrixChanged += RootBone_WorldMatrixChanged;
                        RootBone_WorldMatrixPreviewChanged(RootBone, RootBone.WorldMatrix);
                        RootBone_WorldMatrixChanged(RootBone, RootBone.RenderMatrix);
                    }
                    break;
                case nameof(Component):
                    if (Component is not null)
                    {
                        Component.Transform.WorldMatrixChanged += Component_WorldMatrixPreviewChanged;
                        Component.Transform.RenderMatrixChanged += Component_WorldMatrixChanged;
                        Component_WorldMatrixPreviewChanged(Component.Transform, Component.Transform.WorldMatrix);
                        Component_WorldMatrixChanged(Component.Transform, Component.Transform.RenderMatrix);
                        Component.PropertyChanged += ComponentPropertyChanged;
                        Component.PropertyChanging += ComponentPropertyChanging;
                    }
                    break;
                case nameof(RenderBounds):
                    if (RenderBounds)
                    {
                        if (!RenderInfo.RenderCommands.Contains(_renderBoundsCommand))
                            RenderInfo.RenderCommands.Add(_renderBoundsCommand);
                    }
                    else
                        RenderInfo.RenderCommands.Remove(_renderBoundsCommand);
                    break;
                case nameof(CurrentLOD):
                    if (CurrentLOD is not null)
                    {
                        var rend = CurrentLODRenderer;
                        bool skinned = (rend?.Mesh?.HasSkinning ?? false) && RuntimeEngine.Rendering.Settings.AllowSkinning;
                        _lastRenderSkinningEnabled = skinned;
                        _rc.WorldMatrix = skinned ? Matrix4x4.Identity : Component.Transform.RenderMatrix;
                    }
                    break;
            }
        }

        /// <summary>
        /// Updates the culling offset matrix for skinned meshes when the root bone moves.
        /// </summary>
        private void RootBone_WorldMatrixChanged(TransformBase rootBone, Matrix4x4 renderMatrix)
        {
            if (RuntimeEngine.IsRenderThread)
            {
                ApplyImmediateRenderMatrixUpdate(componentMatrix: null, rootMatrix: renderMatrix);
                return;
            }

            MarkPendingRootBoneRenderMatrix(renderMatrix);
        }

        private void RootBone_WorldMatrixPreviewChanged(TransformBase rootBone, Matrix4x4 worldMatrix)
        {
            bool hasSkinning = (CurrentLODRenderer?.Mesh?.HasSkinning ?? false) && RuntimeEngine.Rendering.Settings.AllowSkinning;
            if (!hasSkinning)
                return;

            Matrix4x4 basis = GetSkinnedBasisMatrix();
            SetSkinnedRootRenderMatrix(basis);
            RenderInfo?.CullingOffsetMatrix = basis;
        }

        /// <summary>
        /// Updates the culling offset matrix for non-skinned meshes when the component moves.
        /// </summary>
        private void Component_WorldMatrixChanged(TransformBase component, Matrix4x4 renderMatrix)
        {
            if (RuntimeEngine.IsRenderThread)
            {
                ApplyImmediateRenderMatrixUpdate(componentMatrix: renderMatrix, rootMatrix: null);
                return;
            }

            MarkPendingComponentRenderMatrix(renderMatrix);
        }

        private void Component_WorldMatrixPreviewChanged(TransformBase component, Matrix4x4 worldMatrix)
        {
            bool hasSkinning = (CurrentLODRenderer?.Mesh?.HasSkinning ?? false) && RuntimeEngine.Rendering.Settings.AllowSkinning;
            if (hasSkinning)
            {
                Matrix4x4 basis = GetSkinnedBasisMatrix();
                SetSkinnedRootRenderMatrix(basis);
                RenderInfo?.CullingOffsetMatrix = basis;
                return;
            }

            RenderInfo?.CullingOffsetMatrix = worldMatrix;
        }

        private void ApplyImmediateRenderMatrixUpdate(Matrix4x4? componentMatrix, Matrix4x4? rootMatrix)
        {
            bool hasSkinning = (CurrentLODRenderer?.Mesh?.HasSkinning ?? false) && RuntimeEngine.Rendering.Settings.AllowSkinning;
            if (hasSkinning)
            {
                Matrix4x4 basis = GetSkinnedBasisMatrix();
                _rc.WorldMatrix = Matrix4x4.Identity;
                SetSkinnedRootRenderMatrix(basis);
                RenderInfo?.CullingOffsetMatrix = basis;

                return;
            }

            Matrix4x4 matrix = componentMatrix ?? GetCurrentTransformMatrix(Component.Transform);
            _rc?.WorldMatrix = matrix;

            RenderInfo?.CullingOffsetMatrix = matrix;
        }

        internal void QueueCurrentRenderMatrixUpdate()
        {
            if (RuntimeEngine.IsRenderThread)
            {
                ApplyImmediateRenderMatrixUpdate(
                    GetCurrentTransformMatrix(Component.Transform),
                    RootBone is null ? null : GetCurrentTransformMatrix(RootBone));
                return;
            }

            MarkPendingComponentRenderMatrix(GetCurrentTransformMatrix(Component.Transform));

            if (RootBone is not null)
                MarkPendingRootBoneRenderMatrix(GetCurrentTransformMatrix(RootBone));
        }

        private void QueuePendingRenderMatrixUpdate()
        {
            if (Interlocked.Exchange(ref _pendingRenderMatrixQueued, 1) == 0)
                _pendingRenderMatrixUpdates.Enqueue(this);
        }

        private void MarkPendingComponentRenderMatrix(Matrix4x4 renderMatrix)
        {
            lock (_pendingRenderMatrixLock)
            {
                _pendingComponentRenderMatrix = renderMatrix;
                _pendingComponentRenderMatrixVersion++;
            }

            QueuePendingRenderMatrixUpdate();
        }

        private void MarkPendingRootBoneRenderMatrix(Matrix4x4 renderMatrix)
        {
            lock (_pendingRenderMatrixLock)
            {
                _pendingRootBoneRenderMatrix = renderMatrix;
                _pendingRootBoneRenderMatrixVersion++;
            }

            QueuePendingRenderMatrixUpdate();
        }

        private void ApplyPendingRenderMatrixUpdates()
        {
            int componentVersion;
            int rootBoneVersion;
            Matrix4x4 componentMatrix;

            lock (_pendingRenderMatrixLock)
            {
                componentVersion = _pendingComponentRenderMatrixVersion;
                rootBoneVersion = _pendingRootBoneRenderMatrixVersion;
                componentMatrix = _pendingComponentRenderMatrix;
            }

            bool hasSkinning = (CurrentLODRenderer?.Mesh?.HasSkinning ?? false) && RuntimeEngine.Rendering.Settings.AllowSkinning;
            if (hasSkinning)
            {
                Matrix4x4 basis = GetSkinnedBasisMatrix();
                _rc?.WorldMatrix = Matrix4x4.Identity;
                SetSkinnedRootRenderMatrix(basis);
                RenderInfo?.CullingOffsetMatrix = basis;
            }
            else
            {
                _rc?.WorldMatrix = componentMatrix;

                RenderInfo?.CullingOffsetMatrix = componentMatrix;
            }

            Interlocked.Exchange(ref _pendingRenderMatrixQueued, 0);

            lock (_pendingRenderMatrixLock)
            {
                if (_pendingComponentRenderMatrixVersion != componentVersion ||
                    _pendingRootBoneRenderMatrixVersion != rootBoneVersion)
                {
                    QueuePendingRenderMatrixUpdate();
                }
            }

            // Matrix changes are applied in the world's SwapBuffers phase after visible
            // collection has already run. Publish the command snapshot here too; otherwise
            // dirty-delta command swapping can leave the rendered matrix one frame behind.
            _rc?.SwapBuffers();

            ProcessSkinnedBoundsRefresh();
        }

        internal static void ProcessPendingRenderMatrixUpdates()
        {
            // Bound draining to a snapshot of the current queue length. ApplyPendingRenderMatrixUpdates
            // -> ProcessSkinnedBoundsRefresh may re-enqueue the same mesh while an async bounds refresh
            // is in flight (or bounds are still being built for the first time); draining until empty
            // would spin indefinitely. Re-enqueued meshes are picked up on the next swap.
            int remaining = _pendingRenderMatrixUpdates.Count;
            while (remaining-- > 0 && _pendingRenderMatrixUpdates.TryDequeue(out var mesh))
                mesh.ApplyPendingRenderMatrixUpdates();
        }
    }
}
