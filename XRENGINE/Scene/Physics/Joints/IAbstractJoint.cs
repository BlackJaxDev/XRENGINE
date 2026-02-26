using System.Numerics;

namespace XREngine.Scene.Physics.Joints
{
    /// <summary>
    /// Backend-neutral base interface for all physics joints/constraints.
    /// Implementations are provided by physics backends (PhysX, Jolt, etc.).
    /// </summary>
    public interface IAbstractJoint
    {
        /// <summary>
        /// Local frame (anchor) for actor A.
        /// </summary>
        JointAnchor LocalFrameA { get; set; }

        /// <summary>
        /// Local frame (anchor) for actor B.
        /// </summary>
        JointAnchor LocalFrameB { get; set; }

        /// <summary>
        /// Break force threshold. When the constraint force exceeds this, the joint breaks.
        /// </summary>
        float BreakForce { get; set; }

        /// <summary>
        /// Break torque threshold. When the constraint torque exceeds this, the joint breaks.
        /// </summary>
        float BreakTorque { get; set; }

        /// <summary>
        /// Relative linear velocity between the two constrained bodies.
        /// </summary>
        Vector3 RelativeLinearVelocity { get; }

        /// <summary>
        /// Relative angular velocity between the two constrained bodies.
        /// </summary>
        Vector3 RelativeAngularVelocity { get; }

        /// <summary>
        /// Inverse mass scale for actor A.
        /// </summary>
        float InvMassScaleA { get; set; }

        /// <summary>
        /// Inverse mass scale for actor B.
        /// </summary>
        float InvMassScaleB { get; set; }

        /// <summary>
        /// Inverse inertia scale for actor A.
        /// </summary>
        float InvInertiaScaleA { get; set; }

        /// <summary>
        /// Inverse inertia scale for actor B.
        /// </summary>
        float InvInertiaScaleB { get; set; }

        /// <summary>
        /// Whether collision between the two connected bodies is enabled.
        /// </summary>
        bool EnableCollision { get; set; }

        /// <summary>
        /// Whether constraint preprocessing/projection is enabled.
        /// </summary>
        bool EnablePreprocessing { get; set; }

        /// <summary>
        /// Releases the native joint resources.
        /// </summary>
        void Release();
    }

    /// <summary>
    /// A joint that rigidly connects two actors with no degrees of freedom.
    /// </summary>
    public interface IAbstractFixedJoint : IAbstractJoint
    {
    }

    /// <summary>
    /// A joint that constrains the distance between two anchor points.
    /// </summary>
    public interface IAbstractDistanceJoint : IAbstractJoint
    {
        /// <summary>
        /// Current distance between the two anchor points.
        /// </summary>
        float Distance { get; }

        float MinDistance { get; set; }
        float MaxDistance { get; set; }

        /// <summary>
        /// Whether the minimum distance limit is enforced.
        /// </summary>
        bool EnableMinDistance { get; set; }

        /// <summary>
        /// Whether the maximum distance limit is enforced.
        /// </summary>
        bool EnableMaxDistance { get; set; }

        float Stiffness { get; set; }
        float Damping { get; set; }
        float Tolerance { get; set; }
    }

    /// <summary>
    /// A hinge joint (revolute) that allows rotation around a single axis.
    /// </summary>
    public interface IAbstractHingeJoint : IAbstractJoint
    {
        /// <summary>
        /// Current angle of the hinge in radians.
        /// </summary>
        float AngleRadians { get; }

        /// <summary>
        /// Current angular velocity of the hinge.
        /// </summary>
        float Velocity { get; }

        bool EnableLimit { get; set; }
        JointAngularLimitPair Limit { get; set; }

        bool EnableDrive { get; set; }
        float DriveVelocity { get; set; }
        float DriveForceLimit { get; set; }
        float DriveGearRatio { get; set; }
        bool DriveIsFreeSpin { get; set; }
    }

    /// <summary>
    /// A prismatic (slider) joint that allows translation along a single axis.
    /// </summary>
    public interface IAbstractPrismaticJoint : IAbstractJoint
    {
        /// <summary>
        /// Current position along the slider axis.
        /// </summary>
        float Position { get; }

        /// <summary>
        /// Current velocity along the slider axis.
        /// </summary>
        float Velocity { get; }

        bool EnableLimit { get; set; }
        JointLinearLimitPair Limit { get; set; }
    }

    /// <summary>
    /// A spherical (ball-and-socket) joint with optional cone limit.
    /// </summary>
    public interface IAbstractSphericalJoint : IAbstractJoint
    {
        float SwingYAngle { get; }
        float SwingZAngle { get; }

        bool EnableLimitCone { get; set; }
        JointLimitCone LimitCone { get; set; }
    }

    /// <summary>
    /// A fully configurable 6-degree-of-freedom joint.
    /// Each axis can be locked, limited, or free, with drives and limits.
    /// </summary>
    public interface IAbstractD6Joint : IAbstractJoint
    {
        float TwistAngle { get; }
        float SwingYAngle { get; }
        float SwingZAngle { get; }

        JointMotion GetMotion(JointD6Axis axis);
        void SetMotion(JointD6Axis axis, JointMotion motion);

        JointLinearLimit DistanceLimit { get; set; }
        JointLinearLimitPair GetLinearLimit(JointD6Axis axis);
        void SetLinearLimit(JointD6Axis axis, JointLinearLimitPair limit);
        JointAngularLimitPair TwistLimit { get; set; }
        JointLimitCone SwingLimit { get; set; }

        JointDrive GetDrive(JointD6DriveType driveType);
        void SetDrive(JointD6DriveType driveType, JointDrive drive);

        (Vector3 position, Quaternion rotation) DrivePosition { get; set; }
        (Vector3 linear, Vector3 angular) DriveVelocity { get; set; }

        float ProjectionLinearTolerance { get; set; }
        float ProjectionAngularTolerance { get; set; }
    }
}
