using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Vectors;

namespace XREngine.Scene
{
    public partial class Lights3DCollection
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ForwardPointShadowGpu
        {
            public IVector4 Packed0;
            public IVector4 Packed1;
            public Vector4 Params0;
            public Vector4 Params1;
            public Vector4 Params2;
            public Vector4 Params3;
            public Vector4 Params4;
            public Vector4 Params5;
            public IVector4 Packed2;
            public Vector4 Params6;
            public IVector4 AtlasPacked0Face0;
            public IVector4 AtlasPacked0Face1;
            public IVector4 AtlasPacked0Face2;
            public IVector4 AtlasPacked0Face3;
            public IVector4 AtlasPacked0Face4;
            public IVector4 AtlasPacked0Face5;
            public Vector4 AtlasParams0Face0;
            public Vector4 AtlasParams0Face1;
            public Vector4 AtlasParams0Face2;
            public Vector4 AtlasParams0Face3;
            public Vector4 AtlasParams0Face4;
            public Vector4 AtlasParams0Face5;
            public Vector4 AtlasParams1Face0;
            public Vector4 AtlasParams1Face1;
            public Vector4 AtlasParams1Face2;
            public Vector4 AtlasParams1Face3;
            public Vector4 AtlasParams1Face4;
            public Vector4 AtlasParams1Face5;
        }
    }
}
