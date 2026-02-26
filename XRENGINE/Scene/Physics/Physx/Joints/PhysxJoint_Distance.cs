using MagicPhysX;
using XREngine.Scene.Physics.Joints;

namespace XREngine.Rendering.Physics.Physx.Joints
{
    public unsafe class PhysxJoint_Distance(PxDistanceJoint* joint) : PhysxJoint, IAbstractDistanceJoint
    {
        public PxDistanceJoint* _joint = joint;
        public override unsafe PxJoint* JointBase => (PxJoint*)_joint;

        public float Distance => _joint->GetDistance();

        public float MinDistance
        {
            get => _joint->GetMinDistance();
            set => _joint->SetMinDistanceMut(value);
        }

        public float MaxDistance
        {
            get => _joint->GetMaxDistance();
            set => _joint->SetMaxDistanceMut(value);
        }

        public float Stiffness
        {
            get => _joint->GetStiffness();
            set => _joint->SetStiffnessMut(value);
        }

        public float Damping
        {
            get => _joint->GetDamping();
            set => _joint->SetDampingMut(value);
        }

        public float Tolerance
        {
            get => _joint->GetTolerance();
            set => _joint->SetToleranceMut(value);
        }

        public float ContactDistance
        {
            get => _joint->GetContactDistance();
            set => _joint->SetContactDistanceMut(value);
        }

        public PxDistanceJointFlags DistanceFlags
        {
            get => _joint->GetDistanceJointFlags();
            set => _joint->SetDistanceJointFlagsMut(value);
        }

        public void SetFlag(PxDistanceJointFlag flag, bool value)
            => _joint->SetDistanceJointFlagMut(flag, value);

        #region IAbstractDistanceJoint

        float IAbstractDistanceJoint.Distance => Distance;
        float IAbstractDistanceJoint.MinDistance { get => MinDistance; set => MinDistance = value; }
        float IAbstractDistanceJoint.MaxDistance { get => MaxDistance; set => MaxDistance = value; }
        float IAbstractDistanceJoint.Stiffness { get => Stiffness; set => Stiffness = value; }
        float IAbstractDistanceJoint.Damping { get => Damping; set => Damping = value; }
        float IAbstractDistanceJoint.Tolerance { get => Tolerance; set => Tolerance = value; }

        bool IAbstractDistanceJoint.EnableMinDistance
        {
            get => DistanceFlags.HasFlag(PxDistanceJointFlags.MinDistanceEnabled);
            set => SetFlag(PxDistanceJointFlag.MinDistanceEnabled, value);
        }

        bool IAbstractDistanceJoint.EnableMaxDistance
        {
            get => DistanceFlags.HasFlag(PxDistanceJointFlags.MaxDistanceEnabled);
            set => SetFlag(PxDistanceJointFlag.MaxDistanceEnabled, value);
        }

        #endregion
    }
}