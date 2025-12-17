using MagicPhysX;

namespace XREngine.Rendering.Physics.Physx
{
    public unsafe class PhysxBoxController : PhysxController
    {
        public PxBoxController* BoxControllerPtr { get; internal set; }
        public override unsafe PxController* ControllerPtr => (PxController*)BoxControllerPtr;

        public float HalfHeight
        {
            get =>BoxControllerPtr->GetHalfHeight();
            set => BoxControllerPtr->SetHalfHeightMut(value);
        }
        public float HalfSideExtent
        {
            get => BoxControllerPtr->GetHalfSideExtent();
            set => BoxControllerPtr->SetHalfSideExtentMut(value);
        }
        public float HalfForwardExtent
        {
            get => BoxControllerPtr->GetHalfForwardExtent();
            set => BoxControllerPtr->SetHalfForwardExtentMut(value);
        }
    }
}