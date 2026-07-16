using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Scene
{
    public partial class Lights3DCollection
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ForwardPointLightGpu
        {
            public ForwardBaseLightGpu Base;
            public Vector4 PositionRadius;
            public Vector4 BrightnessPadding;
        }
    }
}
