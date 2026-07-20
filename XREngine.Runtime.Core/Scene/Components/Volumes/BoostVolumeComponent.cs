using System.ComponentModel;
using System.Numerics;
using XREngine.Scene.Physics;

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
            if (component is IRuntimeDynamicRigidBodyComponent rb)
            {
                IAbstractDynamicRigidBody? body = rb.RigidBody;
                body?.SetLinearVelocity(body.LinearVelocity + Force, wake: true);
            }

            base.OnEntered(component);
        }
    }
}
