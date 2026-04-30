using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Components.Mesh.Shapes
{
    public class ConeMeshComponent : ShapeMeshComponent
    {
        public float Radius { get; set; } = 1.0f;
        public float Height { get; set; } = 1.0f;
        public int Sides { get; set; } = 40;
        public bool CloseBottom { get; set; } = true;

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Radius):
                case nameof(Height):
                case nameof(Sides):
                case nameof(CloseBottom):
                    Shape = new Cone(Vector3.Zero, Globals.Up, Height, Radius);
                //    new(material, XRMesh.Shapes.SolidCone(Vector3.Zero, Globals.Up, height, radius, meshSides, closeBottom), radius * 8),
                //new(material, XRMesh.Shapes.SolidCone(Vector3.Zero, Globals.Up, height, radius, meshSides / 4 * 3, closeBottom), radius * 16),
                //new(material, XRMesh.Shapes.SolidCone(Vector3.Zero, Globals.Up, height, radius, meshSides / 2, closeBottom), radius * 32),
                //new(material, XRMesh.Shapes.SolidCone(Vector3.Zero, Globals.Up, height, radius, meshSides / 4, closeBottom), radius * 64),
                    break;
            }
        }
    }
}
