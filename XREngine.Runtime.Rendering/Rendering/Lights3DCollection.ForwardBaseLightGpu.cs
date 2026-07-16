using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Scene
{
    public partial class Lights3DCollection
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ForwardBaseLightGpu
        {
            public Vector4 ColorDiffuse;
            public Vector4 AmbientPadding;
            public Matrix4x4 WorldToLightSpaceProjMatrix;
        }
    }
}
