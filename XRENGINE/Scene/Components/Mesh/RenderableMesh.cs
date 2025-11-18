using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
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
        private readonly object _skinnedDataLock = new();
        private bool _skinnedBoundsDirty = true;
        private bool _skinnedBvhDirty = true;
        private bool _hasSkinnedBounds;
        private AABB _skinnedWorldBounds;
        private Vector3[]? _skinnedVertexPositions;
        private int _skinnedVertexCount;
        private BVH<Triangle>? _skinnedBvh;
        private AABB _bindPoseBounds;

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
                    }
                }
                else if (_trackedSkinnedBones.TryGetValue(bone, out int count))
                {
                    if (count <= 1)
                    {
                        _trackedSkinnedBones.Remove(bone);
                        bone.RenderMatrixChanged -= Bone_RenderMatrixChanged;
                    }
                    else
                        _trackedSkinnedBones[bone] = count - 1;
                }
            }
        }

        private void UntrackAllBones()
        {
            foreach (var pair in _trackedSkinnedBones.ToArray())
                pair.Key.RenderMatrixChanged -= Bone_RenderMatrixChanged;
            _trackedSkinnedBones.Clear();
        }

        private void Bone_RenderMatrixChanged(TransformBase bone, Matrix4x4 renderMatrix)
            => MarkSkinnedDataDirty();

        private void MarkSkinnedDataDirty()
        {
            _skinnedBoundsDirty = true;
            _skinnedBvhDirty = true;
            _hasSkinnedBounds = false;
        }

        private bool EnsureSkinnedBounds()
        {
            if (!IsSkinned)
                return false;

            lock (_skinnedDataLock)
            {
                if (!_skinnedBoundsDirty && _hasSkinnedBounds)
                    return true;

                if (Engine.Rendering.Settings.CalculateSkinnedBoundsInComputeShader &&
                    TryComputeSkinnedBoundsOnGpu())
                    return true;

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

                _skinnedVertexPositions ??= new Vector3[vertices.Length];
                if (_skinnedVertexPositions.Length < vertices.Length)
                    Array.Resize(ref _skinnedVertexPositions, vertices.Length);
                _skinnedVertexCount = vertices.Length;

                bool initialized = false;
                Vector3 min = Vector3.Zero;
                Vector3 max = Vector3.Zero;
                Matrix4x4 fallbackMatrix = Component.Transform.RenderMatrix;

                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector3 worldPos = ComputeSkinnedPosition(vertices[i], fallbackMatrix);
                    _skinnedVertexPositions[i] = worldPos;

                    if (!initialized)
                    {
                        min = max = worldPos;
                        initialized = true;
                    }
                    else
                    {
                        min = Vector3.Min(min, worldPos);
                        max = Vector3.Max(max, worldPos);
                    }
                }

                if (!initialized)
                {
                    _hasSkinnedBounds = false;
                    return false;
                }

                _skinnedWorldBounds = new AABB(min, max);
                _skinnedBoundsDirty = false;
                _hasSkinnedBounds = true;
                _skinnedBvhDirty = true;

                RenderInfo.LocalCullingVolume = _skinnedWorldBounds;
                RenderInfo.CullingOffsetMatrix = Matrix4x4.Identity;
                return true;
            }
        }

        private bool TryComputeSkinnedBoundsOnGpu()
        {
            if (!SkinnedMeshBoundsCalculator.Instance.TryCompute(this, out var result))
                return false;

            _skinnedVertexPositions = result.Positions;
            _skinnedVertexCount = _skinnedVertexPositions.Length;
            if (_skinnedVertexCount == 0)
                return false;

            _skinnedWorldBounds = result.Bounds;
            _skinnedBoundsDirty = false;
            _hasSkinnedBounds = true;
            _skinnedBvhDirty = true;

            RenderInfo.LocalCullingVolume = _skinnedWorldBounds;
            RenderInfo.CullingOffsetMatrix = Matrix4x4.Identity;
            return true;
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

                var mesh = CurrentLODRenderer?.Mesh;
                if (mesh?.Triangles is null || _skinnedVertexPositions is null)
                {
                    _skinnedBvh = null;
                    _skinnedBvhDirty = false;
                    return null;
                }

                var worldTriangles = new List<Triangle>(mesh.Triangles.Count);
                foreach (var tri in mesh.Triangles)
                {
                    if (tri.Point0 >= _skinnedVertexCount ||
                        tri.Point1 >= _skinnedVertexCount ||
                        tri.Point2 >= _skinnedVertexCount)
                        continue;

                    worldTriangles.Add(new Triangle(
                        _skinnedVertexPositions[tri.Point0],
                        _skinnedVertexPositions[tri.Point1],
                        _skinnedVertexPositions[tri.Point2]));
                }

                _skinnedBvh = worldTriangles.Count > 0
                    ? new BVH<Triangle>(new TriangleAdapter(), worldTriangles)
                    : null;
                _skinnedBvhDirty = false;
                return _skinnedBvh;
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
                if (RootBone is not null)
                    localSegment = worldSegment.TransformedBy(RootBone.InverseWorldMatrix);
                else
                    localSegment = worldSegment;
            }
            else
            {
                localSegment = worldSegment.TransformedBy(Component.Transform.InverseWorldMatrix);
            }

            return localSegment;
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

            if (IsSkinned)
            {
                MarkSkinnedDataDirty();
                return;
            }

            RenderInfo.CullingOffsetMatrix = renderMatrix;
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
                MarkSkinnedDataDirty();
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
