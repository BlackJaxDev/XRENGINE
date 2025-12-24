using System.ComponentModel;
using System.Numerics;
using XREngine.Components.Physics;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene.Physics.Jolt;

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

        private readonly HashSet<DynamicRigidBodyComponent> _affected = [];

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            RegisterTick(ETickGroup.PostPhysics, (int)ETickOrder.Scene, ApplyGravityTick);
        }

        protected internal override void OnComponentDeactivated()
        {
            UnregisterTick(ETickGroup.PostPhysics, (int)ETickOrder.Scene, ApplyGravityTick);
            _affected.Clear();
            base.OnComponentDeactivated();
        }

        private void ApplyGravityTick()
        {
            if (_affected.Count == 0)
                return;

            var scene = World?.PhysicsScene;
            if (scene is null)
                return;

            // Approximate per-body gravity by applying a velocity delta each fixed step.
            // - PhysX: we can disable built-in gravity per actor.
            // - Jolt: no per-body gravity toggle exposed; apply the difference from scene gravity.
            float dt = Engine.FixedDelta;
            Vector3 dvPhysx = Gravity * dt;
            Vector3 dvJolt = (Gravity - scene.Gravity) * dt;

            foreach (var bodyComponent in _affected)
            {
                var body = bodyComponent.RigidBody;
                switch (body)
                {
                    case PhysxDynamicRigidBody physx:
                        physx.SetLinearVelocity(physx.LinearVelocity + dvPhysx, wake: true);
                        break;
                    case JoltDynamicRigidBody jolt:
                        jolt.SetLinearVelocity(jolt.LinearVelocity + dvJolt);
                        break;
                }
            }
        }

        protected override void OnEntered(XRComponent component)
        {
            if (component is DynamicRigidBodyComponent rb)
            {
                _affected.Add(rb);
                if (rb.RigidBody is PhysxActor actor)
                    actor.GravityEnabled = false;
            }

            base.OnEntered(component);
        }
        protected override void OnLeft(XRComponent component)
        {
            if (component is DynamicRigidBodyComponent rb)
            {
                _affected.Remove(rb);
                if (rb.RigidBody is PhysxActor actor)
                    actor.GravityEnabled = true;
            }

            base.OnLeft(component);
        }
    }
}