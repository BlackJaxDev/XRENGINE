using XREngine.Data.Geometry;

namespace XREngine.Components.Mesh.Shapes
{
    public class BoxMeshComponent : ShapeMeshComponent
    {
        private AABB _box;
        public AABB Box
        {
            get => _box;
            set => SetField(ref _box, value);
        }

        protected override IShape CreateShapeFromProperties()
            => Box;

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Box):
                    Shape = CreateShapeFromProperties();
                    //new SubMeshLOD(material, XRMesh.Shapes.SolidBox(-halfExtents, halfExtents), 0.0f);
                    break;
            }
        }
    }
}
