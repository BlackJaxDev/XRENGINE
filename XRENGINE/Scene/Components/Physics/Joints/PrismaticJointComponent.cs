using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Scene;
using XREngine.Scene.Physics.Joints;

namespace XREngine.Components.Physics
{
    /// <summary>
    /// A prismatic (slider) joint that allows translation along a single axis.
    /// </summary>
    [Category("Physics")]
    [DisplayName("Prismatic Joint")]
    [Description("Allows sliding along a single axis with optional limits.")]
    [XRComponentEditor("XREngine.Editor.ComponentEditors.PrismaticJointComponentEditor")]
    public class PrismaticJointComponent : PhysicsJointComponent
    {
        private bool _enableLimit;
        private float _lowerLimit;
        private float _upperLimit;
        private float _limitRestitution;
        private float _limitBounceThreshold;
        private float _limitStiffness;
        private float _limitDamping;

        /// <summary>
        /// Current position along the slider axis (read-only runtime value).
        /// </summary>
        [Browsable(false)]
        public float Position => NativeJoint is IAbstractPrismaticJoint pj ? pj.Position : 0f;

        /// <summary>
        /// Current velocity along the slider axis (read-only runtime value).
        /// </summary>
        [Browsable(false)]
        public float Velocity => NativeJoint is IAbstractPrismaticJoint pj ? pj.Velocity : 0f;

        [Category("Limits")]
        [DisplayName("Enable Limit")]
        [Description("Enable linear limits on the slider.")]
        public bool EnableLimit
        {
            get => _enableLimit;
            set
            {
                if (SetField(ref _enableLimit, value) && NativeJoint is IAbstractPrismaticJoint pj)
                    pj.EnableLimit = value;
            }
        }

        [Category("Limits")]
        [DisplayName("Lower Limit")]
        [Description("Lower translation limit along the slider axis.")]
        public float LowerLimit
        {
            get => _lowerLimit;
            set
            {
                if (SetField(ref _lowerLimit, value))
                    PushLimits();
            }
        }

        [Category("Limits")]
        [DisplayName("Upper Limit")]
        [Description("Upper translation limit along the slider axis.")]
        public float UpperLimit
        {
            get => _upperLimit;
            set
            {
                if (SetField(ref _upperLimit, value))
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

        protected override IAbstractJoint? CreateJointImpl(
            AbstractPhysicsScene scene,
            IAbstractPhysicsActor? actorA, JointAnchor localFrameA,
            IAbstractPhysicsActor? actorB, JointAnchor localFrameB)
            => scene.CreatePrismaticJoint(actorA, localFrameA, actorB, localFrameB);

        protected override void ApplyJointProperties(IAbstractJoint joint)
        {
            if (joint is not IAbstractPrismaticJoint pj)
                return;

            pj.EnableLimit = _enableLimit;
            pj.Limit = new JointLinearLimitPair(
                _lowerLimit, _upperLimit,
                _limitStiffness, _limitDamping,
                _limitRestitution, _limitBounceThreshold);
        }

        private void PushLimits()
        {
            if (NativeJoint is not IAbstractPrismaticJoint pj)
                return;

            pj.Limit = new JointLinearLimitPair(
                _lowerLimit, _upperLimit,
                _limitStiffness, _limitDamping,
                _limitRestitution, _limitBounceThreshold);
        }

        protected override void RenderJointSpecificGizmos(Vector3 anchorWorldA, Vector3 anchorWorldB, Matrix4x4 bodyWorldA)
        {
            if (!_enableLimit)
                return;

            // Draw linear limit markers along the slide axis (local X axis of anchor)
            Matrix4x4 anchorWorld = Matrix4x4.CreateFromQuaternion(AnchorRotation) * bodyWorldA;
            Vector3 axis = Vector3.Normalize(new Vector3(anchorWorld.M11, anchorWorld.M12, anchorWorld.M13));

            Vector3 lowerPos = anchorWorldA + axis * _lowerLimit;
            Vector3 upperPos = anchorWorldA + axis * _upperLimit;

            Engine.Rendering.Debug.RenderLine(lowerPos, upperPos, ColorF4.Yellow);
            Engine.Rendering.Debug.RenderSphere(lowerPos, 0.015f, false, ColorF4.Orange);
            Engine.Rendering.Debug.RenderSphere(upperPos, 0.015f, false, ColorF4.Orange);
        }
    }
}
