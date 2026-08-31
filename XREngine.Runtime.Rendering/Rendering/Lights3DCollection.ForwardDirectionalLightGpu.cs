using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Scene
{
    public partial class Lights3DCollection
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ForwardDirectionalLightGpu
        {
            public ForwardBaseLightGpu Base;
            public Vector4 DirectionPadding;
            public Matrix4x4 WorldToLightInvViewMatrix;
            public Matrix4x4 WorldToLightProjMatrix;
            public Matrix4x4 WorldToLightSpaceMatrix;
            public int CascadeCount;
            private int _padding0;
            private int _padding1;
            private int _padding2;
        }
    }
}
