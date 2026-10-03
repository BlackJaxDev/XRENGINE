using System.Runtime.InteropServices;
namespace XREngine.Rendering.Meshlets;

/// <summary>Describes mesh-processing output with a stable sequential byte layout.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MeshOptimizerMeshlet
{
        public uint VertexOffset;
        public uint TriangleOffset;
        public uint VertexCount;
        public uint TriangleCount;
}
