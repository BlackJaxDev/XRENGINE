using System.Runtime.InteropServices;

namespace XREngine.Rendering.Materials
{

public partial class GPUMaterialTable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct GPUMaterialEntryWords
        {
            // XR_MaterialRecord contains vec4 fields, so std430 rounds its array
            // stride up to four words. Keep this CPU upload struct at that stride.
            public const int WordCount = 16;

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
            public uint AlphaCutoff;
            public uint Padding0;
            public uint Padding1;
            public uint Padding2;
        }
    }
}
