using System.ComponentModel;
using System.Numerics;
using XREngine.Components.Physics;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene.Physics.Jolt;

namespace XREngine.Components.Scene.Volumes
{
    /// <summary>
    /// A volume that applies a constant force to dynamic rigid bodies only when they first enter it.
    /// </summary>
    [Description("A volume that applies a constant force to dynamic rigid bodies only when they first enter it.")]
    public class BoostVolumeComponent : TriggerVolumeComponent
    {
        private Vector3 _force;
        public Vector3 Force
        {
            get => _force;
            set => SetField(ref _force, value);
        }

        protected override void OnEntered(XRComponent component)
        {
            if (component is DynamicRigidBodyComponent rb)
            {
                switch (rb.RigidBody)
                {
                    case PhysxDynamicRigidBody physx:
                        physx.SetLinearVelocity(physx.LinearVelocity + Force, wake: true);
                        break;
                    case JoltDynamicRigidBody jolt:
                        jolt.SetLinearVelocity(jolt.LinearVelocity + Force);
                        break;
                }
            }

            base.OnEntered(component);
        }
    }
}
