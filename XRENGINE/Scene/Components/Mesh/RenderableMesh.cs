using System;
using System.Collections.Generic;
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
using SimpleScene.Util.ssBVH;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Scene.Mesh
{
    public class RenderableMesh : XRBase, IDisposable
    {
        public RenderInfo3D RenderInfo { get; }

        private readonly RenderCommandMesh3D _rc;

        private readonly Dictionary<TransformBase, int> _trackedSkinnedBones = new();
        private readonly Dictionary<TransformBase, Matrix4x4> _currentSkinMatrices = new();
        private readonly Dictionary<TransformBase, Matrix4x4> _relativeBoneMatrices = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        private readonly object _relativeCacheLock = new();
        private readonly object _skinnedDataLock = new();
        private bool _skinnedBoundsDirty = true;
        private bool _skinnedBvhDirty = true;
        private bool _hasSkinnedBounds;
        private AABB _skinnedLocalBounds;
        private Vector3[]? _skinnedVertexPositions;
        private int _skinnedVertexCount;
        private BVH<Triangle>? _skinnedBvh;
        private Task<SkinnedMeshBvhScheduler.Result>? _skinnedBvhTask;
        private int _skinnedBvhVersion;
        private AABB _bindPoseBounds;
        private Matrix4x4 _skinnedRootRenderMatrix = Matrix4x4.Identity;
        private Matrix4x4 _skinnedRootRenderMatrixInverse = Matrix4x4.Identity;

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
        public XRWorldInstance? World => Component.SceneNode.World;
        public LinkedList<RenderableLOD> LODs { get; private set; } = new();

        private bool _renderBounds = Engine.Rendering.Settings.RenderMesh3DBounds;
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

        public bool IsSkinned
            => (CurrentLODRenderer?.Mesh?.HasSkinning ?? false) && Engine.Rendering.Settings.AllowSkinning;

        void ComponentPropertyChanged(object? s, IXRPropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RenderableComponent.Transform) && !Component.SceneNode.IsTransformNull)
                Component.Transform.RenderMatrixChanged += Component_WorldMatrixChanged;
        }
        void ComponentPropertyChanging(object? s, IXRPropertyChangingEventArgs e)
        {
            if (e.PropertyName == nameof(RenderableComponent.Transform) && !Component.SceneNode.IsTransformNull)
                Component.Transform.RenderMatrixChanged -= Component_WorldMatrixChanged;
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

            var box = (RenderInfo as IOctreeItem)?.WorldCullingVolume;
            if (box is not null)
                Engine.Rendering.Debug.RenderBox(box.Value.LocalHalfExtents, box.Value.LocalCenter, box.Value.Transform, false, ColorF4.White);

            if (RootBone is not null)
            {
                Vector3 rootTranslation = RootBone.RenderTranslation;
                Engine.Rendering.Debug.RenderPoint(rootTranslation, ColorF4.Red);
                if (RootBone.Name is not null)
                    Engine.Rendering.Debug.RenderText(rootTranslation, RootBone.Name, ColorF4.Black);
            }
        }

        private void SettingUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
        {
            //vertexProgram.Uniform(EEngineUniform.RootInvModelMatrix.ToString(), /*RootTransform?.InverseWorldMatrix ?? */Matrix4x4.Identity);
        }

        private bool BeforeAdd(RenderInfo info, RenderCommandCollection passes, XRCamera? camera)
        {
            var rend = CurrentLODRenderer;
            bool skinned = (rend?.Mesh?.HasSkinning ?? false) && Engine.Rendering.Settings.AllowSkinning;
            TransformBase tfm = skinned ? RootBone ?? Component.Transform : Component.Transform;
            float distance = camera?.DistanceFromNearPlane(tfm.RenderTranslation) ?? 0.0f;

            if (!passes.IsShadowPass)
                UpdateLOD(distance);

            rend = CurrentLODRenderer;
            skinned = (rend?.Mesh?.HasSkinning ?? false) && Engine.Rendering.Settings.AllowSkinning;
            if (skinned)
            {
                if (!EnsureSkinnedBounds())
                {
                    RenderInfo.LocalCullingVolume = _bindPoseBounds;
                    RenderInfo.CullingOffsetMatrix = Component.Transform.RenderMatrix;
                }
            }
            else
            {
                RenderInfo.LocalCullingVolume = _bindPoseBounds;
                RenderInfo.CullingOffsetMatrix = Component.Transform.RenderMatrix;
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

        private Matrix4x4 GetSkinnedBasisMatrix()
        {
            var ownerTransform = Component?.Transform;
            return RootBone?.RenderMatrix ?? ownerTransform?.RenderMatrix ?? Matrix4x4.Identity;
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
                localPositions[i] = Vector3.Transform(worldPositions[i], invBasis);

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

                if (!MatrixNearlyEqual(previous, relative))
                {
                    _relativeBoneMatrices[bone] = relative;
                    return true;
                }
            }

            return false;
        }

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
            _skinnedBvhDirty = true;
            _hasSkinnedBounds = false;
            Interlocked.Increment(ref _skinnedBvhVersion);
        }

        private bool EnsureSkinnedBounds()
        {
            if (!IsSkinned)
                return false;

            bool useGpu;

            lock (_skinnedDataLock)
            {
                if (!_skinnedBoundsDirty && _hasSkinnedBounds)
                    return true;

                useGpu = Engine.Rendering.Settings.CalculateSkinnedBoundsInComputeShader;

                if (!useGpu)
                    return TryComputeSkinnedBoundsOnCpuLocked();
            }

            if (useGpu && TryComputeSkinnedBoundsOnGpu(out SkinnedMeshBoundsCalculator.Result gpuResult))
            {
                lock (_skinnedDataLock)
                {
                    if (!_skinnedBoundsDirty && _hasSkinnedBounds)
                        return true;

                    return ApplySkinnedBoundsResult(gpuResult, markBvhDirty: true);
                }
            }

            lock (_skinnedDataLock)
            {
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
                return false;
            }

            _skinnedLocalBounds = result.Bounds;
            _skinnedBoundsDirty = false;
            _hasSkinnedBounds = true;
            if (markBvhDirty)
                _skinnedBvhDirty = true;

            SetSkinnedRootRenderMatrix(result.Basis);
            if (RenderInfo is not null)
            {
                RenderInfo.LocalCullingVolume = _skinnedLocalBounds;
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
                Vector3 localPos = Vector3.Transform(worldPos, invBasis);
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
                return Vector3.Transform(vertex.Position, fallbackMatrix);

            Vector3 result = Vector3.Zero;
            foreach (var (bone, data) in vertex.Weights)
            {
                if (!_currentSkinMatrices.TryGetValue(bone, out Matrix4x4 boneMatrix))
                    boneMatrix = data.bindInvWorldMatrix * bone.RenderMatrix;
                result += Vector3.Transform(vertex.Position, boneMatrix) * data.weight;
            }
            return result;
        }

        public BVH<Triangle>? GetSkinnedBvh()
        {
            if (!IsSkinned)
                return CurrentLODRenderer?.Mesh?.BVHTree;

            lock (_skinnedDataLock)
            {
                if (_skinnedBoundsDirty && !EnsureSkinnedBounds())
                    return null;

                if (!_skinnedBvhDirty && _skinnedBvh is not null)
                    return _skinnedBvh;

                if (TryFinalizeSkinnedBvhJob(out var readyTree))
                    return readyTree;

                ScheduleSkinnedBvhJobIfNeeded();
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
                _skinnedBvhDirty = false;
                tree = _skinnedBvh;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, "Skinned BVH compute path failed.");
                _skinnedBvh = null;
                _skinnedBvhDirty = false;
                return true;
            }
            finally
            {
                _skinnedBvhTask = null;
            }
        }

        public void UpdateLOD(XRCamera camera)
            => UpdateLOD(camera.DistanceFromNearPlane(Component.Transform.RenderTranslation));
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
                worldBounds = _skinnedLocalBounds.Transformed(p => Vector3.Transform(p, _skinnedRootRenderMatrix));
                return worldBounds.IsValid;
            }

            // Fall back to the bind-pose/local culling bounds.
            AABB localBounds = RenderInfo?.LocalCullingVolume ?? _bindPoseBounds;
            if (!localBounds.IsValid)
                return false;

            Matrix4x4 basis = Component.Transform.RenderMatrix;
            worldBounds = localBounds.Transformed(p => Vector3.Transform(p, basis));
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
                            RootBone.RenderMatrixChanged -= RootBone_WorldMatrixChanged;
                        break;

                    case nameof(Component):
                        if (Component is not null)
                        {
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
                        RootBone.RenderMatrixChanged += RootBone_WorldMatrixChanged;
                        RootBone_WorldMatrixChanged(RootBone, RootBone.RenderMatrix);
                    }
                    break;
                case nameof(Component):
                    if (Component is not null)
                    {
                        Component.Transform.RenderMatrixChanged += Component_WorldMatrixChanged;
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
        /// Updates the culling offset matrix for skinned meshes.
        /// </summary>
        /// <param name="rootBone"></param>
        private void RootBone_WorldMatrixChanged(TransformBase rootBone, Matrix4x4 renderMatrix)
        {
            if (RenderInfo is null)
                return;

            SetSkinnedRootRenderMatrix(renderMatrix);
            RenderInfo.CullingOffsetMatrix = renderMatrix;
            if (!IsSkinned)
                return;
        }

        /// <summary>
        /// Updates the culling offset matrix for non-skinned meshes.
        /// </summary>
        /// <param name="component"></param>
        private void Component_WorldMatrixChanged(TransformBase component, Matrix4x4 renderMatrix)
        {
            //using var timer = Engine.Profiler.Start();

            bool hasSkinning = (CurrentLOD?.Value?.Renderer?.Mesh?.HasSkinning ?? false) && Engine.Rendering.Settings.AllowSkinning;
            if (hasSkinning)
            {
                if (RootBone is null && RenderInfo is not null)
                {
                    SetSkinnedRootRenderMatrix(renderMatrix);
                    RenderInfo.CullingOffsetMatrix = renderMatrix;
                }
                return;
            }

            if (component is null)
                return;
            
            if (_rc is not null)
                _rc.WorldMatrix = renderMatrix;

            if (RenderInfo is not null/* && !hasSkinning*/)
                RenderInfo.CullingOffsetMatrix = renderMatrix;
        }
    }
}
