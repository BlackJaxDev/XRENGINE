using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.GI.DDGI;

/// <summary>GPU representation of a probe ray intersection in the persistent hit buffer SSBO (32 bytes).</summary>
/// <remarks>Matches HitRecord in BvhRaycastCore.glsl layout.</remarks>
[StructLayout(LayoutKind.Sequential)]
public struct DDGIHitGPU
{
    /// <summary>Hit distance along ray.</summary>
    public float T;

    /// <summary>DDGI material table index supplied by the aggregate geometry BVH.</summary>
    public uint ObjectId;

    /// <summary>Hit mesh face index.</summary>
    public uint FaceIndex;

    /// <summary>Hit triangle index within the aggregate scene BVH.</summary>
    public uint TriangleIndex;

    /// <summary>Barycentric hit coordinates (u, v, w).</summary>
    public Vector3 Barycentric;

    /// <summary>Std430 struct alignment padding.</summary>
    public float Padding;
}
