using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Scene;
using XREngine.Scene.Physics.Joints;

namespace XREngine.Components.Physics
{
    /// <summary>
    /// Constrains the distance between two anchor points within configurable min/max bounds.
    /// </summary>
    [Category("Physics")]
    [DisplayName("Distance Joint")]
    [Description("Constrains the distance between two anchor points.")]
    [XRComponentEditor("XREngine.Editor.ComponentEditors.DistanceJointComponentEditor")]
    public class DistanceJointComponent : PhysicsJointComponent
    {
        private float _minDistance;
        private float _maxDistance = float.MaxValue;
        private bool _enableMinDistance;
        private bool _enableMaxDistance;
        private float _stiffness;
        private float _damping;
        private float _tolerance = 0.025f;

        [Category("Distance")]
        [DisplayName("Min Distance")]
        [Description("Minimum allowed distance between the anchor points.")]
        public float MinDistance
        {
            get => _minDistance;
            set
            {
                if (SetField(ref _minDistance, value) && NativeJoint is IAbstractDistanceJoint dj)
                    dj.MinDistance = value;
            }
        }

        [Category("Distance")]
        [DisplayName("Max Distance")]
        [Description("Maximum allowed distance between the anchor points.")]
        public float MaxDistance
        {
            get => _maxDistance;
            set
            {
                if (SetField(ref _maxDistance, value) && NativeJoint is IAbstractDistanceJoint dj)
                    dj.MaxDistance = value;
            }
        }

        [Category("Distance")]
        [DisplayName("Enable Min Distance")]
        [Description("Whether the minimum distance limit is enforced.")]
        public bool EnableMinDistance
        {
            get => _enableMinDistance;
            set
            {
                if (SetField(ref _enableMinDistance, value) && NativeJoint is IAbstractDistanceJoint dj)
                    dj.EnableMinDistance = value;
            }
        }

        [Category("Distance")]
        [DisplayName("Enable Max Distance")]
        [Description("Whether the maximum distance limit is enforced.")]
        public bool EnableMaxDistance
        {
            get => _enableMaxDistance;
            set
            {
                if (SetField(ref _enableMaxDistance, value) && NativeJoint is IAbstractDistanceJoint dj)
                    dj.EnableMaxDistance = value;
            }
        }

        [Category("Spring")]
        [DisplayName("Stiffness")]
        [Description("Spring stiffness for the distance constraint.")]
        public float Stiffness
        {
            get => _stiffness;
            set
            {
                if (SetField(ref _stiffness, value) && NativeJoint is IAbstractDistanceJoint dj)
                    dj.Stiffness = value;
            }
        }

        [Category("Spring")]
        [DisplayName("Damping")]
        [Description("Spring damping for the distance constraint.")]
        public float Damping
        {
            get => _damping;
            set
            {
                if (SetField(ref _damping, value) && NativeJoint is IAbstractDistanceJoint dj)
                    dj.Damping = value;
            }
        }

        [Category("Distance")]
        [DisplayName("Tolerance")]
        [Description("Tolerance for the distance limit.")]
        public float Tolerance
        {
            get => _tolerance;
            set
            {
                if (SetField(ref _tolerance, value) && NativeJoint is IAbstractDistanceJoint dj)
                    dj.Tolerance = value;
            }
        }

        protected override IAbstractJoint? CreateJointImpl(
            AbstractPhysicsScene scene,
            IAbstractPhysicsActor? actorA, JointAnchor localFrameA,
            IAbstractPhysicsActor? actorB, JointAnchor localFrameB)
            => scene.CreateDistanceJoint(actorA, localFrameA, actorB, localFrameB);

        protected override void ApplyJointProperties(IAbstractJoint joint)
        {
            if (joint is not IAbstractDistanceJoint dj)
                return;

            dj.MinDistance = _minDistance;
            dj.MaxDistance = _maxDistance;
            dj.EnableMinDistance = _enableMinDistance;
            dj.EnableMaxDistance = _enableMaxDistance;
            dj.Stiffness = _stiffness;
            dj.Damping = _damping;
            dj.Tolerance = _tolerance;
        }

        protected override void RenderJointSpecificGizmos(Vector3 anchorWorldA, Vector3 anchorWorldB, Matrix4x4 bodyWorldA)
        {
            // Draw min/max distance spheres centered on anchor A
            if (_enableMinDistance && _minDistance > 0f)
                Engine.Rendering.Debug.RenderSphere(anchorWorldA, _minDistance, false, new ColorF4(1f, 1f, 0f, 0.3f));

            if (_enableMaxDistance && _maxDistance < 1000f)
                Engine.Rendering.Debug.RenderSphere(anchorWorldA, _maxDistance, false, new ColorF4(1f, 0.5f, 0f, 0.3f));
        }
    }
}
