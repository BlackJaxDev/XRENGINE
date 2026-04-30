using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Components.Mesh.Shapes
{
    public class SphereMeshComponent : ShapeMeshComponent
    {
        private float _radius = 1.0f;
        private uint _meshPrecision = 40u;

        public float Radius
        {
            get => _radius;
            set => SetField(ref _radius, value);
        }

        public uint MeshPrecision
        {
            get => _meshPrecision;
            set => SetField(ref _meshPrecision, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Radius):
                case nameof(MeshPrecision):
                    Shape = new Sphere(Vector3.Zero, Radius);
        //            new Sphere(Vector3.Zero, radius),
        //[
        //    new(material, XRMesh.Shapes.SolidSphere(Vector3.Zero, radius, meshPrecision), radius * 8),
        //    new(material, XRMesh.Shapes.SolidSphere(Vector3.Zero, radius, (uint)(meshPrecision * 0.8f).ClampMin(1.0f)), radius * 16),
        //    new(material, XRMesh.Shapes.SolidSphere(Vector3.Zero, radius, (uint)(meshPrecision * 0.6f).ClampMin(1.0f)), radius * 32),
        //    new(material, XRMesh.Shapes.SolidSphere(Vector3.Zero, radius, (uint)(meshPrecision * 0.4f).ClampMin(1.0f)), radius * 64),
        //    new(material, XRMesh.Shapes.SolidSphere(Vector3.Zero, radius, (uint)(meshPrecision * 0.2f).ClampMin(1.0f)), radius * 128),
        //],
        ////[
        ////    new(material, XRMesh.Shapes.SolidSphere(Vector3.Zero, radius, meshPrecision), radius * 2),
        ////    new(material, XRMesh.Shapes.SolidSphere(Vector3.Zero, radius, (uint)(meshPrecision * 0.7f).ClampMin(1.0f)), radius * 4),
        ////    new(material, XRMesh.Shapes.SolidSphere(Vector3.Zero, radius, (uint)(meshPrecision * 0.5f).ClampMin(1.0f)), radius * 8),
        ////    new(material, XRMesh.Shapes.SolidSphere(Vector3.Zero, radius, (uint)(meshPrecision * 0.3f).ClampMin(1.0f)), radius * 16),
        ////    new(material, XRMesh.Shapes.SolidSphere(Vector3.Zero, radius, (uint)(meshPrecision * 0.15f).ClampMin(1.0f)), radius * 32),
        ////],
                    break;
            }
        }
    }
}
