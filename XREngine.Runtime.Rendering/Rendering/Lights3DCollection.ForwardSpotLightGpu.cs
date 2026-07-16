using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Scene
{
    public partial class Lights3DCollection
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ForwardSpotLightGpu
        {
            public ForwardPointLightGpu Base;
            public Vector4 DirectionInnerCutoff;
            public Vector4 OuterCutoffExponentPadding;
        }
    }
}
