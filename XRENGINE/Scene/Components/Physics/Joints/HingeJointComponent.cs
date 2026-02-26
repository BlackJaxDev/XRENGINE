using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Scene;
using XREngine.Scene.Physics.Joints;

namespace XREngine.Components.Physics
{
    /// <summary>
    /// A hinge (revolute) joint that allows rotation around a single axis.
    /// </summary>
    [Category("Physics")]
    [DisplayName("Hinge Joint")]
    [Description("Allows rotation around a single axis with optional limits and motor.")]
    [XRComponentEditor("XREngine.Editor.ComponentEditors.HingeJointComponentEditor")]
    public class HingeJointComponent : PhysicsJointComponent
    {
        private bool _enableLimit;
        private float _lowerAngleRadians = -float.Pi / 4f;
        private float _upperAngleRadians = float.Pi / 4f;
        private float _limitRestitution;
        private float _limitBounceThreshold;
        private float _limitStiffness;
        private float _limitDamping;
        private bool _enableDrive;
        private float _driveVelocity;
        private float _driveForceLimit = float.MaxValue;
        private float _driveGearRatio = 1f;
        private bool _driveIsFreeSpin;

        /// <summary>
        /// Current hinge angle in radians (read-only runtime value).
        /// </summary>
        [Browsable(false)]
        public float AngleRadians => NativeJoint is IAbstractHingeJoint hj ? hj.AngleRadians : 0f;

        /// <summary>
        /// Current angular velocity of the hinge (read-only runtime value).
        /// </summary>
        [Browsable(false)]
        public float Velocity => NativeJoint is IAbstractHingeJoint hj ? hj.Velocity : 0f;

        [Category("Limits")]
        [DisplayName("Enable Limit")]
        [Description("Enable angular limits on the hinge.")]
        public bool EnableLimit
        {
            get => _enableLimit;
            set
            {
                if (SetField(ref _enableLimit, value) && NativeJoint is IAbstractHingeJoint hj)
                    hj.EnableLimit = value;
            }
        }

        [Category("Limits")]
        [DisplayName("Lower Angle (rad)")]
        [Description("Lower angular limit in radians.")]
        public float LowerAngleRadians
        {
            get => _lowerAngleRadians;
            set
            {
                if (SetField(ref _lowerAngleRadians, value))
                    PushLimits();
            }
        }

        [Category("Limits")]
        [DisplayName("Upper Angle (rad)")]
        [Description("Upper angular limit in radians.")]
        public float UpperAngleRadians
        {
            get => _upperAngleRadians;
            set
            {
                if (SetField(ref _upperAngleRadians, value))
                    PushLimits();
            }
        }

        [Category("Limits")]
        [DisplayName("Limit Restitution")]
        public float LimitRestitution
        {
            get => _limitRestitution;
            set
            {
                if (SetField(ref _limitRestitution, value))
                    PushLimits();
            }
        }

        [Category("Limits")]
        [DisplayName("Limit Bounce Threshold")]
        public float LimitBounceThreshold
        {
            get => _limitBounceThreshold;
            set
            {
                if (SetField(ref _limitBounceThreshold, value))
                    PushLimits();
            }
        }

        [Category("Limits")]
        [DisplayName("Limit Stiffness")]
        public float LimitStiffness
        {
            get => _limitStiffness;
            set
            {
                if (SetField(ref _limitStiffness, value))
                    PushLimits();
            }
        }

        [Category("Limits")]
        [DisplayName("Limit Damping")]
        public float LimitDamping
        {
            get => _limitDamping;
            set
            {
                if (SetField(ref _limitDamping, value))
                    PushLimits();
            }
        }

        [Category("Motor")]
        [DisplayName("Enable Drive")]
        [Description("Enable the hinge motor.")]
        public bool EnableDrive
        {
            get => _enableDrive;
            set
            {
                if (SetField(ref _enableDrive, value) && NativeJoint is IAbstractHingeJoint hj)
                    hj.EnableDrive = value;
            }
        }

        [Category("Motor")]
        [DisplayName("Drive Velocity")]
        [Description("Target angular velocity for the motor.")]
        public float DriveVelocity
        {
            get => _driveVelocity;
            set
            {
                if (SetField(ref _driveVelocity, value) && NativeJoint is IAbstractHingeJoint hj)
                    hj.DriveVelocity = value;
            }
        }

        [Category("Motor")]
        [DisplayName("Drive Force Limit")]
        [Description("Maximum force the motor can apply.")]
        public float DriveForceLimit
        {
            get => _driveForceLimit;
            set
            {
                if (SetField(ref _driveForceLimit, value) && NativeJoint is IAbstractHingeJoint hj)
                    hj.DriveForceLimit = value;
            }
        }

        [Category("Motor")]
        [DisplayName("Drive Gear Ratio")]
        public float DriveGearRatio
        {
            get => _driveGearRatio;
            set
            {
                if (SetField(ref _driveGearRatio, value) && NativeJoint is IAbstractHingeJoint hj)
                    hj.DriveGearRatio = value;
            }
        }

        [Category("Motor")]
        [DisplayName("Drive Free Spin")]
        [Description("When enabled, the motor only applies force in the drive direction.")]
        public bool DriveIsFreeSpin
        {
            get => _driveIsFreeSpin;
            set
            {
                if (SetField(ref _driveIsFreeSpin, value) && NativeJoint is IAbstractHingeJoint hj)
                    hj.DriveIsFreeSpin = value;
            }
        }

        protected override IAbstractJoint? CreateJointImpl(
            AbstractPhysicsScene scene,
            IAbstractPhysicsActor? actorA, JointAnchor localFrameA,
            IAbstractPhysicsActor? actorB, JointAnchor localFrameB)
            => scene.CreateHingeJoint(actorA, localFrameA, actorB, localFrameB);

        protected override void ApplyJointProperties(IAbstractJoint joint)
        {
            if (joint is not IAbstractHingeJoint hj)
                return;

            hj.EnableLimit = _enableLimit;
            hj.Limit = new JointAngularLimitPair(
                _lowerAngleRadians, _upperAngleRadians,
                _limitStiffness, _limitDamping,
                _limitRestitution, _limitBounceThreshold);
            hj.EnableDrive = _enableDrive;
            hj.DriveVelocity = _driveVelocity;
            hj.DriveForceLimit = _driveForceLimit;
            hj.DriveGearRatio = _driveGearRatio;
            hj.DriveIsFreeSpin = _driveIsFreeSpin;
        }

        private void PushLimits()
        {
            if (NativeJoint is not IAbstractHingeJoint hj)
                return;

            hj.Limit = new JointAngularLimitPair(
                _lowerAngleRadians, _upperAngleRadians,
                _limitStiffness, _limitDamping,
                _limitRestitution, _limitBounceThreshold);
        }

        protected override void RenderJointSpecificGizmos(Vector3 anchorWorldA, Vector3 anchorWorldB, Matrix4x4 bodyWorldA)
        {
            if (!_enableLimit)
                return;

            // Draw angular limit arc around the hinge axis (local X axis of anchor)
            Matrix4x4 anchorWorld = Matrix4x4.CreateFromQuaternion(AnchorRotation) * bodyWorldA;
            Vector3 axis = Vector3.Normalize(new Vector3(anchorWorld.M11, anchorWorld.M12, anchorWorld.M13));
            Vector3 refDir = Vector3.Normalize(new Vector3(anchorWorld.M21, anchorWorld.M22, anchorWorld.M23));

            const int arcSegments = 16;
            const float arcRadius = 0.08f;
            float range = _upperAngleRadians - _lowerAngleRadians;
            for (int i = 0; i < arcSegments; i++)
            {
                float t0 = _lowerAngleRadians + range * i / arcSegments;
                float t1 = _lowerAngleRadians + range * (i + 1) / arcSegments;
                Vector3 p0 = anchorWorldA + Vector3.Transform(refDir, Quaternion.CreateFromAxisAngle(axis, t0)) * arcRadius;
                Vector3 p1 = anchorWorldA + Vector3.Transform(refDir, Quaternion.CreateFromAxisAngle(axis, t1)) * arcRadius;
                Engine.Rendering.Debug.RenderLine(p0, p1, ColorF4.Yellow);
            }

            // Draw limit boundary lines
            Vector3 lower = anchorWorldA + Vector3.Transform(refDir, Quaternion.CreateFromAxisAngle(axis, _lowerAngleRadians)) * arcRadius;
            Vector3 upper = anchorWorldA + Vector3.Transform(refDir, Quaternion.CreateFromAxisAngle(axis, _upperAngleRadians)) * arcRadius;
            Engine.Rendering.Debug.RenderLine(anchorWorldA, lower, ColorF4.Orange);
            Engine.Rendering.Debug.RenderLine(anchorWorldA, upper, ColorF4.Orange);
        }
    }
}
