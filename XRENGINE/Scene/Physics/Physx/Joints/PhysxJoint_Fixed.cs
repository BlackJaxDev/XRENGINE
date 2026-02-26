using MagicPhysX;
using XREngine.Scene.Physics.Joints;

namespace XREngine.Rendering.Physics.Physx.Joints
{
    public unsafe class PhysxJoint_Fixed(PxFixedJoint* joint) : PhysxJoint, IAbstractFixedJoint
    {
        public PxFixedJoint* _joint = joint;

        public override unsafe PxJoint* JointBase => (PxJoint*)_joint;
    }
}