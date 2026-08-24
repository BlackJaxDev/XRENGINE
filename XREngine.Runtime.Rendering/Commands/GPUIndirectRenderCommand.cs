using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands
{
    public enum EGpuMaterialStateClass : uint
    {
        Invalid = 0,
        OpaqueDeferred = 1,
        OpaqueForward = 2,
        AlphaTested = 3,
        Shadow = 4,
        Transparent = 5,
        Custom = 6
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DrawMetadata
    {
        public uint DrawID;
        public uint MeshID;
        public uint SubmeshID;
        public uint MaterialID;
        public uint TransformID;
        public uint SkinID;
        public uint RenderPassMask;
        public uint LayerMask;
        public uint Flags;
        public uint LodPolicy;
        public uint StateClassID;
        public uint InstanceCount;
        public uint RenderPass;
        public uint RenderIdentityID;
        public uint LogicalMeshID;
        public uint BoundsID;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TransformGpu
    {
        public Matrix4x4 WorldMatrix;

        public TransformGpu(Matrix4x4 worldMatrix)
            => WorldMatrix = worldMatrix;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BoundsGpu
    {
        public Vector4 BoundingSphere;
        public Vector4 AabbMin;
        public Vector4 AabbMax;
        public uint BoundsVersion;
        public uint Padding0;
        public uint Padding1;
        public uint Padding2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MaterialStateGpu
    {
        public uint StateClassID;
        public uint MaterialID;
        public uint PipelineKey;
        public uint OptionsBits;
        public uint TransparencyMode;
        public uint DescriptorStart;
        public uint DescriptorCount;
        public uint Flags;
    }

    [Flags]
    public enum GPUIndirectRenderFlags : uint
    {
        None = 0,
        Transparent = 1 << 0,
        CastShadow = 1 << 1,
        Skinned = 1 << 2,
        Dynamic = 1 << 3,
        DoubleSided = 1 << 4,
        ReceiveShadows = 1 << 8,
        Wireframe = 1 << 9,
        Instanced = 1 << 10,
        Animated = 1 << 11,
        BlendShapes = 1 << 12,
        FrustumCulled = 1 << 13,
        OcclusionCulled = 1 << 14,
        LODEnabled = 1 << 15,
        CustomShader = 1 << 16,
        Deferred = 1 << 17,
        Forward = 1 << 18,
        Unlit = 1 << 19,
        /// <summary>
        /// The mesh prefers the legacy CPU lane in diagnostic strategies. Strict
        /// zero-readback ignores this bit and keeps the draw GPU-resident.
        /// </summary>
        CpuFallbackOnly = 1 << 20,
        /// <summary>
        /// The draw requires raster state that is not represented by the current
        /// canonical opaque-deferred meshlet pipeline (for example front-face culling).
        /// </summary>
        NonCanonicalRasterState = 1 << 21
    }

    public enum GPUSortAlgorithm
    {
        Bitonic,
        Radix,
        Merge
    }

    public enum GPUSortDirection
    {
        Ascending,
        Descending
    }
}
