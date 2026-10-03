using JoltPhysicsSharp;
using System.Numerics;
using XREngine.Scene.Physics.Joints;

namespace XREngine.Scene.Physics.Jolt
{
    internal abstract class JoltJointBase : IAbstractJoint
    {
        protected readonly JoltScene Scene;
        protected readonly Constraint Constraint;
        protected readonly BodyID BodyA;
        protected readonly BodyID BodyB;

        private JointAnchor _localFrameA;
        private JointAnchor _localFrameB;
        private float _breakForce = float.MaxValue;
        private float _breakTorque = float.MaxValue;
        private float _invMassScaleA = 1.0f;
        private float _invMassScaleB = 1.0f;
        private float _invInertiaScaleA = 1.0f;
        private float _invInertiaScaleB = 1.0f;
        private bool _enableCollision;
        private bool _enablePreprocessing = true;

        protected JoltJointBase(
            JoltScene scene,
            Constraint constraint,
            BodyID bodyA,
            BodyID bodyB,
            JointAnchor localFrameA,
            JointAnchor localFrameB)
        {
            Scene = scene;
            Constraint = constraint;
            BodyA = bodyA;
            BodyB = bodyB;
            _localFrameA = localFrameA;
            _localFrameB = localFrameB;
        }

        internal Constraint NativeConstraint => Constraint;

        public JointAnchor LocalFrameA
        {
            get => _localFrameA;
            set => _localFrameA = value;
        }

        public JointAnchor LocalFrameB
        {
            get => _localFrameB;
            set => _localFrameB = value;
        }

        public float BreakForce
        {
            get => _breakForce;
            set => _breakForce = value;
        }

        public float BreakTorque
        {
            get => _breakTorque;
            set => _breakTorque = value;
        }

        public Vector3 RelativeLinearVelocity
            => Scene.GetBodyLinearVelocity(BodyA) - Scene.GetBodyLinearVelocity(BodyB);

        public Vector3 RelativeAngularVelocity
            => Scene.GetBodyAngularVelocity(BodyA) - Scene.GetBodyAngularVelocity(BodyB);

        public float InvMassScaleA
        {
            get => _invMassScaleA;
            set => _invMassScaleA = value;
        }

        public float InvMassScaleB
        {
            get => _invMassScaleB;
            set => _invMassScaleB = value;
        }

        public float InvInertiaScaleA
        {
            get => _invInertiaScaleA;
            set => _invInertiaScaleA = value;
        }

        public float InvInertiaScaleB
        {
            get => _invInertiaScaleB;
            set => _invInertiaScaleB = value;
        }

        public bool EnableCollision
        {
            get => _enableCollision;
            set => _enableCollision = value;
        }

        public bool EnablePreprocessing
        {
            get => _enablePreprocessing;
            set => _enablePreprocessing = value;
        }

        public void Release()
        {
            Scene.RemoveJoint(this);
        }
    }

    internal sealed class JoltFixedJoint : JoltJointBase, IAbstractFixedJoint
    {
        public JoltFixedJoint(JoltScene scene, FixedConstraint constraint, BodyID bodyA, BodyID bodyB, JointAnchor localFrameA, JointAnchor localFrameB)
            : base(scene, constraint, bodyA, bodyB, localFrameA, localFrameB)
        {
        }
    }

    internal sealed class JoltDistanceJoint : JoltJointBase, IAbstractDistanceJoint
    {
        private readonly DistanceConstraint _constraint;
        private float _minDistance;
        private float _maxDistance = float.MaxValue;

        public JoltDistanceJoint(JoltScene scene, DistanceConstraint constraint, BodyID bodyA, BodyID bodyB, JointAnchor localFrameA, JointAnchor localFrameB)
            : base(scene, constraint, bodyA, bodyB, localFrameA, localFrameB)
        {
            _constraint = constraint;
        }

        public float Distance => Vector3.Distance(Scene.GetBodyPosition(BodyA), Scene.GetBodyPosition(BodyB));

        public float MinDistance
        {
            get => _minDistance;
            set
            {
                _minDistance = value;
                _constraint.SetDistance(_minDistance, _maxDistance);
            }
        }

        public float MaxDistance
        {
            get => _maxDistance;
            set
            {
                _maxDistance = value;
                _constraint.SetDistance(_minDistance, _maxDistance);
            }
        }

        public bool EnableMinDistance { get; set; }
        public bool EnableMaxDistance { get; set; }

        public float Stiffness
        {
            get => _constraint.SpringSettings.FrequencyOrStiffness;
            set
            {
                var spring = _constraint.SpringSettings;
                spring.Mode = SpringMode.StiffnessAndDamping;
                spring.FrequencyOrStiffness = value;
                _constraint.SpringSettings = spring;
            }
        }

        public float Damping
        {
            get => _constraint.SpringSettings.Damping;
            set
            {
                var spring = _constraint.SpringSettings;
                spring.Mode = SpringMode.StiffnessAndDamping;
                spring.Damping = value;
                _constraint.SpringSettings = spring;
            }
        }

        public float Tolerance { get; set; } = 0.025f;
    }

    internal sealed class JoltHingeJoint : JoltJointBase, IAbstractHingeJoint
    {
        private readonly HingeConstraint _constraint;
        private JointAngularLimitPair _limit;

        public JoltHingeJoint(JoltScene scene, HingeConstraint constraint, BodyID bodyA, BodyID bodyB, JointAnchor localFrameA, JointAnchor localFrameB)
            : base(scene, constraint, bodyA, bodyB, localFrameA, localFrameB)
        {
            _constraint = constraint;
            _limit = new JointAngularLimitPair(_constraint.LimitsMin, _constraint.LimitsMax);
        }

        public float AngleRadians => _constraint.CurrentAngle;

        public float Velocity
        {
            get
            {
                Vector3 rel = RelativeAngularVelocity;
                Vector3 axis = Vector3.Normalize(_constraint.LocalSpaceHingeAxis1);
                return Vector3.Dot(rel, axis);
            }
        }

        public bool EnableLimit
        {
            get => _constraint.HasLimits;
            set
            {
                if (value)
                    _constraint.SetLimits(_limit.LowerRadians, _limit.UpperRadians);
                else
                    _constraint.SetLimits(float.NegativeInfinity, float.PositiveInfinity);
            }
        }

        public JointAngularLimitPair Limit
        {
            get => _limit;
            set
            {
                _limit = value;
                _constraint.SetLimits(value.LowerRadians, value.UpperRadians);
                var spring = _constraint.LimitsSpringSettings;
                spring.Mode = SpringMode.StiffnessAndDamping;
                spring.FrequencyOrStiffness = value.Stiffness;
                spring.Damping = value.Damping;
                _constraint.LimitsSpringSettings = spring;
            }
        }

        public bool EnableDrive
        {
            get => _constraint.MotorState != MotorState.Off;
            set => _constraint.MotorState = value ? MotorState.Velocity : MotorState.Off;
        }

        public float DriveVelocity
        {
            get => _constraint.TargetAngularVelocity;
            set => _constraint.TargetAngularVelocity = value;
        }

        public float DriveForceLimit
        {
            get => _constraint.MotorSettings.MaxTorqueLimit;
            set
            {
                var motor = _constraint.MotorSettings;
                motor.SetTorqueLimit(value);
                _constraint.MotorSettings = motor;
            }
        }

        public float DriveGearRatio { get; set; } = 1.0f;

        public bool DriveIsFreeSpin
        {
            get => _constraint.MotorState == MotorState.Velocity;
            set => _constraint.MotorState = value ? MotorState.Velocity : MotorState.Position;
        }
    }

    internal sealed class JoltPrismaticJoint : JoltJointBase, IAbstractPrismaticJoint
    {
        private readonly SliderConstraint _constraint;
        private JointLinearLimitPair _limit;

        public JoltPrismaticJoint(JoltScene scene, SliderConstraint constraint, BodyID bodyA, BodyID bodyB, JointAnchor localFrameA, JointAnchor localFrameB)
            : base(scene, constraint, bodyA, bodyB, localFrameA, localFrameB)
        {
            _constraint = constraint;
            _limit = new JointLinearLimitPair(_constraint.LimitsMin, _constraint.LimitsMax);
        }

        public float Position => _constraint.CurrentPosition;

        public float Velocity
        {
            get
            {
                Vector3 rel = RelativeLinearVelocity;
                Vector3 axis = Vector3.Normalize(_constraint.Settings.SliderAxis1);
                return Vector3.Dot(rel, axis);
            }
        }

        public bool EnableLimit
        {
            get => _constraint.HasLimits;
            set
            {
                if (value)
                    _constraint.SetLimits(_limit.Lower, _limit.Upper);
                else
                    _constraint.SetLimits(float.NegativeInfinity, float.PositiveInfinity);
            }
        }

        public JointLinearLimitPair Limit
        {
            get => _limit;
            set
            {
                _limit = value;
                _constraint.SetLimits(value.Lower, value.Upper);
                var spring = _constraint.LimitsSpringSettings;
                spring.Mode = SpringMode.StiffnessAndDamping;
                spring.FrequencyOrStiffness = value.Stiffness;
                spring.Damping = value.Damping;
                _constraint.LimitsSpringSettings = spring;
            }
        }
    }

    internal sealed class JoltSphericalJoint : JoltJointBase, IAbstractSphericalJoint
    {
        private readonly SwingTwistConstraint _constraint;
        private JointLimitCone _limit;

        public JoltSphericalJoint(JoltScene scene, SwingTwistConstraint constraint, BodyID bodyA, BodyID bodyB, JointAnchor localFrameA, JointAnchor localFrameB)
            : base(scene, constraint, bodyA, bodyB, localFrameA, localFrameB)
        {
            _constraint = constraint;
            _limit = new JointLimitCone(constraint.NormalHalfConeAngle, constraint.NormalHalfConeAngle);
        }

        public float SwingYAngle => _constraint.TotalLambdaSwingY;
        public float SwingZAngle => _constraint.TotalLambdaSwingZ;

        public bool EnableLimitCone
        {
            get => _limit.YAngleRadians > 0.0f || _limit.ZAngleRadians > 0.0f;
            set
            {
                if (!value)
                {
                    _limit = new JointLimitCone(0.0f, 0.0f, _limit.Stiffness, _limit.Damping, _limit.Restitution, _limit.BounceThreshold);
                }
                else
                {
                    // Runtime mutation is backend-limited in current Jolt API bindings.
                    // Keep cached value for serialization/editor parity.
                }
            }
        }

        public JointLimitCone LimitCone
        {
            get => _limit;
            set => _limit = value;
        }
    }

    internal sealed class JoltD6Joint : JoltJointBase, IAbstractD6Joint
    {
        private readonly SixDOFConstraint _constraint;
        private readonly JointMotion[] _motion = new JointMotion[6];
        private readonly JointLinearLimitPair[] _linearLimits = new JointLinearLimitPair[3];
        private readonly JointDrive[] _drives = new JointDrive[6];

        private JointAngularLimitPair _twistLimit;
        private JointLimitCone _swingLimit;
        private JointLinearLimit _distanceLimit = new(float.MaxValue);
        private (Vector3 position, Quaternion rotation) _drivePosition = (Vector3.Zero, Quaternion.Identity);
        private (Vector3 linear, Vector3 angular) _driveVelocity = (Vector3.Zero, Vector3.Zero);
        private float _projectionLinearTolerance = 1e10f;
        private float _projectionAngularTolerance = float.Pi;

        public JoltD6Joint(JoltScene scene, SixDOFConstraint constraint, BodyID bodyA, BodyID bodyB, JointAnchor localFrameA, JointAnchor localFrameB)
            : base(scene, constraint, bodyA, bodyB, localFrameA, localFrameB)
        {
            _constraint = constraint;
            for (int i = 0; i < _motion.Length; i++)
                _motion[i] = JointMotion.Locked;
        }

        public float TwistAngle => _constraint.TotalLambdaRotation.X;
        public float SwingYAngle => _constraint.TotalLambdaRotation.Y;
        public float SwingZAngle => _constraint.TotalLambdaRotation.Z;

        public JointMotion GetMotion(JointD6Axis axis) => _motion[(int)axis];

        public void SetMotion(JointD6Axis axis, JointMotion motion)
        {
            _motion[(int)axis] = motion;
            var settings = _constraint.Settings;
            var joltAxis = MapAxis(axis);
            switch (motion)
            {
                case JointMotion.Free:
                    settings.MakeFreeAxis(joltAxis);
                    break;
                case JointMotion.Locked:
                    settings.MakeFixedAxis(joltAxis);
                    break;
                case JointMotion.Limited:
                    settings.SetLimitedAxis(joltAxis, -1.0f, 1.0f);
                    break;
            }
        }

        public JointLinearLimit DistanceLimit
        {
            get => _distanceLimit;
            set => _distanceLimit = value;
        }

        public JointLinearLimitPair GetLinearLimit(JointD6Axis axis)
        {
            int idx = axis switch
            {
                JointD6Axis.X => 0,
                JointD6Axis.Y => 1,
                JointD6Axis.Z => 2,
                _ => 0,
            };
            return _linearLimits[idx];
        }

        public void SetLinearLimit(JointD6Axis axis, JointLinearLimitPair limit)
        {
            int idx = axis switch
            {
                JointD6Axis.X => 0,
                JointD6Axis.Y => 1,
                JointD6Axis.Z => 2,
                _ => 0,
            };
            _linearLimits[idx] = limit;

            var settings = _constraint.Settings;
            settings.SetLimitedAxis(MapAxis(axis), limit.Lower, limit.Upper);
        }

        public JointAngularLimitPair TwistLimit
        {
            get => _twistLimit;
            set => _twistLimit = value;
        }

        public JointLimitCone SwingLimit
        {
            get => _swingLimit;
            set => _swingLimit = value;
        }

        public JointDrive GetDrive(JointD6DriveType driveType) => _drives[(int)driveType];

        public void SetDrive(JointD6DriveType driveType, JointDrive drive)
        {
            _drives[(int)driveType] = drive;
            var settings = _constraint.Settings;
            if ((int)driveType < settings.MotorSettings.Length)
            {
                var motor = settings.MotorSettings[(int)driveType];
                motor.SpringSettings.Mode = SpringMode.StiffnessAndDamping;
                motor.SpringSettings.FrequencyOrStiffness = drive.Stiffness;
                motor.SpringSettings.Damping = drive.Damping;
                motor.SetForceLimit(drive.ForceLimit);
                settings.MotorSettings[(int)driveType] = motor;
            }
        }

        public (Vector3 position, Quaternion rotation) DrivePosition
        {
            get => _drivePosition;
            set => _drivePosition = value;
        }

        public (Vector3 linear, Vector3 angular) DriveVelocity
        {
            get => _driveVelocity;
            set => _driveVelocity = value;
        }

        public float ProjectionLinearTolerance
        {
            get => _projectionLinearTolerance;
            set => _projectionLinearTolerance = value;
        }

        public float ProjectionAngularTolerance
        {
            get => _projectionAngularTolerance;
            set => _projectionAngularTolerance = value;
        }

        private static SixDOFConstraintAxis MapAxis(JointD6Axis axis)
            => axis switch
            {
                JointD6Axis.X => SixDOFConstraintAxis.TranslationX,
                JointD6Axis.Y => SixDOFConstraintAxis.TranslationY,
                JointD6Axis.Z => SixDOFConstraintAxis.TranslationZ,
                JointD6Axis.Twist => SixDOFConstraintAxis.RotationX,
                JointD6Axis.Swing1 => SixDOFConstraintAxis.RotationY,
                JointD6Axis.Swing2 => SixDOFConstraintAxis.RotationZ,
                _ => SixDOFConstraintAxis.TranslationX,
            };
    }
}
