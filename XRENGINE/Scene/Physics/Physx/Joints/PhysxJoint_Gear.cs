using XREngine.Scene.Physics.Physx;
using MagicPhysX;

namespace XREngine.Rendering.Physics.Physx.Joints
{
    public unsafe class PhysxJoint_Gear : PhysxJoint
    {
        public PxGearJoint* _joint;
        public override unsafe PxJoint* JointBase => (PxJoint*)_joint;

        public float GearRatio
        {
            get => _joint->GetGearRatio();
            set => _joint->SetGearRatioMut(value);
        }

        public void SetHinges(IHingeJoint hinge0, IHingeJoint hinge1)
            => _joint->SetHingesMut(((PhysxBase)hinge0).BasePtr, ((PhysxBase)hinge1).BasePtr);
    }
}