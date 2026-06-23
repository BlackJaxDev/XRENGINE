using System.Runtime.InteropServices;

namespace XREngine.Rendering.Materials
{

public partial class GPUMaterialTable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct GPUMaterialEntryWords
        {
            public const int WordCount = 12;

            public uint AlbedoHandleIndex;
            public uint NormalHandleIndex;
            public uint RMHandleIndex;
            public uint Flags;
            public uint BaseColorX;
            public uint BaseColorY;
            public uint BaseColorZ;
            public uint Opacity;
            public uint Roughness;
            public uint Metallic;
            public uint Specular;
            public uint Emission;
        }
    }
}
