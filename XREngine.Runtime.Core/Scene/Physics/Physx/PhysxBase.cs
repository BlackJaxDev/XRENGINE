using MagicPhysX;
using XREngine.Data.Core;

namespace XREngine.Scene.Physics.Physx
{
    public unsafe abstract class PhysxBase : XRBase
    {
        public abstract PxBase* BasePtr { get; }
    }
}