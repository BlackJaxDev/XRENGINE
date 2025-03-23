using MagicPhysX;

namespace XREngine.Rendering.Physics.Physx
{
    public unsafe class CapsuleController : Controller
    {
        public PxCapsuleController* CapsuleControllerPtr { get; internal set; }
        public override unsafe PxController* ControllerPtr => (PxController*)CapsuleControllerPtr;

        public float Radius
        {
            get => CapsuleControllerPtr->GetRadius();
            set => CapsuleControllerPtr->SetRadiusMut(value);
        }
        public float Height
        {
            get => CapsuleControllerPtr->GetHeight();
            set => CapsuleControllerPtr->SetHeightMut(value);
        }
        public PxCapsuleClimbingMode ClimbingMode
        {
            get => CapsuleControllerPtr->GetClimbingMode();
            set => CapsuleControllerPtr->SetClimbingModeMut(value);
        }
    }
}