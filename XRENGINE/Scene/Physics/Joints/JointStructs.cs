using System.Numerics;

namespace XREngine.Scene.Physics.Joints
{
    /// <summary>
    /// Backend-neutral representation of a linear joint limit (single value, e.g. distance).
    /// </summary>
    public struct JointLinearLimit
    {
        public float Value;
        public float Restitution;
        public float BounceThreshold;
        public float Stiffness;
        public float Damping;

        public JointLinearLimit(float value, float stiffness = 0f, float damping = 0f, float restitution = 0f, float bounceThreshold = 0f)
        {
            Value = value;
            Stiffness = stiffness;
            Damping = damping;
            Restitution = restitution;
            BounceThreshold = bounceThreshold;
        }
    }

    /// <summary>
    /// Backend-neutral representation of a linear limit pair (lower + upper).
    /// </summary>
    public struct JointLinearLimitPair
    {
        public float Lower;
        public float Upper;
        public float Restitution;
        public float BounceThreshold;
        public float Stiffness;
        public float Damping;

        public JointLinearLimitPair(float lower, float upper, float stiffness = 0f, float damping = 0f, float restitution = 0f, float bounceThreshold = 0f)
        {
            Lower = lower;
            Upper = upper;
            Stiffness = stiffness;
            Damping = damping;
            Restitution = restitution;
            BounceThreshold = bounceThreshold;
        }
    }

    /// <summary>
    /// Backend-neutral representation of an angular limit pair (lower + upper, in radians).
    /// </summary>
    public struct JointAngularLimitPair
    {
        public float LowerRadians;
        public float UpperRadians;
        public float Restitution;
        public float BounceThreshold;
        public float Stiffness;
        public float Damping;

        public JointAngularLimitPair(float lowerRadians, float upperRadians, float stiffness = 0f, float damping = 0f, float restitution = 0f, float bounceThreshold = 0f)
        {
            LowerRadians = lowerRadians;
            UpperRadians = upperRadians;
            Stiffness = stiffness;
            Damping = damping;
            Restitution = restitution;
            BounceThreshold = bounceThreshold;
        }
    }

    /// <summary>
    /// Backend-neutral representation of a cone limit for spherical joints (angles in radians).
    /// </summary>
    public struct JointLimitCone
    {
        public float YAngleRadians;
        public float ZAngleRadians;
        public float Restitution;
        public float BounceThreshold;
        public float Stiffness;
        public float Damping;

        public JointLimitCone(float yAngleRadians, float zAngleRadians, float stiffness = 0f, float damping = 0f, float restitution = 0f, float bounceThreshold = 0f)
        {
            YAngleRadians = yAngleRadians;
            ZAngleRadians = zAngleRadians;
            Stiffness = stiffness;
            Damping = damping;
            Restitution = restitution;
            BounceThreshold = bounceThreshold;
        }
    }

    /// <summary>
    /// Backend-neutral representation of a joint drive (spring + damper).
    /// </summary>
    public struct JointDrive
    {
        public float Stiffness;
        public float Damping;
        public float ForceLimit;
        public bool IsAcceleration;

        public JointDrive(float stiffness, float damping, float forceLimit = float.MaxValue, bool isAcceleration = false)
        {
            Stiffness = stiffness;
            Damping = damping;
            ForceLimit = forceLimit;
            IsAcceleration = isAcceleration;
        }
    }

    /// <summary>
    /// Anchor/local frame for one side of a joint.
    /// </summary>
    public struct JointAnchor
    {
        public Vector3 Position;
        public Quaternion Rotation;

        public static JointAnchor Identity => new() { Position = Vector3.Zero, Rotation = Quaternion.Identity };

        public JointAnchor(Vector3 position, Quaternion rotation)
        {
            Position = position;
            Rotation = rotation;
        }
    }

    /// <summary>
    /// Degree-of-freedom motion type for D6 joints.
    /// </summary>
    public enum JointMotion : byte
    {
        Locked = 0,
        Limited = 1,
        Free = 2,
    }

    /// <summary>
    /// Identifies a D6 axis.
    /// </summary>
    public enum JointD6Axis : byte
    {
        X = 0,
        Y = 1,
        Z = 2,
        Twist = 3,
        Swing1 = 4,
        Swing2 = 5,
    }

    /// <summary>
    /// Identifies a D6 drive.
    /// </summary>
    public enum JointD6DriveType : byte
    {
        X = 0,
        Y = 1,
        Z = 2,
        Swing = 3,
        Twist = 4,
        Slerp = 5,
    }
}
