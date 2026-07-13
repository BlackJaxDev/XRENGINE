using System.ComponentModel;
using System.Numerics;
using XREngine.Components.Physics;

namespace XREngine.Components.Scene.Volumes
{
    /// <summary>
    /// A volume that applies a custom gravity vector to dynamic rigid bodies within it.
    /// </summary>
    [Description("A volume that applies a custom gravity vector to dynamic rigid bodies within it.")]
    public class GravityVolumeComponent : TriggerVolumeComponent
    {
        private Vector3 _gravity = new(0.0f, -9.81f, 0.0f);
        public Vector3 Gravity
        {
            get => _gravity;
            set => SetField(ref _gravity, value);
        }

        private readonly Dictionary<DynamicRigidBodyComponent, bool> _affected = [];

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            RegisterTick(ETickGroup.PostPhysics, (int)ETickOrder.Scene, ApplyGravityTick);
        }

        protected override void OnComponentDeactivated()
        {
            UnregisterTick(ETickGroup.PostPhysics, (int)ETickOrder.Scene, ApplyGravityTick);
            foreach ((DynamicRigidBodyComponent component, bool gravityEnabled) in _affected)
            {
                if (component.RigidBody is not null)
                    component.RigidBody.GravityEnabled = gravityEnabled;
            }
            _affected.Clear();
            base.OnComponentDeactivated();
        }

        private void ApplyGravityTick()
        {
            if (_affected.Count == 0)
                return;

            float dt = Engine.FixedDelta;
            Vector3 velocityDelta = Gravity * dt;

            foreach (DynamicRigidBodyComponent bodyComponent in _affected.Keys)
            {
                var body = bodyComponent.RigidBody;
                body?.SetLinearVelocity(body.LinearVelocity + velocityDelta, wake: true);
            }
        }

        protected override void OnEntered(XRComponent component)
        {
            if (component is DynamicRigidBodyComponent rb)
            {
                if (rb.RigidBody is { } body && _affected.TryAdd(rb, body.GravityEnabled))
                    body.GravityEnabled = false;
            }

            base.OnEntered(component);
        }
        protected override void OnLeft(XRComponent component)
        {
            if (component is DynamicRigidBodyComponent rb)
            {
                if (_affected.Remove(rb, out bool gravityEnabled) && rb.RigidBody is { } body)
                    body.GravityEnabled = gravityEnabled;
            }

            base.OnLeft(component);
        }
    }
}
