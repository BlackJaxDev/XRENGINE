using Extensions;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Core;

namespace XREngine.Data.Geometry
{
    /// <summary>
    /// Represents a circle in 3D space.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Circle3D
    {
        public Circle3D(float radius)
        {
            _plane = new Plane(Globals.Up, 0.0f);
            _radius = radius;
        }
        public Circle3D(float radius, Vector3 normal, float distance)
        {
            _plane = new Plane(normal, distance);
            _radius = radius;
        }
        public Circle3D(float radius, Vector3 point)
        {
            Vector3 normal = (-point).Normalized();
            float distance = XRMath.GetPlaneDistance(point, normal);
            _plane = new Plane(normal, distance);
            _radius = radius;
        }
        public Circle3D(float radius, Vector3 point, Vector3 normal)
        {
            float distance = XRMath.GetPlaneDistance(point, normal);
            _plane = new Plane(normal, distance);
            _radius = radius;
        }
        public Circle3D(float radius, Vector3 point0, Vector3 point1, Vector3 point2)
        {
            _plane = Plane.CreateFromVertices(point0, point1, point2);
            _radius = radius;
        }
        public Circle3D(float radius, Plane plane)
        {
            _plane = plane;
            _radius = radius;
        }

        private Plane _plane;
        private float _radius;

        public Plane Plane
        {
            readonly get => _plane;
            set => _plane = value;
        }

        public float Radius
        {
            readonly get => _radius;
            set => _radius = value;
        }
    }
}
