using XREngine.Scene.Physics.Physx;
using MagicPhysX;

namespace XREngine.Rendering.Physics.Physx.Joints
{
    public unsafe class PhysxJoint_RackAndPinion : PhysxJoint
    {
        public PxRackAndPinionJoint* _joint;
        public override unsafe PxJoint* JointBase => (PxJoint*)_joint;

        public float Ratio
        {
            get => _joint->GetRatio();
            set => _joint->SetRatioMut(value);
        }

        public bool SetData(uint rackTeethCount, uint pinionTeeth, float rackLength)
            => _joint->SetDataMut(rackTeethCount, pinionTeeth, rackLength);

        public void SetJoints(IHingeJoint hinge, IPrismaticJoint prismatic)
            => _joint->SetJointsMut(((PhysxBase)hinge).BasePtr, ((PhysxBase)prismatic).BasePtr);
    }
}