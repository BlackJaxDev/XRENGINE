using System.Numerics;
using MagicPhysX;
using XREngine.Components.Physics;

namespace XREngine.Scene.Physics.Physx;

public unsafe partial class PhysxDynamicRigidBody
{
    bool IPhysicsRuntimeBodyProperties.IsInScene => Scene is not null;
    AbstractPhysicsScene? IPhysicsSceneAttachedActor.AttachedScene => Scene;

    void IPhysicsRuntimeBodyProperties.ApplyShapeOffset(Vector3 translation, Quaternion rotation)
    {
        var shapes = GetShapes();
        for (int index = 0; index < shapes.Length; index++)
        {
            if (shapes[index] is { } shape)
                shape.LocalPose = (translation, rotation);
        }
    }

    PhysicsGroupsMask IPhysicsRuntimeBodyProperties.GroupsMask
    {
        get => new(GroupsMask.bits0, GroupsMask.bits1, GroupsMask.bits2, GroupsMask.bits3);
        set
        {
            PxGroupsMask mask;
            mask.bits0 = (ushort)value.Word0;
            mask.bits1 = (ushort)value.Word1;
            mask.bits2 = (ushort)value.Word2;
            mask.bits3 = (ushort)value.Word3;
            GroupsMask = mask;
        }
    }

    PhysicsRigidBodyFlags IPhysicsRuntimeBodyProperties.BodyFlags
    {
        get
        {
            PhysicsRigidBodyFlags flags = PhysicsRigidBodyFlags.None;
            if (Flags.HasFlag(PxRigidBodyFlags.Kinematic)) flags |= PhysicsRigidBodyFlags.Kinematic;
            if (Flags.HasFlag(PxRigidBodyFlags.UseKinematicTargetForSceneQueries)) flags |= PhysicsRigidBodyFlags.UseKinematicTargetForQueries;
            if (Flags.HasFlag(PxRigidBodyFlags.EnableCcd)) flags |= PhysicsRigidBodyFlags.EnableCcd;
            if (Flags.HasFlag(PxRigidBodyFlags.EnableSpeculativeCcd)) flags |= PhysicsRigidBodyFlags.EnableSpeculativeCcd;
            if (Flags.HasFlag(PxRigidBodyFlags.EnableCcdMaxContactImpulse)) flags |= PhysicsRigidBodyFlags.EnableCcdMaxContactImpulse;
            if (Flags.HasFlag(PxRigidBodyFlags.EnableCcdFriction)) flags |= PhysicsRigidBodyFlags.EnableCcdFriction;
            return flags;
        }
        set
        {
            PxRigidBodyFlags flags = 0;
            if (value.HasFlag(PhysicsRigidBodyFlags.Kinematic)) flags |= PxRigidBodyFlags.Kinematic;
            if (value.HasFlag(PhysicsRigidBodyFlags.UseKinematicTargetForQueries)) flags |= PxRigidBodyFlags.UseKinematicTargetForSceneQueries;
            if (value.HasFlag(PhysicsRigidBodyFlags.EnableCcd)) flags |= PxRigidBodyFlags.EnableCcd;
            if (value.HasFlag(PhysicsRigidBodyFlags.EnableSpeculativeCcd)) flags |= PxRigidBodyFlags.EnableSpeculativeCcd;
            if (value.HasFlag(PhysicsRigidBodyFlags.EnableCcdMaxContactImpulse)) flags |= PxRigidBodyFlags.EnableCcdMaxContactImpulse;
            if (value.HasFlag(PhysicsRigidBodyFlags.EnableCcdFriction)) flags |= PxRigidBodyFlags.EnableCcdFriction;
            Flags = flags;
        }
    }

    PhysicsLockFlags IPhysicsRuntimeBodyProperties.LockFlags
    {
        get
        {
            PhysicsLockFlags flags = PhysicsLockFlags.None;
            if (LockFlags.HasFlag(PxRigidDynamicLockFlags.LockLinearX)) flags |= PhysicsLockFlags.LinearX;
            if (LockFlags.HasFlag(PxRigidDynamicLockFlags.LockLinearY)) flags |= PhysicsLockFlags.LinearY;
            if (LockFlags.HasFlag(PxRigidDynamicLockFlags.LockLinearZ)) flags |= PhysicsLockFlags.LinearZ;
            if (LockFlags.HasFlag(PxRigidDynamicLockFlags.LockAngularX)) flags |= PhysicsLockFlags.AngularX;
            if (LockFlags.HasFlag(PxRigidDynamicLockFlags.LockAngularY)) flags |= PhysicsLockFlags.AngularY;
            if (LockFlags.HasFlag(PxRigidDynamicLockFlags.LockAngularZ)) flags |= PhysicsLockFlags.AngularZ;
            return flags;
        }
        set
        {
            PxRigidDynamicLockFlags flags = 0;
            if (value.HasFlag(PhysicsLockFlags.LinearX)) flags |= PxRigidDynamicLockFlags.LockLinearX;
            if (value.HasFlag(PhysicsLockFlags.LinearY)) flags |= PxRigidDynamicLockFlags.LockLinearY;
            if (value.HasFlag(PhysicsLockFlags.LinearZ)) flags |= PxRigidDynamicLockFlags.LockLinearZ;
            if (value.HasFlag(PhysicsLockFlags.AngularX)) flags |= PxRigidDynamicLockFlags.LockAngularX;
            if (value.HasFlag(PhysicsLockFlags.AngularY)) flags |= PxRigidDynamicLockFlags.LockAngularY;
            if (value.HasFlag(PhysicsLockFlags.AngularZ)) flags |= PxRigidDynamicLockFlags.LockAngularZ;
            LockFlags = flags;
        }
    }

    PhysicsMassFrame IPhysicsRuntimeBodyProperties.CenterOfMassLocalPose
    {
        get => new(CMassLocalPose.Item2, CMassLocalPose.Item1);
        set => CMassLocalPose = (value.Rotation, value.Translation);
    }

    float IPhysicsRuntimeBodyProperties.MinCcdAdvanceCoefficient
    {
        get => MinCCDAdvanceCoefficient;
        set => MinCCDAdvanceCoefficient = value;
    }

    PhysicsSolverIterations IPhysicsRuntimeBodyProperties.SolverIterations
    {
        get => new(SolverIterationCounts.minPositionIters, SolverIterationCounts.minVelocityIters);
        set => SolverIterationCounts = (value.MinPositionIterations, value.MinVelocityIterations);
    }
}
