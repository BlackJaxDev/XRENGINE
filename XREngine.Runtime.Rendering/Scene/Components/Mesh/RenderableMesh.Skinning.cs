using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using SimpleScene.Util.ssBVH;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Data.Transforms;
using XREngine.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models;
using XREngine.Scene.Transforms;
using XREngine.Timers;

namespace XREngine.Components.Scene.Mesh
{
    public partial class RenderableMesh
    {
        #region Skinned bounds and BVH state

        private readonly record struct SkinnedBoundsCpuSnapshot(
            Vertex[] Vertices,
            Dictionary<TransformBase, Matrix4x4> SkinMatrices,
            IReadOnlyDictionary<TransformBase, TransformBase>? BoneReferenceRemap,
            Matrix4x4 FallbackMatrix,
            Matrix4x4 Basis);

        internal readonly record struct SkinnedBoneCullingVolume(
            TransformBase Transform,
            AABB LocalBounds);

        private struct SkinnedBoneBoundsBuilder
        {
            public bool Initialized;
            public Vector3 Min;
            public Vector3 Max;

            public void Include(Vector3 point)
            {
                if (!Initialized)
                {
                    Min = point;
                    Max = point;
                    Initialized = true;
                    return;
                }

                Min = Vector3.Min(Min, point);
                Max = Vector3.Max(Max, point);
            }

            public readonly AABB ToBounds()
                => new(Min, Max);
        }

        private readonly record struct SkinnedBoundsRefreshResult(
            int Revision,
            SkinnedMeshBoundsCalculator.Result Result,
            bool Succeeded,
            long QueueWaitTicks,
            long CpuJobTicks);

        private readonly Dictionary<TransformBase, int> _trackedSkinnedBones = new();
        private readonly Dictionary<TransformBase, Matrix4x4> _relativeBoneMatrices = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        private readonly object _relativeCacheLock = new();
        private readonly object _skinnedDataLock = new();
        private readonly TransformBase? _skinnedBoundsRootTransform;
        private SkinnedBoneCullingVolume[] _skinnedBoneCullingVolumes = [];
        private XRMesh? _skinnedBoneCullingSourceMesh;
        private bool _skinnedBoneCullingVolumesDirty = true;

        /// <summary>
        /// Tracks whether this skinned renderable was collected in the previous diagnostic generation.
        /// Used only by <see cref="RenderDiagnosticsFlags.SkinCullRejectDiag"/> to detect the
        /// collected-then-dropped transition that produces the visible flicker.
        /// </summary>
        internal bool DiagWasCollectedLastEval;
        private bool _skinnedBoundsDirty = true;
        private bool _hasSkinnedBounds;
        private bool _skinnedBoundsAreWorldSpace;
        private AABB _skinnedLocalBounds;
        private Vector3[]? _skinnedVertexPositions;
        private int _skinnedVertexCount;
        private readonly List<uint> _pathAScratchIndices = new(8);
        private Task<SkinnedBoundsRefreshResult>? _skinnedBoundsRefreshTask;
        private int _skinnedBoundsRevision;
        private BVH<Triangle>? _skinnedBvh;
        private Task<SkinnedMeshBvhScheduler.Result>? _skinnedBvhTask;
        private int _skinnedBvhVersion = 0;
        private bool _skinnedBvhScheduledOnce;
        private bool _usesAuthoredSkinnedCullingBounds;
        private AABB _bindPoseBounds;
        private Matrix4x4 _skinnedRootRenderMatrix = Matrix4x4.Identity;
        private Matrix4x4 _skinnedRootRenderMatrixInverse = Matrix4x4.Identity;
        private bool _lastRenderSkinningEnabled;
        private bool _lastRenderComputeSkinningEnabled;
        private bool _lastRenderComputeSkinnedBoundsEnabled;
        private bool _lastRenderComputeBlendshapesEnabled;
        private bool _lastRenderBlendshapesEnabled;

        // Scene load can seed the draw matrix before every transform has published a fresh render matrix.
        // The first collect re-reads the current transform state and mirrors the skinning-toggle path.
        private bool _initialRenderStateSeeded;

        // Vertex skinning shares the compute path's bone palette but not its pose-settle loop.
        // Keep reseeding until startup poses stop changing so imported rigs do not latch half-built poses.
        private bool _vertexSkinSeedSettled;

        /// <summary>
        /// Minimum interval, in seconds, between expensive skinned local-AABB recomputations.
        /// The root-bone matrix still updates every frame, so world placement remains current.
        /// </summary>
        private const float SkinnedBoundsRefreshInterval = 5.0f;
        private static readonly long SkinnedBoundsRefreshIntervalTicks = RuntimeTiming.SecondsToStopwatchTicks(SkinnedBoundsRefreshInterval);
        private long _lastSkinnedBoundsRefreshTicks = long.MinValue;

        #endregion

        #region Render deformation settings

        private void CaptureRenderDeformationSettings(bool isSkinned)
        {
            var settings = RuntimeEngine.Rendering.Settings;
            _lastRenderSkinningEnabled = isSkinned;
            _lastRenderComputeSkinningEnabled = settings.CalculateSkinningInComputeShader;
            _lastRenderComputeSkinnedBoundsEnabled = settings.CalculateSkinnedBoundsInComputeShader;
            _lastRenderComputeBlendshapesEnabled = settings.CalculateBlendshapesInComputeShader;
            _lastRenderBlendshapesEnabled = settings.AllowBlendshapes;
        }

        private bool RenderDeformationSettingsChanged(bool isSkinned)
        {
            var settings = RuntimeEngine.Rendering.Settings;
            return isSkinned != _lastRenderSkinningEnabled ||
                settings.CalculateSkinningInComputeShader != _lastRenderComputeSkinningEnabled ||
                settings.CalculateSkinnedBoundsInComputeShader != _lastRenderComputeSkinnedBoundsEnabled ||
                settings.CalculateBlendshapesInComputeShader != _lastRenderComputeBlendshapesEnabled ||
                settings.AllowBlendshapes != _lastRenderBlendshapesEnabled;
        }

        private void InvalidateGpuDeformationState()
        {
            foreach (RenderableLOD lod in GetLodSnapshot())
            {
                XRMeshRenderer renderer = lod.Renderer;
                SkinningPrepassDispatcher.Instance.InvalidateRenderer(renderer);
                renderer.ResetSkinPaletteSeedState();
                renderer.MarkSkinnedOutputDirty();
            }

            SkinnedMeshBoundsCalculator.Instance.UnregisterSkinnedMesh(this, World?.VisualScene?.GPUCommands);
            DisposeGpuSkinnedBoundsDebugRenderer();
            _vertexSkinSeedSettled = false;
            _initialRenderStateSeeded = false;
        }

        #endregion

        #region Skinned bounds policy

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

        #endregion

        #region Bone tracking and transform helpers

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

        internal static TransformBase? ResolveSkinnedRootBoneTransform(
            TransformBase? serializedRootBone,
            TransformBase? inferredRootBone,
            TransformBase? referenceSearchRoot)
        {
            if (serializedRootBone is null)
                return inferredRootBone;

            if (referenceSearchRoot is null)
                return serializedRootBone;

            if (IsSelfOrDescendantOf(referenceSearchRoot, serializedRootBone))
                return serializedRootBone;

            if (inferredRootBone is not null && IsSelfOrDescendantOf(referenceSearchRoot, inferredRootBone))
                return inferredRootBone;

            return serializedRootBone;
        }

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

        #endregion

        #region Per-bone culling bounds

        private bool SkinnedBoneCullingIntersectionOverride(RenderInfo3D info, IVolume? cullingVolume, bool containsOnly)
        {
            if (cullingVolume is null)
                return true;

            if (!IsSkinned)
                return DefaultRenderInfoCullingIntersection(info, cullingVolume, containsOnly);

            SkinnedBoneCullingVolume[] volumes;
            lock (_skinnedDataLock)
            {
                if (!TryEnsureSkinnedBoneCullingVolumesLocked(out volumes))
                    return DefaultRenderInfoCullingIntersection(info, cullingVolume, containsOnly);
            }

            return IntersectsSkinnedBoneCullingVolumes(volumes, cullingVolume, containsOnly);
        }

        /// <summary>
        /// Builds the <c>[SkinCullReject]</c> diagnostic line for this skinned renderable. Called by
        /// the CPU collect path when this mesh was collected last generation but dropped this one.
        /// <paramref name="stage"/> is the attributed rejecting stage
        /// (<c>bvh-node</c>/<c>bone-override</c>/<c>downstream</c>).
        /// </summary>
        internal string BuildSkinCullRejectPayload(string stage, long gen)
        {
            XRMesh? lodMesh = CurrentLODRenderer?.Mesh;
            string meshName = lodMesh?.Name ?? "<null>";
            bool hasSkinning = lodMesh?.HasSkinning ?? false;

            int boneVolumeCount = 0;
            bool aggOk = false;
            AABB agg = default;
            lock (_skinnedDataLock)
            {
                if (TryEnsureSkinnedBoneCullingVolumesLocked(out SkinnedBoneCullingVolume[] volumes))
                {
                    boneVolumeCount = volumes.Length;
                    aggOk = TryComputeSkinnedBoneAggregateWorldBounds(volumes, out agg);
                }
            }

            Box? worldBox = ((IOctreeItem)RenderInfo).WorldCullingVolume;
            bool overrideInstalled = RenderInfo.CullingIntersectionOverride is not null;
            string aggStr = aggOk
                ? $"min=({agg.Min.X:F2},{agg.Min.Y:F2},{agg.Min.Z:F2}) max=({agg.Max.X:F2},{agg.Max.Y:F2},{agg.Max.Z:F2})"
                : "min=<n/a> max=<n/a>";
            string boxStr = worldBox.HasValue
                ? $"center=({worldBox.Value.WorldCenter.X:F2},{worldBox.Value.WorldCenter.Y:F2},{worldBox.Value.WorldCenter.Z:F2}) half=({worldBox.Value.LocalHalfExtents.X:F2},{worldBox.Value.LocalHalfExtents.Y:F2},{worldBox.Value.LocalHalfExtents.Z:F2})"
                : "<null>";

            return $"[SkinCullReject] gen={gen} stage={stage} mesh='{meshName}' rootBone='{RootBone?.Name ?? "<null>"}' " +
                $"hasSkinning={hasSkinning} overrideInstalled={overrideInstalled} forceUnbounded={RenderDiagnosticsFlags.ForceSkinnedUnbounded} " +
                $"boneVolumeCount={boneVolumeCount} aggregate[{aggStr}] worldBox[{boxStr}]";
        }

        private static bool DefaultRenderInfoCullingIntersection(RenderInfo3D info, IVolume? cullingVolume, bool containsOnly)
        {
            Box? worldCullingVolume = ((IOctreeItem)info).WorldCullingVolume;
            if (worldCullingVolume is null)
                return true;

            EContainment containment = cullingVolume?.ContainsBox(worldCullingVolume.Value) ?? EContainment.Contains;
            return containsOnly ? containment == EContainment.Contains : containment != EContainment.Disjoint;
        }

        internal static bool IntersectsSkinnedBoneCullingVolumes(
            ReadOnlySpan<SkinnedBoneCullingVolume> volumes,
            IVolume? cullingVolume,
            bool containsOnly)
        {
            if (cullingVolume is null)
                return true;

            for (int i = 0; i < volumes.Length; i++)
            {
                if (!TryGetSkinnedBoneWorldBounds(volumes[i], out AABB worldBounds))
                    continue;

                EContainment containment = ClassifySkinnedCullingBounds(cullingVolume, worldBounds);
                if (containsOnly ? containment == EContainment.Contains : containment != EContainment.Disjoint)
                    return true;
            }

            return false;
        }

        private static EContainment ClassifySkinnedCullingBounds(IVolume cullingVolume, AABB worldBounds)
        {
            EContainment containment = cullingVolume.ContainsAABB(worldBounds);
            if (containment != EContainment.Disjoint)
                return containment;

            if (cullingVolume is AABB aabb && aabb.Intersects(worldBounds))
                return EContainment.Intersects;

            if (cullingVolume is Frustum frustum && frustum.Intersects(worldBounds))
                return EContainment.Intersects;

            return EContainment.Disjoint;
        }

        private bool TryApplySkinnedBoneCullingBounds()
        {
            if (!IsSkinned)
                return false;

            lock (_skinnedDataLock)
            {
                if (!TryEnsureSkinnedBoneCullingVolumesLocked(out SkinnedBoneCullingVolume[] volumes) ||
                    !TryComputeSkinnedBoneAggregateWorldBounds(volumes, out AABB aggregateWorldBounds))
                {
                    return false;
                }

                ApplySkinnedBoneAggregateWorldBounds(aggregateWorldBounds);

                return true;
            }
        }

        // Throttle so an anomalous frame logs at most a few lines per mesh per second.
        private long _lastAggregateAnomalyLogTicks;

        /// <summary>
        /// Flag-gated (<see cref="RenderDiagnosticsFlags.SkinCullRejectDiag"/>) diagnostic that proves
        /// when the FINAL published <see cref="RenderInfo3D.WorldCullingVolume"/> is displaced or oversized
        /// relative to where the skinned geometry actually is. The live per-bone aggregate is recomputed
        /// as ground truth; if the published box center diverges from it (the alternate-frame "teleport to
        /// origin" that makes the BVH node a tower) or the box is oversized, the offending branch is logged.
        /// Catches the aggregate, GPU/EnsureSkinnedBounds, and bind-pose fallback paths. Mutates nothing.
        /// </summary>
        private void LogSkinnedPublishedBoundsAnomaly(string branch)
        {
            // A whole avatar is ~2m tall; a single submesh broad-phase box larger than this on any
            // axis is a tower. A published center more than this far from the live bone aggregate is
            // a displacement (the box teleported away from the geometry).
            const float TowerHalfExtentMeters = 1.5f;
            const float DisplacementMeters = 0.5f;

            Box? worldBox = ((IOctreeItem)RenderInfo).WorldCullingVolume;
            if (worldBox is not Box box)
                return;

            Vector3 half = box.LocalHalfExtents;
            float maxHalf = MathF.Max(half.X, MathF.Max(half.Y, half.Z));
            bool oversized = maxHalf >= TowerHalfExtentMeters;

            // Ground truth: where the bones actually place the geometry this frame.
            bool aggOk = TryGetSkinnedBoneAggregateWorldBounds(out AABB agg);
            Vector3 publishedCenter = box.WorldCenter;
            float displacement = 0.0f;
            bool displaced = false;
            if (aggOk)
            {
                Vector3 aggCenter = (agg.Min + agg.Max) * 0.5f;
                displacement = Vector3.Distance(publishedCenter, aggCenter);
                displaced = displacement >= DisplacementMeters;
            }

            if (!oversized && !displaced)
                return;

            long nowTicks = Stopwatch.GetTimestamp();
            if (nowTicks - _lastAggregateAnomalyLogTicks < Stopwatch.Frequency / 4)
                return;
            _lastAggregateAnomalyLogTicks = nowTicks;

            string meshName = CurrentLODRenderer?.Mesh?.Name ?? "<null>";
            AABB? localVolume = RenderInfo.LocalCullingVolume;
            Matrix4x4 offset = RenderInfo.CullingOffsetMatrix;
            string localStr = localVolume is AABB lv
                ? $"min=({lv.Min.X:F2},{lv.Min.Y:F2},{lv.Min.Z:F2}) max=({lv.Max.X:F2},{lv.Max.Y:F2},{lv.Max.Z:F2})"
                : "<null>";
            string aggStr = aggOk
                ? $"center=({(agg.Min.X + agg.Max.X) * 0.5f:F2},{(agg.Min.Y + agg.Max.Y) * 0.5f:F2},{(agg.Min.Z + agg.Max.Z) * 0.5f:F2})"
                : "<n/a>";

            RuntimeEngine.LogWarning(
                $"[SkinCullAnomaly] branch={branch} reason={(oversized ? "oversized" : "")}{(displaced ? "displaced" : "")} " +
                $"mesh='{meshName}' rootBone='{RootBone?.Name ?? "<null>"}' " +
                $"publishedCenter=({publishedCenter.X:F2},{publishedCenter.Y:F2},{publishedCenter.Z:F2}) " +
                $"publishedHalf=({half.X:F2},{half.Y:F2},{half.Z:F2}) boneAggregate[{aggStr}] displacement={displacement:F2} " +
                $"localCullingVolume[{localStr}] offsetT=({offset.Translation.X:F2},{offset.Translation.Y:F2},{offset.Translation.Z:F2}) " +
                $"offsetIdentity={offset.Equals(Matrix4x4.Identity)}");
        }

        internal bool RefreshSkinnedCullingBoundsForSceneCulling()
        {
            if (!IsSkinned)
                return false;

            bool hasSkinning = (CurrentLODRenderer?.Mesh?.HasSkinning ?? false) &&
                RuntimeEngine.Rendering.Settings.AllowSkinning;
            if (!hasSkinning)
                return false;

            if (RenderDiagnosticsFlags.ForceSkinnedUnbounded)
            {
                RenderInfo.LocalCullingVolume = null;
                RenderInfo.CullingOffsetMatrix = Matrix4x4.Identity;
                return true;
            }

            if (RuntimeEngine.Rendering.Settings.CalculateSkinnedBoundsInComputeShader && RuntimeEngine.IsRenderThread)
                ProcessSkinnedBoundsRefresh();

            bool aggregateOk = TryApplySkinnedBoneCullingBounds();
            bool ensureOk = !aggregateOk && EnsureSkinnedBounds();
            bool skinnedBoundsOk = aggregateOk || ensureOk;
            if (!skinnedBoundsOk)
                PublishSkinnedWorldCullingBounds(_bindPoseBounds, GetSkinnedBasisMatrix(), boundsAreWorldSpace: false);

            if (RenderDiagnosticsFlags.SkinCullRejectDiag)
            {
                string branch = aggregateOk ? "aggregate" : ensureOk ? "ensure-skinned" : "bind-pose-fallback";
                LogSkinnedPublishedBoundsAnomaly(branch);
            }

            return skinnedBoundsOk;
        }

        private void ApplySkinnedBoneAggregateWorldBounds(in AABB aggregateWorldBounds)
        {
            if (RenderInfo is null)
                return;

            if (RenderInfo.LocalCullingVolume is not AABB currentBounds || !AabbNearlyEqual(currentBounds, aggregateWorldBounds))
                RenderInfo.LocalCullingVolume = aggregateWorldBounds;

            if (RenderInfo.CullingOffsetMatrix != Matrix4x4.Identity)
                RenderInfo.CullingOffsetMatrix = Matrix4x4.Identity;
        }

        /// <summary>
        /// Publishes skinned culling bounds to <see cref="RenderInfo"/> using a SINGLE consistent convention:
        /// a world-space <see cref="RenderInfo3D.LocalCullingVolume"/> paired with an IDENTITY
        /// <see cref="RenderInfo3D.CullingOffsetMatrix"/>. The aggregate path already publishes world-space +
        /// identity; the GPU/ensure/bind-pose paths historically published root-bone-local bounds plus a
        /// non-identity root render-matrix offset. Lock-free readers (CPU BVH rebuild, octree moves,
        /// diagnostics) evaluate <c>WorldCullingVolume = LocalCullingVolume.ToBox(CullingOffsetMatrix)</c> by
        /// reading the two fields separately, so a frame that switched conventions could pair a world-space
        /// local volume with the stale root-matrix offset and double-transform the box into the "tower" that
        /// culls the mesh out (visible as flicker). Baking the basis into the published world AABB and forcing
        /// the offset to Identity makes the pair torn-read safe: any read yields a valid (possibly one frame
        /// stale) world box, never a double transform. <see cref="_skinnedRootRenderMatrix"/> is still
        /// maintained for the GPU BVH (<see cref="SkinnedBvhLocalToWorldMatrix"/>).
        /// </summary>
        private void PublishSkinnedWorldCullingBounds(in AABB bounds, in Matrix4x4 basis, bool boundsAreWorldSpace)
        {
            if (RenderInfo is null)
                return;

            AABB worldBounds = boundsAreWorldSpace ? bounds : TransformBounds(bounds, basis);

            // Force the offset to Identity FIRST so any octree move queued from the LocalCullingVolume
            // change already observes the identity offset (never world-local x stale-matrix).
            if (RenderInfo.CullingOffsetMatrix != Matrix4x4.Identity)
                RenderInfo.CullingOffsetMatrix = Matrix4x4.Identity;
            RenderInfo.LocalCullingVolume = worldBounds;
        }

        private static bool AabbNearlyEqual(in AABB a, in AABB b, float epsilon = 1e-4f)
            => MathF.Abs(a.Min.X - b.Min.X) <= epsilon &&
               MathF.Abs(a.Min.Y - b.Min.Y) <= epsilon &&
               MathF.Abs(a.Min.Z - b.Min.Z) <= epsilon &&
               MathF.Abs(a.Max.X - b.Max.X) <= epsilon &&
               MathF.Abs(a.Max.Y - b.Max.Y) <= epsilon &&
               MathF.Abs(a.Max.Z - b.Max.Z) <= epsilon;

        private bool TryGetSkinnedBoneAggregateWorldBounds(out AABB aggregateWorldBounds)
        {
            aggregateWorldBounds = default;
            if (!IsSkinned)
                return false;

            lock (_skinnedDataLock)
            {
                return TryEnsureSkinnedBoneCullingVolumesLocked(out SkinnedBoneCullingVolume[] volumes) &&
                    TryComputeSkinnedBoneAggregateWorldBounds(volumes, out aggregateWorldBounds);
            }
        }

        private bool TryGetSkinnedBoneCullingVolumesSnapshot(out SkinnedBoneCullingVolume[] volumes)
        {
            volumes = [];
            if (!IsSkinned)
                return false;

            lock (_skinnedDataLock)
                return TryEnsureSkinnedBoneCullingVolumesLocked(out volumes);
        }

        private bool TryEnsureSkinnedBoneCullingVolumesLocked(out SkinnedBoneCullingVolume[] volumes)
        {
            XRMesh? mesh = CurrentLODRenderer?.Mesh;
            if (mesh is null || !mesh.HasSkinning)
            {
                volumes = [];
                return false;
            }

            if (!_skinnedBoneCullingVolumesDirty && ReferenceEquals(_skinnedBoneCullingSourceMesh, mesh))
            {
                volumes = _skinnedBoneCullingVolumes;
                return volumes.Length > 0;
            }

            _skinnedBoneCullingVolumes = BuildSkinnedBoneCullingVolumes(mesh, Component.Transform);
            _skinnedBoneCullingSourceMesh = mesh;
            _skinnedBoneCullingVolumesDirty = false;
            volumes = _skinnedBoneCullingVolumes;
            return volumes.Length > 0;
        }

        internal static SkinnedBoneCullingVolume[] BuildSkinnedBoneCullingVolumes(XRMesh mesh, TransformBase fallbackTransform)
        {
            Vertex[]? vertices = mesh.Vertices;
            if (vertices is not { Length: > 0 })
                return [];

            Dictionary<TransformBase, SkinnedBoneBoundsBuilder> builders =
                new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
            IReadOnlyDictionary<TransformBase, TransformBase>? boneReferenceRemap = mesh.RuntimeBoneReferenceRemap;

            for (int vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
            {
                Vertex vertex = vertices[vertexIndex];
                bool includedWeightedBone = false;

                if (vertex.Weights is { Count: > 0 } weights)
                {
                    foreach ((TransformBase bone, (float weight, Matrix4x4 bindInvWorldMatrix) data) in weights)
                    {
                        if (!(data.weight > 0.0f))
                            continue;

                        TransformBase resolvedBone = ResolveRuntimeBoneReference(bone, boneReferenceRemap);
                        Vector3 localPosition = TransformPosition(vertex.Position, data.bindInvWorldMatrix);
                        IncludeSkinnedBoneCullingPoint(builders, resolvedBone, localPosition);
                        includedWeightedBone = true;
                    }
                }

                if (!includedWeightedBone)
                    IncludeSkinnedBoneCullingPoint(builders, fallbackTransform, vertex.Position);
            }

            if (builders.Count == 0)
                return [];

            SkinnedBoneCullingVolume[] volumes = new SkinnedBoneCullingVolume[builders.Count];
            int volumeIndex = 0;
            foreach (KeyValuePair<TransformBase, SkinnedBoneBoundsBuilder> pair in builders)
            {
                SkinnedBoneBoundsBuilder builder = pair.Value;
                if (!builder.Initialized)
                    continue;

                volumes[volumeIndex++] = new SkinnedBoneCullingVolume(pair.Key, builder.ToBounds());
            }

            if (volumeIndex == volumes.Length)
                return volumes;

            Array.Resize(ref volumes, volumeIndex);
            return volumes;
        }

        private static void IncludeSkinnedBoneCullingPoint(
            Dictionary<TransformBase, SkinnedBoneBoundsBuilder> builders,
            TransformBase transform,
            Vector3 localPosition)
        {
            builders.TryGetValue(transform, out SkinnedBoneBoundsBuilder builder);
            builder.Include(localPosition);
            builders[transform] = builder;
        }

        private static TransformBase ResolveRuntimeBoneReference(
            TransformBase bone,
            IReadOnlyDictionary<TransformBase, TransformBase>? boneReferenceRemap)
            => boneReferenceRemap is not null && boneReferenceRemap.TryGetValue(bone, out TransformBase? reboundBone)
                ? reboundBone
                : bone;

        private static bool TryComputeSkinnedBoneAggregateWorldBounds(
            ReadOnlySpan<SkinnedBoneCullingVolume> volumes,
            out AABB aggregateWorldBounds)
        {
            aggregateWorldBounds = default;
            bool initialized = false;

            for (int i = 0; i < volumes.Length; i++)
            {
                if (!TryGetSkinnedBoneWorldBounds(volumes[i], out AABB worldBounds))
                    continue;

                if (!initialized)
                {
                    aggregateWorldBounds = worldBounds;
                    initialized = true;
                }
                else
                {
                    aggregateWorldBounds.ExpandToInclude(worldBounds);
                }
            }

            return initialized;
        }

        private static bool TryGetSkinnedBoneWorldBounds(SkinnedBoneCullingVolume volume, out AABB worldBounds)
        {
            Matrix4x4 matrix = GetCurrentTransformMatrix(volume.Transform);
            worldBounds = TransformBounds(volume.LocalBounds, matrix);
            return worldBounds.IsValid;
        }

        private bool HasAnySkinnedLod()
        {
            lock (_lodsLock)
            {
                for (LinkedListNode<RenderableLOD>? node = LODs.First; node is not null; node = node.Next)
                {
                    if (node.Value.Renderer.Mesh?.HasSkinning == true)
                        return true;
                }
            }

            return false;
        }

        private void MarkSkinnedBoneCullingVolumesDirty()
        {
            lock (_skinnedDataLock)
            {
                _skinnedBoneCullingVolumesDirty = true;
                _skinnedBoneCullingSourceMesh = null;
                _skinnedBoneCullingVolumes = [];
            }
        }

        private void RefreshSkinnedCullingIntersectionOverride()
        {
            // The scene tree uses the aggregate as a broad proxy; final visibility for
            // skinned meshes is "any transformed bone box intersects the collection volume."
            RenderInfo.CullingIntersectionOverride = HasAnySkinnedLod()
                ? SkinnedBoneCullingIntersectionOverride
                : null;
        }

        #endregion

        #region Skinned bounds refresh

        private bool EnsureSkinnedBounds()
        {
            if (!IsSkinned)
                return false;

            if (ShouldUseAuthoredSkinnedCullingBounds())
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
                    PublishSkinnedWorldCullingBounds(_skinnedLocalBounds, _skinnedRootRenderMatrix, _skinnedBoundsAreWorldSpace);
                    return true;
                }

                return false;
            }
        }

        private void ApplyCachedSkinnedBoundsLocked()
        {
            Matrix4x4 basis = GetSkinnedBasisMatrix();
            SetSkinnedRootRenderMatrix(basis);
            PublishSkinnedWorldCullingBounds(_skinnedLocalBounds, _skinnedRootRenderMatrix, _skinnedBoundsAreWorldSpace);
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
            // Bake the basis (root bone world matrix) into a world-space AABB and publish with an
            // identity offset so lock-free readers can never pair this world volume with a stale matrix.
            PublishSkinnedWorldCullingBounds(_skinnedLocalBounds, _skinnedRootRenderMatrix, _skinnedBoundsAreWorldSpace);
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
            PublishSkinnedWorldCullingBounds(_skinnedLocalBounds, _skinnedRootRenderMatrix, _skinnedBoundsAreWorldSpace);

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
            if (!IsSkinned || ShouldUseAuthoredSkinnedCullingBounds())
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
                bool refreshComputeNow = ShouldRefreshComputeSkinnedBoundsNow(refreshInFlight);
                bool scheduleCpuVisibleBoundsRefresh = ShouldScheduleSkinnedBoundsRefresh(
                    policy,
                    allowInitialBuildWhenNever,
                    _hasSkinnedBounds,
                    _skinnedBoundsDirty,
                    refreshInFlight,
                    nowTicks,
                    _lastSkinnedBoundsRefreshTicks);

                if (refreshComputeNow && CurrentLODRenderer is { } computeRenderer)
                    SkinningPrepassDispatcher.Instance.RunForGpuMeshBvh(computeRenderer);

                if (scheduleCpuVisibleBoundsRefresh)
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

        #endregion

        #region Skinned BVH

        public BVH<Triangle>? GetSkinnedBvh(bool allowRebuild = true)
        {
            if (!IsSkinned)
                return CurrentLODRenderer?.Mesh?.CachedBVHTree;

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

        private bool ShouldRefreshComputeSkinnedBoundsNow(bool refreshInFlight)
        {
            if (!RuntimeEngine.Rendering.Settings.CalculateSkinnedBoundsInComputeShader ||
                !RuntimeEngine.IsRenderThread ||
                refreshInFlight)
            {
                return false;
            }

            var renderer = CurrentLODRenderer;
            var mesh = renderer?.Mesh;
            return renderer is not null &&
                mesh?.HasSkinning == true &&
                mesh.VertexCount > 0 &&
                RuntimeEngine.Rendering.Settings.AllowSkinning;
        }

        private bool ShouldUseAuthoredSkinnedCullingBounds()
            => _usesAuthoredSkinnedCullingBounds &&
               !RuntimeEngine.Rendering.Settings.CalculateSkinnedBoundsInComputeShader;


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

                // A null tree means the build produced no geometry this attempt (e.g. the GPU
                // skinned-position readback was not ready yet). Clear the one-shot latch so a
                // subsequent pick reschedules instead of leaving the mesh permanently
                // unselectable after a single empty build.
                if (_skinnedBvh is null)
                    _skinnedBvhScheduledOnce = false;
                return true;
            }
            catch (Exception ex)
            {
                Debug.RenderingException(ex, "Skinned BVH compute path failed.");
                _skinnedBvh = null;
                _skinnedBvhScheduledOnce = false;
                return true;
            }
            finally
            {
                _skinnedBvhTask = null;
            }
        }

        #endregion

        #region Root-bone inference

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

        #endregion
    }
}
