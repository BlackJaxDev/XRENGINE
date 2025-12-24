using System.Runtime.InteropServices;

namespace XREngine.Rendering
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DrawElementsIndirectCommand
    {
        public uint Count;          // Number of indices to draw
        public uint InstanceCount;  // Number of instances to draw
        public uint FirstIndex;     // First index in the index buffer
        public int BaseVertex;      // Base vertex offset
        public uint BaseInstance;   // Base instance offset
    }
}