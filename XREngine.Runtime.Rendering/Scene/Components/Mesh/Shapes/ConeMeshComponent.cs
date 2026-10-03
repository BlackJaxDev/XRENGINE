using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Components.Mesh.Shapes
{
    public class ConeMeshComponent : ShapeMeshComponent
    {
        private float _radius = 1.0f;
        private float _height = 1.0f;
        private int _sides = 40;
        private bool _closeBottom = true;

        public float Radius
        {
            get => _radius;
            set => SetField(ref _radius, value);
        }

        public float Height
        {
            get => _height;
            set => SetField(ref _height, value);
        }

        public int Sides
        {
            get => _sides;
            set => SetField(ref _sides, value);
        }

        public bool CloseBottom
        {
            get => _closeBottom;
            set => SetField(ref _closeBottom, value);
        }

        protected override IShape CreateShapeFromProperties()
            => new Cone(Vector3.Zero, Globals.Up, Height, Radius);

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Radius):
                case nameof(Height):
                case nameof(Sides):
                case nameof(CloseBottom):
                    Shape = CreateShapeFromProperties();
                //    new(material, XRMesh.Shapes.SolidCone(Vector3.Zero, Globals.Up, height, radius, meshSides, closeBottom), radius * 8),
                //new(material, XRMesh.Shapes.SolidCone(Vector3.Zero, Globals.Up, height, radius, meshSides / 4 * 3, closeBottom), radius * 16),
                //new(material, XRMesh.Shapes.SolidCone(Vector3.Zero, Globals.Up, height, radius, meshSides / 2, closeBottom), radius * 32),
                //new(material, XRMesh.Shapes.SolidCone(Vector3.Zero, Globals.Up, height, radius, meshSides / 4, closeBottom), radius * 64),
                    break;
            }
        }
    }
}
