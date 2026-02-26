using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Scene;
using XREngine.Scene.Physics.Joints;

namespace XREngine.Components.Physics
{
    /// <summary>
    /// A fully configurable 6-degree-of-freedom joint.
    /// Each linear and angular axis can be individually locked, limited, or free.
    /// </summary>
    [Category("Physics")]
    [DisplayName("D6 Joint")]
    [Description("Fully configurable 6-DOF joint with per-axis motion, limits, and drives.")]
    [XRComponentEditor("XREngine.Editor.ComponentEditors.D6JointComponentEditor")]
    public class D6JointComponent : PhysicsJointComponent
    {
        private JointMotion _motionX = JointMotion.Locked;
        private JointMotion _motionY = JointMotion.Locked;
        private JointMotion _motionZ = JointMotion.Locked;
        private JointMotion _motionTwist = JointMotion.Locked;
        private JointMotion _motionSwing1 = JointMotion.Locked;
        private JointMotion _motionSwing2 = JointMotion.Locked;

        // Twist limit
        private float _twistLowerRadians = -float.Pi / 4f;
        private float _twistUpperRadians = float.Pi / 4f;
        private float _twistLimitRestitution;
        private float _twistLimitBounceThreshold;
        private float _twistLimitStiffness;
        private float _twistLimitDamping;

        // Swing limit (cone)
        private float _swingLimitYAngle = float.Pi / 4f;
        private float _swingLimitZAngle = float.Pi / 4f;
        private float _swingLimitRestitution;
        private float _swingLimitBounceThreshold;
        private float _swingLimitStiffness;
        private float _swingLimitDamping;

        // Linear limit (distance)
        private float _distanceLimitValue = float.MaxValue;
        private float _distanceLimitRestitution;
        private float _distanceLimitBounceThreshold;
        private float _distanceLimitStiffness;
        private float _distanceLimitDamping;

        // Linear axis limits
        private float _linearLimitXLower;
        private float _linearLimitXUpper;
        private float _linearLimitYLower;
        private float _linearLimitYUpper;
        private float _linearLimitZLower;
        private float _linearLimitZUpper;

        // Drives
        private JointDrive _driveX;
        private JointDrive _driveY;
        private JointDrive _driveZ;
        private JointDrive _driveSwing;
        private JointDrive _driveTwist;
        private JointDrive _driveSlerp;

        // Drive targets
        private Vector3 _driveTargetPosition = Vector3.Zero;
        private Quaternion _driveTargetRotation = Quaternion.Identity;
        private Vector3 _driveLinearVelocity = Vector3.Zero;
        private Vector3 _driveAngularVelocity = Vector3.Zero;

        // Projection
        private float _projectionLinearTolerance = 1e10f;
        private float _projectionAngularTolerance = float.Pi;

        #region Runtime Read-Only

        [Browsable(false)]
        public float TwistAngle => NativeJoint is IAbstractD6Joint d6 ? d6.TwistAngle : 0f;

        [Browsable(false)]
        public float SwingYAngle => NativeJoint is IAbstractD6Joint d6 ? d6.SwingYAngle : 0f;

        [Browsable(false)]
        public float SwingZAngle => NativeJoint is IAbstractD6Joint d6 ? d6.SwingZAngle : 0f;

        #endregion

        #region Motion

        [Category("Motion")]
        [DisplayName("X Motion")]
        public JointMotion MotionX
        {
            get => _motionX;
            set
            {
                if (SetField(ref _motionX, value) && NativeJoint is IAbstractD6Joint d6)
                    d6.SetMotion(JointD6Axis.X, value);
            }
        }

        [Category("Motion")]
        [DisplayName("Y Motion")]
        public JointMotion MotionY
        {
            get => _motionY;
            set
            {
                if (SetField(ref _motionY, value) && NativeJoint is IAbstractD6Joint d6)
                    d6.SetMotion(JointD6Axis.Y, value);
            }
        }

        [Category("Motion")]
        [DisplayName("Z Motion")]
        public JointMotion MotionZ
        {
            get => _motionZ;
            set
            {
                if (SetField(ref _motionZ, value) && NativeJoint is IAbstractD6Joint d6)
                    d6.SetMotion(JointD6Axis.Z, value);
            }
        }

        [Category("Motion")]
        [DisplayName("Twist Motion")]
        public JointMotion MotionTwist
        {
            get => _motionTwist;
            set
            {
                if (SetField(ref _motionTwist, value) && NativeJoint is IAbstractD6Joint d6)
                    d6.SetMotion(JointD6Axis.Twist, value);
            }
        }

        [Category("Motion")]
        [DisplayName("Swing1 Motion")]
        public JointMotion MotionSwing1
        {
            get => _motionSwing1;
            set
            {
                if (SetField(ref _motionSwing1, value) && NativeJoint is IAbstractD6Joint d6)
                    d6.SetMotion(JointD6Axis.Swing1, value);
            }
        }

        [Category("Motion")]
        [DisplayName("Swing2 Motion")]
        public JointMotion MotionSwing2
        {
            get => _motionSwing2;
            set
            {
                if (SetField(ref _motionSwing2, value) && NativeJoint is IAbstractD6Joint d6)
                    d6.SetMotion(JointD6Axis.Swing2, value);
            }
        }

        #endregion

        #region Twist Limit

        [Category("Twist Limit")]
        [DisplayName("Twist Lower (rad)")]
        public float TwistLowerRadians
        {
            get => _twistLowerRadians;
            set { if (SetField(ref _twistLowerRadians, value)) PushTwistLimit(); }
        }

        [Category("Twist Limit")]
        [DisplayName("Twist Upper (rad)")]
        public float TwistUpperRadians
        {
            get => _twistUpperRadians;
            set { if (SetField(ref _twistUpperRadians, value)) PushTwistLimit(); }
        }

        [Category("Twist Limit")]
        [DisplayName("Twist Stiffness")]
        public float TwistLimitStiffness
        {
            get => _twistLimitStiffness;
            set { if (SetField(ref _twistLimitStiffness, value)) PushTwistLimit(); }
        }

        [Category("Twist Limit")]
        [DisplayName("Twist Damping")]
        public float TwistLimitDamping
        {
            get => _twistLimitDamping;
            set { if (SetField(ref _twistLimitDamping, value)) PushTwistLimit(); }
        }

        [Category("Twist Limit")]
        [DisplayName("Twist Restitution")]
        public float TwistLimitRestitution
        {
            get => _twistLimitRestitution;
            set { if (SetField(ref _twistLimitRestitution, value)) PushTwistLimit(); }
        }

        [Category("Twist Limit")]
        [DisplayName("Twist Bounce Threshold")]
        public float TwistLimitBounceThreshold
        {
            get => _twistLimitBounceThreshold;
            set { if (SetField(ref _twistLimitBounceThreshold, value)) PushTwistLimit(); }
        }

        #endregion

        #region Swing Limit

        [Category("Swing Limit")]
        [DisplayName("Swing Y Angle (rad)")]
        public float SwingLimitYAngle
        {
            get => _swingLimitYAngle;
            set { if (SetField(ref _swingLimitYAngle, value)) PushSwingLimit(); }
        }

        [Category("Swing Limit")]
        [DisplayName("Swing Z Angle (rad)")]
        public float SwingLimitZAngle
        {
            get => _swingLimitZAngle;
            set { if (SetField(ref _swingLimitZAngle, value)) PushSwingLimit(); }
        }

        [Category("Swing Limit")]
        [DisplayName("Swing Stiffness")]
        public float SwingLimitStiffness
        {
            get => _swingLimitStiffness;
            set { if (SetField(ref _swingLimitStiffness, value)) PushSwingLimit(); }
        }

        [Category("Swing Limit")]
        [DisplayName("Swing Damping")]
        public float SwingLimitDamping
        {
            get => _swingLimitDamping;
            set { if (SetField(ref _swingLimitDamping, value)) PushSwingLimit(); }
        }

        [Category("Swing Limit")]
        [DisplayName("Swing Restitution")]
        public float SwingLimitRestitution
        {
            get => _swingLimitRestitution;
            set { if (SetField(ref _swingLimitRestitution, value)) PushSwingLimit(); }
        }

        [Category("Swing Limit")]
        [DisplayName("Swing Bounce Threshold")]
        public float SwingLimitBounceThreshold
        {
            get => _swingLimitBounceThreshold;
            set { if (SetField(ref _swingLimitBounceThreshold, value)) PushSwingLimit(); }
        }

        #endregion

        #region Distance Limit

        [Category("Distance Limit")]
        [DisplayName("Distance Limit Value")]
        public float DistanceLimitValue
        {
            get => _distanceLimitValue;
            set { if (SetField(ref _distanceLimitValue, value)) PushDistanceLimit(); }
        }

        [Category("Distance Limit")]
        [DisplayName("Distance Stiffness")]
        public float DistanceLimitStiffness
        {
            get => _distanceLimitStiffness;
            set { if (SetField(ref _distanceLimitStiffness, value)) PushDistanceLimit(); }
        }

        [Category("Distance Limit")]
        [DisplayName("Distance Damping")]
        public float DistanceLimitDamping
        {
            get => _distanceLimitDamping;
            set { if (SetField(ref _distanceLimitDamping, value)) PushDistanceLimit(); }
        }

        #endregion

        #region Linear Axis Limits

        [Category("Linear Limits")]
        [DisplayName("X Lower")]
        public float LinearLimitXLower { get => _linearLimitXLower; set { if (SetField(ref _linearLimitXLower, value)) PushLinearLimit(JointD6Axis.X); } }

        [Category("Linear Limits")]
        [DisplayName("X Upper")]
        public float LinearLimitXUpper { get => _linearLimitXUpper; set { if (SetField(ref _linearLimitXUpper, value)) PushLinearLimit(JointD6Axis.X); } }

        [Category("Linear Limits")]
        [DisplayName("Y Lower")]
        public float LinearLimitYLower { get => _linearLimitYLower; set { if (SetField(ref _linearLimitYLower, value)) PushLinearLimit(JointD6Axis.Y); } }

        [Category("Linear Limits")]
        [DisplayName("Y Upper")]
        public float LinearLimitYUpper { get => _linearLimitYUpper; set { if (SetField(ref _linearLimitYUpper, value)) PushLinearLimit(JointD6Axis.Y); } }

        [Category("Linear Limits")]
        [DisplayName("Z Lower")]
        public float LinearLimitZLower { get => _linearLimitZLower; set { if (SetField(ref _linearLimitZLower, value)) PushLinearLimit(JointD6Axis.Z); } }

        [Category("Linear Limits")]
        [DisplayName("Z Upper")]
        public float LinearLimitZUpper { get => _linearLimitZUpper; set { if (SetField(ref _linearLimitZUpper, value)) PushLinearLimit(JointD6Axis.Z); } }

        #endregion

        #region Drives

        [Category("Drives")]
        [DisplayName("Drive X")]
        public JointDrive DriveX { get => _driveX; set { if (SetField(ref _driveX, value)) PushDrive(JointD6DriveType.X); } }

        [Category("Drives")]
        [DisplayName("Drive Y")]
        public JointDrive DriveY { get => _driveY; set { if (SetField(ref _driveY, value)) PushDrive(JointD6DriveType.Y); } }

        [Category("Drives")]
        [DisplayName("Drive Z")]
        public JointDrive DriveZ { get => _driveZ; set { if (SetField(ref _driveZ, value)) PushDrive(JointD6DriveType.Z); } }

        [Category("Drives")]
        [DisplayName("Drive Swing")]
        public JointDrive DriveSwing { get => _driveSwing; set { if (SetField(ref _driveSwing, value)) PushDrive(JointD6DriveType.Swing); } }

        [Category("Drives")]
        [DisplayName("Drive Twist")]
        public JointDrive DriveTwist { get => _driveTwist; set { if (SetField(ref _driveTwist, value)) PushDrive(JointD6DriveType.Twist); } }

        [Category("Drives")]
        [DisplayName("Drive Slerp")]
        public JointDrive DriveSlerp { get => _driveSlerp; set { if (SetField(ref _driveSlerp, value)) PushDrive(JointD6DriveType.Slerp); } }

        #endregion

        #region Drive Targets

        [Category("Drive Target")]
        [DisplayName("Target Position")]
        public Vector3 DriveTargetPosition
        {
            get => _driveTargetPosition;
            set
            {
                if (SetField(ref _driveTargetPosition, value) && NativeJoint is IAbstractD6Joint d6)
                    d6.DrivePosition = (value, _driveTargetRotation);
            }
        }

        [Category("Drive Target")]
        [DisplayName("Target Rotation")]
        public Quaternion DriveTargetRotation
        {
            get => _driveTargetRotation;
            set
            {
                if (SetField(ref _driveTargetRotation, value) && NativeJoint is IAbstractD6Joint d6)
                    d6.DrivePosition = (_driveTargetPosition, value);
            }
        }

        [Category("Drive Target")]
        [DisplayName("Linear Velocity")]
        public Vector3 DriveLinearVelocity
        {
            get => _driveLinearVelocity;
            set
            {
                if (SetField(ref _driveLinearVelocity, value) && NativeJoint is IAbstractD6Joint d6)
                    d6.DriveVelocity = (value, _driveAngularVelocity);
            }
        }

        [Category("Drive Target")]
        [DisplayName("Angular Velocity")]
        public Vector3 DriveAngularVelocity
        {
            get => _driveAngularVelocity;
            set
            {
                if (SetField(ref _driveAngularVelocity, value) && NativeJoint is IAbstractD6Joint d6)
                    d6.DriveVelocity = (_driveLinearVelocity, value);
            }
        }

        #endregion

        #region Projection

        [Category("Projection")]
        [DisplayName("Linear Tolerance")]
        public float ProjectionLinearTolerance
        {
            get => _projectionLinearTolerance;
            set
            {
                if (SetField(ref _projectionLinearTolerance, value) && NativeJoint is IAbstractD6Joint d6)
                    d6.ProjectionLinearTolerance = value;
            }
        }

        [Category("Projection")]
        [DisplayName("Angular Tolerance")]
        public float ProjectionAngularTolerance
        {
            get => _projectionAngularTolerance;
            set
            {
                if (SetField(ref _projectionAngularTolerance, value) && NativeJoint is IAbstractD6Joint d6)
                    d6.ProjectionAngularTolerance = value;
            }
        }

        #endregion

        protected override IAbstractJoint? CreateJointImpl(
            AbstractPhysicsScene scene,
            IAbstractPhysicsActor? actorA, JointAnchor localFrameA,
            IAbstractPhysicsActor? actorB, JointAnchor localFrameB)
            => scene.CreateD6Joint(actorA, localFrameA, actorB, localFrameB);

        protected override void ApplyJointProperties(IAbstractJoint joint)
        {
            if (joint is not IAbstractD6Joint d6)
                return;

            // Motion axes
            d6.SetMotion(JointD6Axis.X, _motionX);
            d6.SetMotion(JointD6Axis.Y, _motionY);
            d6.SetMotion(JointD6Axis.Z, _motionZ);
            d6.SetMotion(JointD6Axis.Twist, _motionTwist);
            d6.SetMotion(JointD6Axis.Swing1, _motionSwing1);
            d6.SetMotion(JointD6Axis.Swing2, _motionSwing2);

            // Limits
            d6.TwistLimit = new JointAngularLimitPair(
                _twistLowerRadians, _twistUpperRadians,
                _twistLimitStiffness, _twistLimitDamping,
                _twistLimitRestitution, _twistLimitBounceThreshold);

            d6.SwingLimit = new JointLimitCone(
                _swingLimitYAngle, _swingLimitZAngle,
                _swingLimitStiffness, _swingLimitDamping,
                _swingLimitRestitution, _swingLimitBounceThreshold);

            d6.DistanceLimit = new JointLinearLimit(
                _distanceLimitValue,
                _distanceLimitStiffness, _distanceLimitDamping,
                _distanceLimitRestitution, _distanceLimitBounceThreshold);

            d6.SetLinearLimit(JointD6Axis.X, new JointLinearLimitPair(_linearLimitXLower, _linearLimitXUpper));
            d6.SetLinearLimit(JointD6Axis.Y, new JointLinearLimitPair(_linearLimitYLower, _linearLimitYUpper));
            d6.SetLinearLimit(JointD6Axis.Z, new JointLinearLimitPair(_linearLimitZLower, _linearLimitZUpper));

            // Drives
            d6.SetDrive(JointD6DriveType.X, _driveX);
            d6.SetDrive(JointD6DriveType.Y, _driveY);
            d6.SetDrive(JointD6DriveType.Z, _driveZ);
            d6.SetDrive(JointD6DriveType.Swing, _driveSwing);
            d6.SetDrive(JointD6DriveType.Twist, _driveTwist);
            d6.SetDrive(JointD6DriveType.Slerp, _driveSlerp);

            // Drive targets
            d6.DrivePosition = (_driveTargetPosition, _driveTargetRotation);
            d6.DriveVelocity = (_driveLinearVelocity, _driveAngularVelocity);

            // Projection
            d6.ProjectionLinearTolerance = _projectionLinearTolerance;
            d6.ProjectionAngularTolerance = _projectionAngularTolerance;
        }

        #region Push Helpers

        private void PushTwistLimit()
        {
            if (NativeJoint is not IAbstractD6Joint d6) return;
            d6.TwistLimit = new JointAngularLimitPair(
                _twistLowerRadians, _twistUpperRadians,
                _twistLimitStiffness, _twistLimitDamping,
                _twistLimitRestitution, _twistLimitBounceThreshold);
        }

        private void PushSwingLimit()
        {
            if (NativeJoint is not IAbstractD6Joint d6) return;
            d6.SwingLimit = new JointLimitCone(
                _swingLimitYAngle, _swingLimitZAngle,
                _swingLimitStiffness, _swingLimitDamping,
                _swingLimitRestitution, _swingLimitBounceThreshold);
        }

        private void PushDistanceLimit()
        {
            if (NativeJoint is not IAbstractD6Joint d6) return;
            d6.DistanceLimit = new JointLinearLimit(
                _distanceLimitValue,
                _distanceLimitStiffness, _distanceLimitDamping,
                _distanceLimitRestitution, _distanceLimitBounceThreshold);
        }

        private void PushLinearLimit(JointD6Axis axis)
        {
            if (NativeJoint is not IAbstractD6Joint d6) return;
            var pair = axis switch
            {
                JointD6Axis.X => new JointLinearLimitPair(_linearLimitXLower, _linearLimitXUpper),
                JointD6Axis.Y => new JointLinearLimitPair(_linearLimitYLower, _linearLimitYUpper),
                JointD6Axis.Z => new JointLinearLimitPair(_linearLimitZLower, _linearLimitZUpper),
                _ => default,
            };
            d6.SetLinearLimit(axis, pair);
        }

        private void PushDrive(JointD6DriveType driveType)
        {
            if (NativeJoint is not IAbstractD6Joint d6) return;
            var drive = driveType switch
            {
                JointD6DriveType.X => _driveX,
                JointD6DriveType.Y => _driveY,
                JointD6DriveType.Z => _driveZ,
                JointD6DriveType.Swing => _driveSwing,
                JointD6DriveType.Twist => _driveTwist,
                JointD6DriveType.Slerp => _driveSlerp,
                _ => default,
            };
            d6.SetDrive(driveType, drive);
        }

        #endregion
    }
}
