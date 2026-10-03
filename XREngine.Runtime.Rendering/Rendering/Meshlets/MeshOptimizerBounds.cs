using System.Runtime.InteropServices;
namespace XREngine.Rendering.Meshlets;

/// <summary>Describes mesh-processing output with a stable sequential byte layout.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MeshOptimizerBounds
{
        public float CenterX;
        public float CenterY;
        public float CenterZ;
        public float Radius;
        public float ConeApexX;
        public float ConeApexY;
        public float ConeApexZ;
        public float ConeAxisX;
        public float ConeAxisY;
        public float ConeAxisZ;
        public float ConeCutoff;
        public sbyte ConeAxisS8X;
        public sbyte ConeAxisS8Y;
        public sbyte ConeAxisS8Z;
        public sbyte ConeCutoffS8;
}
