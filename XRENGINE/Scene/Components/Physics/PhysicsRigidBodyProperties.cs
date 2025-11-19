using System;
using System.Numerics;

namespace XREngine.Components.Physics
{
    [Flags]
    public enum PhysicsRigidBodyFlags
    {
        None = 0,
        Kinematic = 1 << 0,
        UseKinematicTargetForQueries = 1 << 1,
        EnableCcd = 1 << 2,
        EnableSpeculativeCcd = 1 << 3,
        EnableCcdMaxContactImpulse = 1 << 4,
        EnableCcdFriction = 1 << 5,
    }

    [Flags]
    public enum PhysicsLockFlags
    {
        None = 0,
        LinearX = 1 << 0,
        LinearY = 1 << 1,
        LinearZ = 1 << 2,
        AngularX = 1 << 3,
        AngularY = 1 << 4,
        AngularZ = 1 << 5,
    }

    public struct PhysicsGroupsMask(uint word0, uint word1, uint word2, uint word3)
    {
        public uint Word0 = word0;
        public uint Word1 = word1;
        public uint Word2 = word2;
        public uint Word3 = word3;

        public static PhysicsGroupsMask Empty => new(0, 0, 0, 0);
    }

    public struct PhysicsMassFrame
    {
        public Vector3 Translation;
        public Quaternion Rotation;

        public PhysicsMassFrame(Vector3 translation, Quaternion rotation)
        {
            Translation = translation;
            Rotation = rotation;
        }

        public static PhysicsMassFrame Identity => new(Vector3.Zero, Quaternion.Identity);
    }

    public struct PhysicsSolverIterations
    {
        public uint MinPositionIterations;
        public uint MinVelocityIterations;

        public PhysicsSolverIterations(uint positions, uint velocities)
        {
            MinPositionIterations = positions;
            MinVelocityIterations = velocities;
        }

        public static PhysicsSolverIterations Default => new(8, 2);
    }
}
