using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Scene;
using YamlDotNet.Serialization;

namespace XREngine.Components.Mesh.Shapes
{
    public abstract class ShapeMeshComponent : RenderableComponent
    {
        private IShape? _shape;
        private XRMaterial? _material;
        private bool _meshRebuildPending;

        [YamlIgnore]
        public IShape? Shape
        {
            get => _shape;
            set => SetField(ref _shape, value);
        }

        public XRMaterial? Material
        {
            get => _material;
            set => SetField(ref _material, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Shape):
                    RebuildMeshWhenAttached();
                    break;
                case nameof(Material):
                    // Propagate the new material to every existing LOD renderer
                    // so the mesh updates immediately without requiring a shape rebuild.
                    foreach (var mesh in Meshes)
                        foreach (RenderableMesh.RenderableLOD lod in mesh.GetLodSnapshot())
                            lod.Renderer.Material = Material;
                    break;
            }
        }

        protected override void AddedToSceneNode(SceneNode sceneNode)
        {
            base.AddedToSceneNode(sceneNode);

            if (_meshRebuildPending || (Shape is not null && Meshes.Count == 0))
                RebuildMeshWhenAttached();
        }

        protected override void OwningSceneNodePostDeserialize()
        {
            base.OwningSceneNodePostDeserialize();

            // Shape is derived from serialized dimensions and is not itself serialized.
            // Snapshot restoration suppresses property notifications, so rebuild once
            // after the owning node has restored its material and transform.
            SetField(ref _shape, CreateShapeFromProperties(), publishNotifications: false, nameof(Shape));
            RebuildMeshWhenAttached();
        }

        protected abstract IShape CreateShapeFromProperties();

        private void RebuildMeshWhenAttached()
        {
            if (SceneNode is null)
            {
                _meshRebuildPending = Shape is not null;
                return;
            }

            RenderableMesh[] oldMeshes = Meshes.Count == 0 ? [] : Meshes.ToArray();
            Meshes.Clear();
            foreach (RenderableMesh mesh in oldMeshes)
                mesh.Dispose();
            _meshRebuildPending = false;

            IShape? shape = Shape;
            if (shape is null)
                return;

            Meshes.Add(new RenderableMesh(
                new(XRMesh.Shapes.FromVolume(shape, false), Material),
                this));
        }
    }
}
