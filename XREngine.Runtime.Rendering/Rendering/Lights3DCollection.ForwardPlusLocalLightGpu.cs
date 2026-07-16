using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Vectors;

namespace XREngine.Scene
{
    public partial class Lights3DCollection
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ForwardPlusLocalLightGpu
        {
            public Vector4 PositionWS;
            public Vector4 DirectionWS_Exponent;
            public Vector4 Color_Type;
            public Vector4 Params;
            public Vector4 SpotAngles;
            public IVector4 Indices;
        }
    }
}
