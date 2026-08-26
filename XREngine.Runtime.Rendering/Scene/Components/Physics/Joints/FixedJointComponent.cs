using System.ComponentModel;
using XREngine.Scene;
using XREngine.Scene.Physics.Joints;

namespace XREngine.Components.Physics
{
    /// <summary>
    /// Rigidly connects two physics actors with no degrees of freedom.
    /// </summary>
    [Category("Physics")]
    [DisplayName("Fixed Joint")]
    [Description("Rigidly locks two physics bodies together.")]
    [XRComponentEditor("XREngine.Editor.ComponentEditors.FixedJointComponentEditor")]
    public class FixedJointComponent : PhysicsJointComponent
    {
        protected override IAbstractJoint? CreateJointImpl(
            AbstractPhysicsScene scene,
            IAbstractPhysicsActor? actorA, JointAnchor localFrameA,
            IAbstractPhysicsActor? actorB, JointAnchor localFrameB)
            => scene.CreateFixedJoint(actorA, localFrameA, actorB, localFrameB);

        protected override void ApplyJointProperties(IAbstractJoint joint)
        {
            // Fixed joints have no additional properties beyond base.
        }
    }
}
