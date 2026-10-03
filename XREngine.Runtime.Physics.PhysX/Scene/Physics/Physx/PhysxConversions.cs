using MagicPhysX;
using System.Numerics;
using static MagicPhysX.NativeMethods;

namespace XREngine.Scene.Physics.Physx;

/// <summary>
/// Converts engine numerics values into native PhysX value types.
/// </summary>
public static unsafe class PhysxConversions
{
    public static PxTransform MakeTransform(Vector3? position, Quaternion? rotation)
    {
        Quaternion q = rotation ?? Quaternion.Identity;
        Vector3 p = position ?? Vector3.Zero;
        PxVec3 nativePosition = p;
        PxQuat nativeRotation = q;
        return PxTransform_new_5(&nativePosition, &nativeRotation);
    }
}
