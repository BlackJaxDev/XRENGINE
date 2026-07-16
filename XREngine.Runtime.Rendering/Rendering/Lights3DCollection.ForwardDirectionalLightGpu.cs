using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Scene
{
    public partial class Lights3DCollection
    {
        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct ForwardDirectionalLightGpu
        {
            public ForwardBaseLightGpu Base;
            public Vector4 DirectionPadding;
            public Matrix4x4 WorldToLightInvViewMatrix;
            public Matrix4x4 WorldToLightProjMatrix;
            public Matrix4x4 WorldToLightSpaceMatrix;
            public fixed float CascadeSplits[ForwardMaxCascades];
            public fixed float CascadeBlendWidths[ForwardMaxCascades];
            public fixed float CascadeBiasMin[ForwardMaxCascades];
            public fixed float CascadeBiasMax[ForwardMaxCascades];
            public fixed float CascadeReceiverOffsets[ForwardMaxCascades];
            public fixed float CascadeMatrices[ForwardMaxCascades * 16];
            public fixed float RenderedCascadeSplits[ForwardMaxCascades];
            public fixed float RenderedCascadeBlendWidths[ForwardMaxCascades];
            public fixed float RenderedCascadeBiasMin[ForwardMaxCascades];
            public fixed float RenderedCascadeBiasMax[ForwardMaxCascades];
            public fixed float RenderedCascadeReceiverOffsets[ForwardMaxCascades];
            public fixed float RenderedCascadeMatrices[ForwardMaxCascades * 16];
            public fixed float RenderedCascadeStaleAge[ForwardMaxCascades];
            public int CascadeCount;
            private int _padding0;
            private int _padding1;
            private int _padding2;
        }
    }
}
