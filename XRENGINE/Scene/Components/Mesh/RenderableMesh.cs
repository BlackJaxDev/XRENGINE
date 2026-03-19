using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
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
using XREngine.Scene.Transforms;
using XREngine.Timers;

namespace XREngine.Components.Scene.Mesh
{
    public class RenderableMesh : XRBase, IDisposable
    {
        private static readonly ConcurrentQueue<RenderableMesh> _pendingRenderMatrixUpdates = new();
        public RenderInfo3D RenderInfo { get; }

        private readonly RenderCommandMesh3D _rc;

        private readonly Dictionary<TransformBase, int> _trackedSkinnedBones = new();
        private readonly Dictionary<TransformBase, Matrix4x4> _currentSkinMatrices = new();
        private readonly Dictionary<TransformBase, Matrix4x4> _relativeBoneMatrices = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        private readonly object _relativeCacheLock = new();
        private readonly object _skinnedDataLock = new();
        private bool _skinnedBoundsDirty = true;
        private bool _hasSkinnedBounds;
        private bool _skinnedBoundsAreWorldSpace;
        private AABB _skinnedLocalBounds;
        private Vector3[]? _skinnedVertexPositions;
        private int _skinnedVertexCount;
        private BVH<Triangle>? _skinnedBvh;
        private Task<SkinnedMeshBvhScheduler.Result>? _skinnedBvhTask;
        private int _skinnedBvhVersion = 0;
        private bool _skinnedBvhScheduledOnce;
        private AABB _bindPoseBounds;
        private Matrix4x4 _skinnedRootRenderMatrix = Matrix4x4.Identity;
        private Matrix4x4 _skinnedRootRenderMatrixInverse = Matrix4x4.Identity;

        /// <summary>
        /// Minimum interval (in seconds) between expensive skinned bounds recomputations.
        /// The root bone matrix is updated every frame regardless, so culling remains correct;
        /// only the local AABB shape is recalculated at this cadence.
        /// </summary>
        private const float SkinnedBoundsRefreshInterval = 5.0f;
        private static readonly long SkinnedBoundsRefreshIntervalTicks = EngineTimer.SecondsToStopwatchTicks(SkinnedBoundsRefreshInterval);
        private long _lastSkinnedBoundsRefreshTicks = long.MinValue;

        internal static bool ShouldReuseSkinnedBounds(long nowTicks, long lastRefreshTicks)
            => lastRefreshTicks != long.MinValue && Math.Max(0L, nowTicks - lastRefreshTicks) < SkinnedBoundsRefreshIntervalTicks;

        public Matrix4x4 SkinnedBvhLocalToWorldMatrix => _skinnedRootRenderMatrix;
        public Matrix4x4 SkinnedBvhWorldToLocalMatrix => _skinnedRootRenderMatrixInverse;

        private void SetSkinnedRootRenderMatrix(Matrix4x4 matrix)
        {
            _skinnedRootRenderMatrix = matrix;
            _skinnedRootRenderMatrixInverse = Matrix4x4.Invert(matrix, out var inv) ? inv : Matrix4x4.Identity;
        }

        public XRMeshRenderer? CurrentLODRenderer => CurrentLOD?.Value?.Renderer;
        public XRMesh? CurrentLODMesh => CurrentLOD?.Value?.Renderer?.Mesh;

        private LinkedListNode<RenderableLOD>? _currentLOD = null;
        public LinkedListNode<RenderableLOD>? CurrentLOD
        {
            get => _currentLOD;
            private set => SetField(ref _currentLOD, value);
        }
        public XRWorldInstance? World => Component.SceneNode.World as XRWorldInstance;
        public LinkedList<RenderableLOD> LODs { get; private set; } = new();

        private bool _renderBounds = Engine.EditorPreferences.Debug.RenderMesh3DBounds;
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
            => (CurrentLODRenderer?.Mesh?.HasSkinning ?? false) && Engine.Rendering.Settings.AllowSkinning;

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
            RootBone = mesh.RootBone;

            foreach (var lod in mesh.LODs)
            {
                var renderer = lod.NewRenderer();
                renderer.SettingUniforms += SettingUniforms;
                void UpdateReferences(object? s, IXRPropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(SubMeshLOD.Mesh))
                    {
                        TrackBones(renderer.Mesh, false);
                        renderer.Mesh = lod.Mesh;
                        TrackBones(renderer.Mesh, true);
                        MarkSkinnedDataDirty();
                    }
                    else if (e.PropertyName == nameof(SubMeshLOD.Material))
                        renderer.Material = lod.Material;
                }
                lod.PropertyChanged += UpdateReferences;
                LODs.AddLast(new RenderableLOD(renderer, lod.MaxVisibleDistance));
                TrackBones(lod.Mesh, true);
            }

            _renderBoundsCommand = new RenderCommandMethod3D((int)EDefaultRenderPass.OpaqueForward, DoRenderBounds);
            RenderInfo = RenderInfo3D.New(component, _rc = new RenderCommandMesh3D(0));
            if (RenderBounds)
                RenderInfo.RenderCommands.Add(_renderBoundsCommand);
            RenderInfo.LocalCullingVolume = mesh.CullingBounds ?? mesh.Bounds;
            _bindPoseBounds = RenderInfo.LocalCullingVolume ?? mesh.Bounds;
            RenderInfo.PreCollectCommandsCallback = BeforeAdd;

            if (LODs.Count > 0)
                CurrentLOD = LODs.First;
            
            // Set initial mesh renderer for GPU scene (will be updated in BeforeAdd if needed)
            _rc.Mesh = CurrentLODRenderer;
            var mat = CurrentLODRenderer?.Material;
            if (mat is not null)
                _rc.RenderPass = mat.RenderPass;
        }

        private void DoRenderBounds()
        {
            if (Engine.Rendering.State.IsShadowPass)
                return;

            var debug = Engine.EditorPreferences.Debug;
            bool showTransparencyModeOverlay = debug.VisualizeTransparencyModeOverlay;
            bool showTransparencyClassificationOverlay = debug.VisualizeTransparencyClassificationOverlay;

            XRMaterial? material = CurrentLODRenderer?.Material;
            ColorF4 boundsColor = ColorF4.White;

            if (showTransparencyModeOverlay && material is not null)
                boundsColor = GetTransparencyModeColor(material.GetEffectiveTransparencyMode());
            else if (showTransparencyClassificationOverlay && material is not null)
                boundsColor = GetTransparencyClassificationColor(material.GetEffectiveTransparencyMode());

            var box = (RenderInfo as IOctreeItem)?.WorldCullingVolume;
            if (box is not null)
            {
                Engine.Rendering.Debug.RenderBox(box.Value.LocalHalfExtents, box.Value.LocalCenter, box.Value.Transform, false, boundsColor);

                if (material is not null && (showTransparencyModeOverlay || showTransparencyClassificationOverlay))
                {
                    string label = showTransparencyModeOverlay
                        ? material.GetEffectiveTransparencyMode().ToString()
                        : GetTransparencyClassificationLabel(material.GetEffectiveTransparencyMode());
                    Engine.Rendering.Debug.RenderText(box.Value.LocalCenter, label, boundsColor);
                }
            }

            if (RootBone is not null)
            {
                Vector3 rootTranslation = RootBone.RenderTranslation;
                Engine.Rendering.Debug.RenderPoint(rootTranslation, ColorF4.Red);
                if (RootBone.Name is not null)
                    Engine.Rendering.Debug.RenderText(rootTranslation, RootBone.Name, ColorF4.Black);
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

        private bool BeforeAdd(RenderInfo info, RenderCommandCollection passes, XRCamera? camera)
        {
            var rend = CurrentLODRenderer;
            bool skinned = (rend?.Mesh?.HasSkinning ?? false) && Engine.Rendering.Settings.AllowSkinning;
            TransformBase tfm = skinned ? RootBone ?? Component.Transform : Component.Transform;
            float distance = camera?.DistanceFromRenderNearPlane(tfm.RenderTranslation) ?? 0.0f;

            if (!passes.IsShadowPass)
                UpdateLOD(distance);

            rend = CurrentLODRenderer;
            skinned = (rend?.Mesh?.HasSkinning ?? false) && Engine.Rendering.Settings.AllowSkinning;
            if (skinned)
            {
                if (!EnsureSkinnedBounds())
                {
                    // Fallback to bind-pose bounds when GPU skinned bounds fail.
                    // For skinned meshes, the bind-pose bounds are in mesh-local space (before any import rotation).
                    // We should NOT transform by Component.Transform because that includes import rotation
                    // which would double-rotate the bounds. Instead, use identity for world-space aligned bounds
                    // OR transform bind-pose through the root bone which has the correct hierarchy.
                    RenderInfo.LocalCullingVolume = _bindPoseBounds;
                    // Use root bone if available (it includes proper hierarchy), otherwise component transform
                    RenderInfo.CullingOffsetMatrix = GetCurrentCullingBasisMatrix(RootBone ?? Component.Transform);
                }
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
                _rc.RenderPass = mat.RenderPass;

            return true;
        }

        public record RenderableLOD(XRMeshRenderer Renderer, float MaxVisibleDistance);

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

        /// <summary>
        /// Returns the matrix used to transform skinned mesh bounds from world space to local space.
        /// For skinned meshes, this is the root bone's world matrix (bounds are in root bone local space).
        /// For non-skinned meshes, this is the component's world matrix.
        /// </summary>
        private Matrix4x4 GetSkinnedBasisMatrix()
        {
            // Skinned mesh bounds should be calculated in root bone local space.
            // The root bone's world matrix transforms from root bone local to world space.
            // Its inverse transforms from world space to root bone local space.
            // Fallback to component transform if no root bone.
            return RootBone is not null
                ? GetCurrentCullingBasisMatrix(RootBone)
                : Component is not null ? GetCurrentCullingBasisMatrix(Component.Transform) : Matrix4x4.Identity;
        }

        private static Matrix4x4 GetCurrentCullingBasisMatrix(TransformBase transform)
            => Engine.IsRenderThread ? transform.RenderMatrix : transform.WorldMatrix;

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
            _hasSkinnedBounds = false;
            _skinnedBoundsAreWorldSpace = false;
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
                if (!_skinnedBoundsDirty && _hasSkinnedBounds)
                    return true;

                // Once initialized, reuse cached skinned local bounds shape and only
                // update root-bone basis. This avoids repeated expensive recompute
                // stalls in the animation hot path.
                if (_hasSkinnedBounds)
                {
                    Matrix4x4 basis = RootBone?.RenderMatrix ?? Component.Transform.RenderMatrix;
                    SetSkinnedRootRenderMatrix(basis);
                    if (RenderInfo is not null)
                    {
                        RenderInfo.LocalCullingVolume = _skinnedLocalBounds;
                        RenderInfo.CullingOffsetMatrix = _skinnedRootRenderMatrix;
                    }
                    _skinnedBoundsDirty = false;
                    return true;
                }

                // If we already have valid bounds from a previous computation, reuse them
                // with the updated root-bone matrix instead of recomputing every frame.
                // Full recomputation is throttled to SkinnedBoundsRefreshInterval seconds.
                if (_hasSkinnedBounds || _skinnedLocalBounds.Max != Vector3.Zero || _skinnedLocalBounds.Min != Vector3.Zero)
                {
                    long nowTicks = Engine.ElapsedTicks;
                    if (ShouldReuseSkinnedBounds(nowTicks, _lastSkinnedBoundsRefreshTicks))
                    {
                        // Reuse existing local bounds shape but update the offset matrix
                        // so culling stays correct as the root bone moves.
                        Matrix4x4 basis = RootBone?.RenderMatrix ?? Component.Transform.RenderMatrix;
                        SetSkinnedRootRenderMatrix(basis);
                        if (RenderInfo is not null)
                        {
                            RenderInfo.LocalCullingVolume = _skinnedLocalBounds;
                            RenderInfo.CullingOffsetMatrix = _skinnedRootRenderMatrix;
                        }
                        _skinnedBoundsDirty = false;
                        _hasSkinnedBounds = true;
                        return true;
                    }
                }

                bool useGpu = Engine.IsRenderThread && Engine.Rendering.Settings.CalculateSkinnedBoundsInComputeShader;

                // Try GPU path first if enabled
                if (useGpu && TryComputeSkinnedBoundsOnGpu(out SkinnedMeshBoundsCalculator.Result gpuResult))
                {
                    _lastSkinnedBoundsRefreshTicks = Engine.ElapsedTicks;
                    return ApplySkinnedBoundsResult(gpuResult, markBvhDirty: true);
                }

                // Fall back to CPU path for debugging or when GPU fails
                _lastSkinnedBoundsRefreshTicks = Engine.ElapsedTicks;
                return TryComputeSkinnedBoundsOnCpuLocked();
            }
        }

        private bool TryComputeSkinnedBoundsOnGpu(out SkinnedMeshBoundsCalculator.Result result)
            => SkinnedMeshBoundsCalculator.Instance.TryCompute(this, out result);

        private bool ApplySkinnedBoundsResult(SkinnedMeshBoundsCalculator.Result result, bool markBvhDirty)
        {
            var positions = result.Positions ?? [];
            _skinnedVertexPositions = positions;
            _skinnedVertexCount = positions.Length;
            if (_skinnedVertexCount == 0)
            {
                _hasSkinnedBounds = false;
                _skinnedBoundsAreWorldSpace = false;
                return false;
            }

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

        private bool TryComputeSkinnedBoundsOnCpuLocked()
        {
            var mesh = CurrentLODRenderer?.Mesh;
            var vertices = mesh?.Vertices;
            if (mesh is null || vertices is null || vertices.Length == 0)
            {
                _hasSkinnedBounds = false;
                return false;
            }

            _currentSkinMatrices.Clear();
            foreach (var (bone, invBind) in mesh.UtilizedBones)
            {
                if (bone is null)
                    continue;
                _currentSkinMatrices[bone] = invBind * bone.RenderMatrix;
            }

            if (_skinnedVertexPositions is null || _skinnedVertexPositions.Length != vertices.Length)
                _skinnedVertexPositions = new Vector3[vertices.Length];
            _skinnedVertexCount = vertices.Length;

            bool initialized = false;
            Vector3 min = Vector3.Zero;
            Vector3 max = Vector3.Zero;
            Matrix4x4 fallbackMatrix = Component.Transform.RenderMatrix;
            Matrix4x4 basis = GetSkinnedBasisMatrix();
            Matrix4x4 invBasis = Matrix4x4.Invert(basis, out var basisInv) ? basisInv : Matrix4x4.Identity;

            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 worldPos = ComputeSkinnedPosition(vertices[i], fallbackMatrix);
                Vector3 localPos = TransformPosition(worldPos, invBasis);
                _skinnedVertexPositions[i] = localPos;

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
                _hasSkinnedBounds = false;
                return false;
            }

            var localBounds = new AABB(min, max);
            var localizedResult = new SkinnedMeshBoundsCalculator.Result((Vector3[])_skinnedVertexPositions.Clone(), localBounds, basis);
            return ApplySkinnedBoundsResult(localizedResult, markBvhDirty: true);
        }

        private Vector3 ComputeSkinnedPosition(Vertex vertex, Matrix4x4 fallbackMatrix)
        {
            if (vertex.Weights is not { Count: > 0 })
                return TransformPosition(vertex.Position, fallbackMatrix);

            Vector3 result = Vector3.Zero;
            foreach (var (bone, data) in vertex.Weights)
            {
                if (!_currentSkinMatrices.TryGetValue(bone, out Matrix4x4 boneMatrix))
                    boneMatrix = data.bindInvWorldMatrix * bone.RenderMatrix;
                result += TransformPosition(vertex.Position, boneMatrix) * data.weight;
            }
            return result;
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

                if (_skinnedBoundsDirty && !EnsureSkinnedBounds())
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

            if (Engine.Rendering.Settings.CalculateSkinnedBoundsInComputeShader)
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
            if (LODs.Count == 0)
                return;

            if (CurrentLOD is null)
            {
                CurrentLOD = LODs.First;
                return;
            }

            while (CurrentLOD.Next is not null && distanceToCamera > CurrentLOD.Value.MaxVisibleDistance)
                CurrentLOD = CurrentLOD.Next;

            if (CurrentLOD.Previous is not null && distanceToCamera < CurrentLOD.Previous.Value.MaxVisibleDistance)
                CurrentLOD = CurrentLOD.Previous;
        }

        public void Dispose()
        {
            UntrackAllBones();
            foreach (var lod in LODs)
                lod.Renderer.Destroy();
            LODs.Clear();
            GC.SuppressFinalize(this);
        }

        [RequiresDynamicCode("")]
        public float? Intersect(Segment localSpaceSegment, out Triangle? triangle)
        {
            triangle = null;
            return CurrentLOD?.Value?.Renderer?.Mesh?.Intersect(localSpaceSegment, out triangle);
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
                        bool skinned = (rend?.Mesh?.HasSkinning ?? false) && Engine.Rendering.Settings.AllowSkinning;
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
            if (Engine.IsRenderThread)
            {
                ApplyImmediateRenderMatrixUpdate(componentMatrix: null, rootMatrix: renderMatrix);
                return;
            }

            MarkPendingRootBoneRenderMatrix(renderMatrix);
        }

        private void RootBone_WorldMatrixPreviewChanged(TransformBase rootBone, Matrix4x4 worldMatrix)
        {
            bool hasSkinning = (CurrentLOD?.Value?.Renderer?.Mesh?.HasSkinning ?? false) && Engine.Rendering.Settings.AllowSkinning;
            if (!hasSkinning)
                return;

            SetSkinnedRootRenderMatrix(worldMatrix);
            if (RenderInfo is not null)
                RenderInfo.CullingOffsetMatrix = worldMatrix;
        }

        /// <summary>
        /// Updates the culling offset matrix for non-skinned meshes when the component moves.
        /// </summary>
        private void Component_WorldMatrixChanged(TransformBase component, Matrix4x4 renderMatrix)
        {
            if (Engine.IsRenderThread)
            {
                ApplyImmediateRenderMatrixUpdate(componentMatrix: renderMatrix, rootMatrix: null);
                return;
            }

            MarkPendingComponentRenderMatrix(renderMatrix);
        }

        private void Component_WorldMatrixPreviewChanged(TransformBase component, Matrix4x4 worldMatrix)
        {
            bool hasSkinning = (CurrentLOD?.Value?.Renderer?.Mesh?.HasSkinning ?? false) && Engine.Rendering.Settings.AllowSkinning;
            if (hasSkinning)
            {
                if (RootBone is not null)
                    return;

                SetSkinnedRootRenderMatrix(worldMatrix);
            }

            if (RenderInfo is not null)
                RenderInfo.CullingOffsetMatrix = worldMatrix;
        }

        private void ApplyImmediateRenderMatrixUpdate(Matrix4x4? componentMatrix, Matrix4x4? rootMatrix)
        {
            bool hasSkinning = (CurrentLOD?.Value?.Renderer?.Mesh?.HasSkinning ?? false) && Engine.Rendering.Settings.AllowSkinning;
            if (hasSkinning)
            {
                Matrix4x4 basis = RootBone is null
                    ? componentMatrix ?? Component.Transform.RenderMatrix
                    : rootMatrix ?? RootBone.RenderMatrix;

                SetSkinnedRootRenderMatrix(basis);
                if (RenderInfo is not null)
                    RenderInfo.CullingOffsetMatrix = basis;

                return;
            }

            Matrix4x4 matrix = componentMatrix ?? Component.Transform.RenderMatrix;
            _rc?.ApplyLateRenderThreadWorldMatrix(matrix);

            if (RenderInfo is not null)
                RenderInfo.CullingOffsetMatrix = matrix;
        }

        private void MarkPendingComponentRenderMatrix(Matrix4x4 renderMatrix)
        {
            lock (_pendingRenderMatrixLock)
            {
                _pendingComponentRenderMatrix = renderMatrix;
                _pendingComponentRenderMatrixVersion++;
            }

            if (Interlocked.Exchange(ref _pendingRenderMatrixQueued, 1) == 0)
                _pendingRenderMatrixUpdates.Enqueue(this);
        }

        private void MarkPendingRootBoneRenderMatrix(Matrix4x4 renderMatrix)
        {
            lock (_pendingRenderMatrixLock)
            {
                _pendingRootBoneRenderMatrix = renderMatrix;
                _pendingRootBoneRenderMatrixVersion++;
            }

            if (Interlocked.Exchange(ref _pendingRenderMatrixQueued, 1) == 0)
                _pendingRenderMatrixUpdates.Enqueue(this);
        }

        private void ApplyPendingRenderMatrixUpdates()
        {
            int componentVersion;
            int rootBoneVersion;
            Matrix4x4 componentMatrix;
            Matrix4x4 rootMatrix;

            lock (_pendingRenderMatrixLock)
            {
                componentVersion = _pendingComponentRenderMatrixVersion;
                rootBoneVersion = _pendingRootBoneRenderMatrixVersion;
                componentMatrix = _pendingComponentRenderMatrix;
                rootMatrix = _pendingRootBoneRenderMatrix;
            }

            bool hasSkinning = (CurrentLOD?.Value?.Renderer?.Mesh?.HasSkinning ?? false) && Engine.Rendering.Settings.AllowSkinning;
            if (hasSkinning)
            {
                Matrix4x4 basis = RootBone is null ? componentMatrix : rootMatrix;
                SetSkinnedRootRenderMatrix(basis);
                if (RenderInfo is not null)
                    RenderInfo.CullingOffsetMatrix = basis;
            }
            else
            {
                if (_rc is not null)
                    _rc.WorldMatrix = componentMatrix;

                if (RenderInfo is not null)
                    RenderInfo.CullingOffsetMatrix = componentMatrix;
            }

            Interlocked.Exchange(ref _pendingRenderMatrixQueued, 0);

            lock (_pendingRenderMatrixLock)
            {
                if (_pendingComponentRenderMatrixVersion != componentVersion ||
                    _pendingRootBoneRenderMatrixVersion != rootBoneVersion)
                {
                    if (Interlocked.Exchange(ref _pendingRenderMatrixQueued, 1) == 0)
                        _pendingRenderMatrixUpdates.Enqueue(this);
                }
            }
        }

        internal static void ProcessPendingRenderMatrixUpdates()
        {
            while (_pendingRenderMatrixUpdates.TryDequeue(out var mesh))
                mesh.ApplyPendingRenderMatrixUpdates();
        }
    }
}
