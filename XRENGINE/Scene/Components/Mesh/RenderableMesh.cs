using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Scene.Mesh
{
    public class RenderableMesh : XRBase, IDisposable
    {
        public RenderInfo3D RenderInfo { get; }

        private readonly RenderCommandMesh3D _rc;

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
                        renderer.Mesh = lod.Mesh;
                    else if (e.PropertyName == nameof(SubMeshLOD.Material))
                        renderer.Material = lod.Material;
                }
                lod.PropertyChanged += UpdateReferences;
                LODs.AddLast(new RenderableLOD(renderer, lod.MaxVisibleDistance));
            }

            _renderBoundsCommand = new RenderCommandMethod3D((int)EDefaultRenderPass.OpaqueForward, DoRenderBounds);
            RenderInfo = RenderInfo3D.New(component, _rc = new RenderCommandMesh3D(0));
            if (RenderBounds)
                RenderInfo.RenderCommands.Add(_renderBoundsCommand);
            RenderInfo.LocalCullingVolume = mesh.CullingBounds ?? mesh.Bounds;
            RenderInfo.PreCollectCommandsCallback = BeforeAdd;

            if (LODs.Count > 0)
                CurrentLOD = LODs.First;
        }

        private void DoRenderBounds()
        {
            if (Engine.Rendering.State.IsShadowPass)
                return;

            var box = (RenderInfo as IOctreeItem)?.WorldCullingVolume;
            if (box is not null)
                Engine.Rendering.Debug.RenderBox(box.Value.LocalHalfExtents, box.Value.LocalCenter, box.Value.Transform, false, ColorF4.White);

            if (RootBone is not null)
                Engine.Rendering.Debug.RenderPoint(RootBone.RenderTranslation, ColorF4.Red);
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

            _rc.Mesh = rend;
            //RenderInfo.CullingOffsetMatrix = _rc.WorldMatrix = skinned ? Matrix4x4.Identity : Component.Transform.WorldMatrix;
            _rc.RenderDistance = distance;

            var mat = rend?.Material;
            if (mat is not null)
                _rc.RenderPass = mat.RenderPass;

            return true;
        }

        public record RenderableLOD(XRMeshRenderer Renderer, float MaxVisibleDistance);

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
            //using var timer = Engine.Profiler.Start();

            bool hasSkinning = (CurrentLOD?.Value?.Renderer?.Mesh?.HasSkinning ?? false) && Engine.Rendering.Settings.AllowSkinning;
            if (!hasSkinning)
                return;
            
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
                return;

            if (component is null)
                return;
            
            if (_rc is not null)
                _rc.WorldMatrix = renderMatrix;

            if (RenderInfo is not null/* && !hasSkinning*/)
                RenderInfo.CullingOffsetMatrix = renderMatrix;
        }
    }
}
