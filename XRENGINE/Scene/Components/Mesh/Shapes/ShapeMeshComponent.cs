using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Rendering;

namespace XREngine.Components.Mesh.Shapes
{
    public abstract class ShapeMeshComponent : RenderableComponent
    {
        private IShape? _shape;
        private XRMaterial? _material;

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

                    Meshes.Clear();

                    if (Shape is not null)
                        Meshes.Add(new RenderableMesh(new(XRMesh.Shapes.FromVolume(Shape, false), Material), this));

                    break;
            }
        }
    }
}
