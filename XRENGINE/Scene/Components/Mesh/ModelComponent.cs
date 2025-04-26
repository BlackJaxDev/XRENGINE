using Extensions;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Models;

namespace XREngine.Components.Scene.Mesh
{
    [Serializable]
    public class ModelComponent : RenderableComponent
    {
        private readonly ConcurrentDictionary<SubMesh, RenderableMesh> _meshLinks = new();

        private Model? _model;
        public Model? Model
        {
            get => _model;
            set => SetField(ref _model, value);
        }

        private bool _renderBounds = false;
        public bool RenderBounds
        {
            get => _renderBounds;
            set => SetField(ref _renderBounds, value);
        }

        private IReadOnlyDictionary<SubMesh, RenderableMesh> MeshLinks => _meshLinks;

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(Model):
                        if (Model != null)
                        {
                            foreach (SubMesh mesh in Model.Meshes)
                                if (_meshLinks.TryRemove(mesh, out RenderableMesh? mesh2))
                                    Meshes.Remove(mesh2);

                            Model.Meshes.PostAnythingAdded -= AddMesh;
                            Model.Meshes.PostAnythingRemoved -= RemoveMesh;
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
                case nameof(Model):
                    ModelChanged();
                    break;
                case nameof(RenderBounds):
                    foreach (RenderableMesh mesh in Meshes)
                        mesh.RenderBounds = RenderBounds;
                    break;
            }
        }

        private void ModelChanged()
        {
            using var t = Engine.Profiler.Start("ModelComponent.ModelChanged");

            Meshes.Clear();
            if (Model is null)
                return;
            
            foreach (SubMesh mesh in Model.Meshes)
            {
                RenderableMesh rendMesh = new(mesh, this)
                {
                    //RootTransform = mesh.RootTransform
                };
                Meshes.Add(rendMesh);
                _meshLinks.TryAdd(mesh, rendMesh);
            }

            Model.Meshes.PostAnythingAdded += AddMesh;
            Model.Meshes.PostAnythingRemoved += RemoveMesh;
        }

        private void AddMesh(SubMesh item)
        {
            RenderableMesh mesh = new(item, this)
            {
                //RootTransform = item.RootTransform
            };
            Meshes.Add(mesh);
            _meshLinks.TryAdd(item, mesh);
        }
        private void RemoveMesh(SubMesh item)
        {
            if (_meshLinks.TryRemove(item, out RenderableMesh? mesh))
                Meshes.Remove(mesh);
        }

        [RequiresDynamicCode("")]
        public float? Intersect(Segment segment, out Triangle? triangle)
        {
            triangle = null;
            float? closest = null;
            foreach (RenderableMesh mesh in Meshes)
            {
                var m = mesh.CurrentLODMesh;
                if (m is null)
                    continue;

                float? distance = mesh.Intersect(mesh.GetLocalSegment(segment, m.HasSkinning), out triangle);
                if (distance.HasValue && (!closest.HasValue || distance < closest))
                    closest = distance;
            }
            return closest;
        }

        public IEnumerable<XRMeshRenderer> GetAllRenderersWhere(Predicate<XRMeshRenderer> predicate)
            => Meshes.SelectMany(x => x.LODs).Select(x => x.Renderer).Where(x => predicate(x));

        public void SetBlendShapeWeight(string blendshapeName, float percentage, StringComparison comp = StringComparison.InvariantCultureIgnoreCase)
        {
            //Debug.Out($"SetBlendShapeWeight: {blendshapeName} {percentage}");

            bool HasMatchingBlendshape(XRMeshRenderer x)
                => (x?.Mesh?.HasBlendshapes ?? false) && x.Mesh!.BlendshapeNames.Contains(blendshapeName, comp);
            var rends = GetAllRenderersWhere(HasMatchingBlendshape);
            //if (!rends.Any())
            //{
            //    Debug.LogWarning($"No renderers found with blendshape {blendshapeName}");
            //    return;
            //}
            rends.ForEach(x => x.SetBlendshapeWeight(blendshapeName, percentage));
        }
        public void SetBlendShapeWeightNormalized(string blendshapeName, float weight, StringComparison comp = StringComparison.InvariantCultureIgnoreCase)
        {
            //Debug.Out($"SetBlendShapeWeightNormalized: {blendshapeName} {weight}");

            bool HasMatchingBlendshape(XRMeshRenderer x)
                => (x?.Mesh?.HasBlendshapes ?? false) && x.Mesh!.BlendshapeNames.Contains(blendshapeName, comp);
            var rends = GetAllRenderersWhere(HasMatchingBlendshape);
            //if (!rends.Any())
            //{
            //    Debug.LogWarning($"No renderers found with blendshape {blendshapeName}");
            //    return;
            //}
            rends.ForEach(x => x.SetBlendshapeWeightNormalized(blendshapeName, weight));
        }
    }
}
