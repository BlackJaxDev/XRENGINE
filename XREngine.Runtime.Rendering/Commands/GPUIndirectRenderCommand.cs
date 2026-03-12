using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands
{
    [StructLayout(LayoutKind.Sequential)]
    public struct GPUIndirectRenderCommand
    {
        public Matrix4x4 WorldMatrix;
        public Matrix4x4 PrevWorldMatrix;
        public Vector4 BoundingSphere;
        public uint MeshID;
        public uint SubmeshID;
        public uint MaterialID;
        public uint InstanceCount;
        public uint RenderPass;
        public uint ShaderProgramID;
        public float RenderDistance;
        public uint LayerMask;
        public uint LODLevel;
        public uint Flags;
        public uint Reserved0;
        public uint Reserved1;

        public void SetBoundingSphere(Vector3 center, float radius)
            => BoundingSphere = new Vector4(center, radius);

        public GPUIndirectRenderCommandHot ToHot(uint sourceCommandIndex)
            => new()
            {
                BoundingSphere = BoundingSphere,
                MeshID = MeshID,
                SubmeshID = SubmeshID,
                MaterialID = MaterialID,
                InstanceCount = InstanceCount,
                RenderPass = RenderPass,
                LayerMask = LayerMask,
                Flags = Flags,
                LODLevel = LODLevel,
                ShaderProgramID = ShaderProgramID,
                RenderDistance = RenderDistance,
                SourceCommandIndex = sourceCommandIndex,
                Reserved0 = Reserved0,
            };

        public GPUIndirectRenderCommandCold ToCold()
            => new()
            {
                WorldMatrix = WorldMatrix,
                PrevWorldMatrix = PrevWorldMatrix,
                ShaderProgramID = ShaderProgramID,
                RenderDistance = RenderDistance,
                Reserved0 = Reserved0,
                Reserved1 = Reserved1,
            };

        public static GPUIndirectRenderCommand FromHotCold(in GPUIndirectRenderCommandHot hot, in GPUIndirectRenderCommandCold cold)
            => new()
            {
                WorldMatrix = cold.WorldMatrix,
                PrevWorldMatrix = cold.PrevWorldMatrix,
                BoundingSphere = hot.BoundingSphere,
                MeshID = hot.MeshID,
                SubmeshID = hot.SubmeshID,
                MaterialID = hot.MaterialID,
                InstanceCount = hot.InstanceCount,
                RenderPass = hot.RenderPass,
                ShaderProgramID = cold.ShaderProgramID,
                RenderDistance = cold.RenderDistance,
                LayerMask = hot.LayerMask,
                LODLevel = hot.LODLevel,
                Flags = hot.Flags,
                Reserved0 = cold.Reserved0,
                Reserved1 = cold.Reserved1,
            };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GPUIndirectRenderCommandHot
    {
        public Vector4 BoundingSphere;
        public uint MeshID;
        public uint SubmeshID;
        public uint MaterialID;
        public uint InstanceCount;
        public uint RenderPass;
        public uint LayerMask;
        public uint Flags;
        public uint LODLevel;
        public uint ShaderProgramID;
        public float RenderDistance;
        public uint SourceCommandIndex;
        public uint Reserved0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GPUIndirectRenderCommandCold
    {
        public Matrix4x4 WorldMatrix;
        public Matrix4x4 PrevWorldMatrix;
        public uint ShaderProgramID;
        public float RenderDistance;
        public uint Reserved0;
        public uint Reserved1;
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
        Unlit = 1 << 19
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