using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering.Objects;

namespace XREngine.Rendering.Meshlets
{
    /// <summary>
    /// GPU-facing meshlet layout used by the task/mesh shaders.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Meshlet : IBufferable
    {
        public Vector4 BoundingSphere;   // xyz = center, w = radius (object space)
        public uint VertexOffset;        // offset into meshlet vertex index buffer
        public uint TriangleOffset;      // offset into meshlet triangle buffer (triplets)
        public uint VertexCount;         // vertices in this meshlet
        public uint TriangleCount;       // triangles in this meshlet
        public uint MeshID;              // lookup into transform buffer
        public uint MaterialID;          // lookup into material buffer
        private uint _padding0;
        private uint _padding1;

        public EComponentType ComponentType => EComponentType.Struct;
        public uint ComponentCount => 1;
        public bool Normalize => false;

        public unsafe void Read(VoidPtr address)
            => this = *(Meshlet*)address.Pointer;

        public readonly unsafe void Write(VoidPtr address)
            => *(Meshlet*)address.Pointer = this;
    }
}
