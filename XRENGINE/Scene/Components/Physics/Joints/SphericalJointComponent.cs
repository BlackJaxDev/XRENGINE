using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Scene;
using XREngine.Scene.Physics.Joints;

namespace XREngine.Components.Physics
{
    /// <summary>
    /// A spherical (ball-and-socket) joint with optional cone limit.
    /// </summary>
    [Category("Physics")]
    [DisplayName("Spherical Joint")]
    [Description("Ball-and-socket joint with optional cone limit.")]
    [XRComponentEditor("XREngine.Editor.ComponentEditors.SphericalJointComponentEditor")]
    public class SphericalJointComponent : PhysicsJointComponent
    {
        private bool _enableLimitCone;
        private float _limitConeYAngleRadians = float.Pi / 4f;
        private float _limitConeZAngleRadians = float.Pi / 4f;
        private float _limitRestitution;
        private float _limitBounceThreshold;
        private float _limitStiffness;
        private float _limitDamping;

        /// <summary>
        /// Current swing Y angle (read-only runtime value).
        /// </summary>
        [Browsable(false)]
        public float SwingYAngle => NativeJoint is IAbstractSphericalJoint sj ? sj.SwingYAngle : 0f;

        /// <summary>
        /// Current swing Z angle (read-only runtime value).
        /// </summary>
        [Browsable(false)]
        public float SwingZAngle => NativeJoint is IAbstractSphericalJoint sj ? sj.SwingZAngle : 0f;

        [Category("Limits")]
        [DisplayName("Enable Limit Cone")]
        [Description("Enable the cone-shaped angular limits.")]
        public bool EnableLimitCone
        {
            get => _enableLimitCone;
            set
            {
                if (SetField(ref _enableLimitCone, value) && NativeJoint is IAbstractSphericalJoint sj)
                    sj.EnableLimitCone = value;
            }
        }

        [Category("Limits")]
        [DisplayName("Cone Y Angle (rad)")]
        [Description("Half-angle of the cone swing limit around the Y axis.")]
        public float LimitConeYAngleRadians
        {
            get => _limitConeYAngleRadians;
            set
            {
                if (SetField(ref _limitConeYAngleRadians, value))
                    PushLimitCone();
            }
        }

        [Category("Limits")]
        [DisplayName("Cone Z Angle (rad)")]
        [Description("Half-angle of the cone swing limit around the Z axis.")]
        public float LimitConeZAngleRadians
        {
            get => _limitConeZAngleRadians;
            set
            {
                if (SetField(ref _limitConeZAngleRadians, value))
                    PushLimitCone();
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
                    PushLimitCone();
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
                    PushLimitCone();
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
                    PushLimitCone();
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
                    PushLimitCone();
            }
        }

        protected override IAbstractJoint? CreateJointImpl(
            AbstractPhysicsScene scene,
            IAbstractPhysicsActor? actorA, JointAnchor localFrameA,
            IAbstractPhysicsActor? actorB, JointAnchor localFrameB)
            => scene.CreateSphericalJoint(actorA, localFrameA, actorB, localFrameB);

        protected override void ApplyJointProperties(IAbstractJoint joint)
        {
            if (joint is not IAbstractSphericalJoint sj)
                return;

            sj.EnableLimitCone = _enableLimitCone;
            sj.LimitCone = new JointLimitCone(
                _limitConeYAngleRadians, _limitConeZAngleRadians,
                _limitStiffness, _limitDamping,
                _limitRestitution, _limitBounceThreshold);
        }

        private void PushLimitCone()
        {
            if (NativeJoint is not IAbstractSphericalJoint sj)
                return;

            sj.LimitCone = new JointLimitCone(
                _limitConeYAngleRadians, _limitConeZAngleRadians,
                _limitStiffness, _limitDamping,
                _limitRestitution, _limitBounceThreshold);
        }

        protected override void RenderJointSpecificGizmos(Vector3 anchorWorldA, Vector3 anchorWorldB, Matrix4x4 bodyWorldA)
        {
            if (!_enableLimitCone)
                return;

            // Draw a cone outline approximation at anchor A
            Matrix4x4 anchorWorld = Matrix4x4.CreateFromQuaternion(AnchorRotation) * bodyWorldA;
            Vector3 axis = Vector3.Normalize(new Vector3(anchorWorld.M11, anchorWorld.M12, anchorWorld.M13));
            Vector3 up = Vector3.Normalize(new Vector3(anchorWorld.M21, anchorWorld.M22, anchorWorld.M23));
            Vector3 right = Vector3.Normalize(new Vector3(anchorWorld.M31, anchorWorld.M32, anchorWorld.M33));

            const float coneLength = 0.12f;
            const int segments = 16;
            Vector3 tip = anchorWorldA;
            Vector3[] ring = new Vector3[segments + 1];
            for (int i = 0; i <= segments; i++)
            {
                float angle = 2f * MathF.PI * i / segments;
                float cosA = MathF.Cos(angle);
                float sinA = MathF.Sin(angle);
                float yRadius = MathF.Tan(_limitConeYAngleRadians) * coneLength;
                float zRadius = MathF.Tan(_limitConeZAngleRadians) * coneLength;
                Vector3 offset = up * (cosA * yRadius) + right * (sinA * zRadius);
                ring[i] = tip + axis * coneLength + offset;
            }

            for (int i = 0; i < segments; i++)
            {
                Engine.Rendering.Debug.RenderLine(ring[i], ring[i + 1], ColorF4.Yellow);
                if (i % 4 == 0)
                    Engine.Rendering.Debug.RenderLine(tip, ring[i], new ColorF4(1f, 1f, 0f, 0.5f));
            }
        }
    }
}
