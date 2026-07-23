using MagicPhysX;
using System.Numerics;

namespace XREngine.Scene.Physics.Physx.Joints
{
    public unsafe class PhysxJoint_Contact(PxContactJoint* joint) : PhysxJoint
    {
        public PxContactJoint* _joint = joint;
        public override unsafe PxJoint* JointBase => (PxJoint*)_joint;

        public float Restitution
        {
            get => _joint->GetRestitution();
            set => _joint->SetRestitutionMut(value);
        }

        public float BounceThreshold
        {
            get => _joint->GetBounceThreshold();
            set => _joint->SetBounceThresholdMut(value);
        }

        public float Penetration
        {
            get => _joint->GetPenetration();
            set => _joint->SetPenetrationMut(value);
        }

        public uint JacobianRowCount => _joint->GetNbJacobianRows();

        public void ComputeJacobians()
        {
            //_joint->ComputeJacobian();
        }

        public Vector3 Contact
        {
            get => _joint->GetContact();
            set
            {
                PxVec3 v = value;
                _joint->SetContactMut(&v);
            }
        }

        public Vector3 Normal
        {
            get => _joint->GetContactNormal();
            set
            {
                PxVec3 v = value;
                _joint->SetContactNormalMut(&v);
            }
        }
    }
}