using XREngine.Scene;

namespace XREngine.Components.Physics
{
    public abstract class PhysicsActorComponent : XRComponent
    {
        public IAbstractPhysicsActor? PhysicsActor { get; }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            if (World is not null && PhysicsActor is not null)
                World.PhysicsScene.AddActor(PhysicsActor);
        }
        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            if (World is not null && PhysicsActor is not null)
                World.PhysicsScene.RemoveActor(PhysicsActor);
        }
    }
}
