using System;
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

    [XREngine.Rendering.Shaders.GpuRecord("DrawMetadata", "GPUScene")]
    [StructLayout(LayoutKind.Sequential)]
    public struct DrawMetadata : IEquatable<DrawMetadata>
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

        // Typed equality keeps per-submesh update comparisons off the boxing
        // ValueType.Equals path; every field is a plain integer, so bitwise
        // field comparison is exact.
        public readonly bool Equals(DrawMetadata other)
            => DrawID == other.DrawID
            && MeshID == other.MeshID
            && SubmeshID == other.SubmeshID
            && MaterialID == other.MaterialID
            && TransformID == other.TransformID
            && SkinID == other.SkinID
            && RenderPassMask == other.RenderPassMask
            && LayerMask == other.LayerMask
            && Flags == other.Flags
            && LodPolicy == other.LodPolicy
            && StateClassID == other.StateClassID
            && InstanceCount == other.InstanceCount
            && RenderPass == other.RenderPass
            && RenderIdentityID == other.RenderIdentityID
            && LogicalMeshID == other.LogicalMeshID
            && BoundsID == other.BoundsID;

        public override readonly bool Equals(object? obj)
            => obj is DrawMetadata other && Equals(other);

        public override readonly int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(DrawID);
            hash.Add(MeshID);
            hash.Add(SubmeshID);
            hash.Add(MaterialID);
            hash.Add(TransformID);
            hash.Add(SkinID);
            hash.Add(RenderPassMask);
            hash.Add(LayerMask);
            hash.Add(Flags);
            hash.Add(LodPolicy);
            hash.Add(StateClassID);
            hash.Add(InstanceCount);
            hash.Add(RenderPass);
            hash.Add(RenderIdentityID);
            hash.Add(LogicalMeshID);
            hash.Add(BoundsID);
            return hash.ToHashCode();
        }

        public static bool operator ==(DrawMetadata left, DrawMetadata right) => left.Equals(right);
        public static bool operator !=(DrawMetadata left, DrawMetadata right) => !left.Equals(right);
    }

    [XREngine.Rendering.Shaders.GpuRecord("TransformGpu", "GPUScene")]
    [StructLayout(LayoutKind.Sequential)]
    public struct TransformGpu
    {
        public Matrix4x4 WorldMatrix;

        public TransformGpu(Matrix4x4 worldMatrix)
            => WorldMatrix = worldMatrix;
    }

    [XREngine.Rendering.Shaders.GpuRecord("BoundsGpu", "GPUScene")]
    [StructLayout(LayoutKind.Sequential)]
    public struct BoundsGpu : IEquatable<BoundsGpu>
    {
        public Vector4 BoundingSphere;
        public Vector4 AabbMin;
        public Vector4 AabbMax;
        public uint BoundsVersion;
        public uint Padding0;
        public uint Padding1;
        public uint Padding2;

        // Typed equality: the Vector4 fields make ValueType.Equals take its
        // reflection path, which boxed every field per submesh update. Vector4
        // equality is exact component comparison, matching the previous result.
        public readonly bool Equals(BoundsGpu other)
            => BoundingSphere.Equals(other.BoundingSphere)
            && AabbMin.Equals(other.AabbMin)
            && AabbMax.Equals(other.AabbMax)
            && BoundsVersion == other.BoundsVersion
            && Padding0 == other.Padding0
            && Padding1 == other.Padding1
            && Padding2 == other.Padding2;

        public override readonly bool Equals(object? obj)
            => obj is BoundsGpu other && Equals(other);

        public override readonly int GetHashCode()
            => HashCode.Combine(BoundingSphere, AabbMin, AabbMax, BoundsVersion, Padding0, Padding1, Padding2);

        public static bool operator ==(BoundsGpu left, BoundsGpu right) => left.Equals(right);
        public static bool operator !=(BoundsGpu left, BoundsGpu right) => !left.Equals(right);
    }

    [XREngine.Rendering.Shaders.GpuRecord("MaterialStateGpu", "GPUScene")]
    [StructLayout(LayoutKind.Sequential)]
    public struct MaterialStateGpu : IEquatable<MaterialStateGpu>
    {
        public uint StateClassID;
        public uint MaterialID;
        public uint PipelineKey;
        public uint OptionsBits;
        public uint TransparencyMode;
        public uint DescriptorStart;
        public uint DescriptorCount;
        public uint Flags;

        // Typed equality lets the state-class resolver skip rewriting a row whose
        // content did not change without boxing through ValueType.Equals.
        public readonly bool Equals(MaterialStateGpu other)
            => StateClassID == other.StateClassID
            && MaterialID == other.MaterialID
            && PipelineKey == other.PipelineKey
            && OptionsBits == other.OptionsBits
            && TransparencyMode == other.TransparencyMode
            && DescriptorStart == other.DescriptorStart
            && DescriptorCount == other.DescriptorCount
            && Flags == other.Flags;

        public override readonly bool Equals(object? obj)
            => obj is MaterialStateGpu other && Equals(other);

        public override readonly int GetHashCode()
            => HashCode.Combine(StateClassID, MaterialID, PipelineKey, OptionsBits, TransparencyMode, DescriptorStart, DescriptorCount, Flags);

        public static bool operator ==(MaterialStateGpu left, MaterialStateGpu right) => left.Equals(right);
        public static bool operator !=(MaterialStateGpu left, MaterialStateGpu right) => !left.Equals(right);
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
        NonCanonicalRasterState = 1 << 21,
        /// <summary>Editor hover metadata; traditional GPU dispatch retains the stencil draw.</summary>
        EditorHovered = 1 << 22,
        /// <summary>Editor selection metadata; native visibility keeps the mesh GPU-resident.</summary>
        EditorSelected = 1 << 23,
        EditorHighlightMask = EditorHovered | EditorSelected
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
