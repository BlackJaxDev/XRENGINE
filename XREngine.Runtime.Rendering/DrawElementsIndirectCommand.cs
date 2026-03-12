using System.Runtime.InteropServices;

namespace XREngine.Rendering
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DrawElementsIndirectCommand
    {
        public uint Count;
        public uint InstanceCount;
        public uint FirstIndex;
        public int BaseVertex;
        public uint BaseInstance;
    }
}