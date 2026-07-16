using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Vectors;

namespace XREngine.Scene
{
    public partial class Lights3DCollection
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ForwardSpotShadowGpu
        {
            public IVector4 Packed0;
            public IVector4 Packed1;
            public Vector4 Params0;
            public Vector4 Params1;
            public Vector4 Params2;
            public Vector4 Params3;
            public IVector4 Packed2;
            public Vector4 Params4;
            public Vector4 Params5;
            public IVector4 AtlasPacked0;
            public Vector4 AtlasParams0;
            public Vector4 AtlasParams1;
            public Vector4 Params6;
        }
    }
}
