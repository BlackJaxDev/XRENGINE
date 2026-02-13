using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands
{
    /// <summary>
    /// GPU-side structure for indirect rendering commands.
    /// This structure is designed to be stored in SSBOs and processed by compute shaders.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GPUIndirectRenderCommand
    {
        // 0 - 15 : World Matrix (16 floats)
        public Matrix4x4 WorldMatrix;            // 64 bytes
        // 16 - 31 : Previous World Matrix for motion vectors (16 floats)
        public Matrix4x4 PrevWorldMatrix;        // 64 bytes
        // 32 - 35 : Bounding Sphere
        public Vector4 BoundingSphere;           // 16 bytes
        // 36 : MeshID
        public uint MeshID;                      // 4 bytes
        // 37 : SubmeshID (flattened mesh+submesh)
        public uint SubmeshID;                   // 4 bytes
        // 38 : MaterialID
        public uint MaterialID;                  // 4 bytes
        // 39 : InstanceCount
        public uint InstanceCount;               // 4 bytes
        // 40 : RenderPass
        public uint RenderPass;                  // 4 bytes
        // 41 : ShaderProgramID
        public uint ShaderProgramID;             // 4 bytes
        // 42 : RenderDistance (camera-space depth or squared distance)
        public float RenderDistance;             // 4 bytes
        // 43 : LayerMask
        public uint LayerMask;                   // 4 bytes
        // 44 : LODLevel
        public uint LODLevel;                    // 4 bytes
        // 45 : Flags
        public uint Flags;                       // 4 bytes
        // 46 : Reserved0
        public uint Reserved0;                   // 4 bytes
        // 47 : Reserved1
        public uint Reserved1;                   // 4 bytes
                                                 // Total size: 192 bytes (48 floats)

        /// <summary>
        /// Sets the bounding sphere for culling.
        /// </summary>
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

    /// <summary>
    /// Hot-path GPU command payload used by culling/occlusion/indirect build stages.
    /// Keeps frequently accessed fields in a compact 64-byte struct (16 uints).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GPUIndirectRenderCommandHot
    {
        public Vector4 BoundingSphere;           // 16 bytes
        public uint MeshID;                      // 20
        public uint SubmeshID;                   // 24
        public uint MaterialID;                  // 28
        public uint InstanceCount;               // 32
        public uint RenderPass;                  // 36
        public uint LayerMask;                   // 40
        public uint Flags;                       // 44
        public uint LODLevel;                    // 48
        public uint ShaderProgramID;             // 52
        public float RenderDistance;             // 56 (squared distance in culling path)
        public uint SourceCommandIndex;          // 60 (stable source index for baseInstance mapping)
        public uint Reserved0;                   // 64
    }

    /// <summary>
    /// Cold-path GPU command payload used for matrix and extended metadata reads.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GPUIndirectRenderCommandCold
    {
        public Matrix4x4 WorldMatrix;            // 64 bytes
        public Matrix4x4 PrevWorldMatrix;        // 64 bytes
        public uint ShaderProgramID;             // 4 bytes
        public float RenderDistance;             // 4 bytes
        public uint Reserved0;                   // 4 bytes
        public uint Reserved1;                   // 4 bytes
    }
    
    /// <summary>
    /// Flags for GPU indirect render commands.
    /// </summary>
    [Flags]
    public enum GPUIndirectRenderFlags : uint
    {
        None            = 0,
        Transparent     = 1 << 0,  // matches FLAG_TRANSPARENT
        CastShadow       = 1 << 1,  // matches FLAG_CAST_SHADOW
        Skinned          = 1 << 2,  // matches FLAG_SKINNED
        Dynamic          = 1 << 3,  // matches FLAG_DYNAMIC
        DoubleSided      = 1 << 4,  // matches FLAG_DOUBLE_SIDED
        // Spare bits from 5 upward for future expansion
        // Legacy / extended flags (retain for compatibility)
        ReceiveShadows   = 1 << 8,
        Wireframe        = 1 << 9,
        Instanced        = 1 << 10,
        Animated         = 1 << 11,
        BlendShapes      = 1 << 12,
        FrustumCulled    = 1 << 13,
        OcclusionCulled  = 1 << 14,
        LODEnabled       = 1 << 15,
        CustomShader     = 1 << 16,
        Deferred         = 1 << 17,
        Forward          = 1 << 18,
        Unlit            = 1 << 19
    }
    
    /// <summary>
    /// GPU sorting algorithms.
    /// </summary>
    public enum GPUSortAlgorithm
    {
        /// <summary>
        /// Bitonic sort - good for small to medium arrays, O(n log² n)
        /// </summary>
        Bitonic,
        
        /// <summary>
        /// Radix sort - excellent for large arrays, O(n)
        /// </summary>
        Radix,
        
        /// <summary>
        /// Merge sort - stable sort, O(n log n)
        /// </summary>
        Merge
    }
    
    /// <summary>
    /// GPU sorting direction.
    /// </summary>
    public enum GPUSortDirection
    {
        /// <summary>
        /// Ascending order (near to far for distance, low to high for render pass)
        /// </summary>
        Ascending,
        
        /// <summary>
        /// Descending order (far to near for distance, high to low for render pass)
        /// </summary>
        Descending
    }
} 