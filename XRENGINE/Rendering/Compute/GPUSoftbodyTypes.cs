using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Vectors;

namespace XREngine.Rendering.Compute;

public enum GPUSoftbodyColliderType
{
    Capsule = 0,
}

[StructLayout(LayoutKind.Sequential)]
public struct GPUSoftbodyParticleData
{
    public Vector3 CurrentPosition;
    public float InverseMass;
    public Vector3 PreviousPosition;
    public float Radius;
    public Vector3 RestPosition;
    public int ClusterMemberStart;
    public int ClusterMemberCount;
    public int InstanceIndex;
    public int Flags;
    public int _pad0; // std430 struct alignment is 16 (from vec3); array stride rounds 60 → 64
}

[StructLayout(LayoutKind.Sequential)]
public struct GPUSoftbodyDistanceConstraintData
{
    public int ParticleA;
    public int ParticleB;
    public float RestLength;
    public float Compliance;
    public float Stiffness;
    public int InstanceIndex;
    public float _pad0;
    public float _pad1;
}

[StructLayout(LayoutKind.Sequential)]
public struct GPUSoftbodyClusterData
{
    public Vector3 RestCenter;
    public float Radius;
    public Vector3 CurrentCenter;
    public float Stiffness;
    public int MemberStart;
    public int MemberCount;
    public int InstanceIndex;
    public int Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public struct GPUSoftbodyClusterMemberData
{
    public int ClusterIndex;
    public int ParticleIndex;
    public float Weight;
    public float Padding0;
    public Vector3 LocalOffset;
    public float Padding1;
}

[StructLayout(LayoutKind.Sequential)]
public struct GPUSoftbodyColliderData
{
    public Vector4 SegmentStartRadius;
    public Vector4 SegmentEndFriction;
    public Vector4 VelocityAndDrag;
    public int Type;
    public int InstanceIndex;
    public float Margin;
    public float CollisionMask;
}

[StructLayout(LayoutKind.Sequential)]
public struct GPUSoftbodyRenderBindingData
{
    public int VertexIndex;
    public int ClusterIndex;
    public float Weight;
    public float Padding0;
    public Vector3 LocalPosition;
    public float Padding1;
    public Vector3 LocalNormal;
    public float Padding2;
}

[StructLayout(LayoutKind.Sequential)]
public struct GPUSoftbodyDispatchData
{
    public IVector4 ParticleConstraintRanges;
    public IVector4 ClusterRanges;
    public IVector4 ColliderBindingRanges;
    public Vector4 SimulationScalars;
    public Vector4 GravitySubsteps;
    public Vector4 ForceIterations;
}