using MagicPhysX;
using static MagicPhysX.NativeMethods;

namespace XREngine.Rendering.Physics.Physx
{
    public abstract unsafe class PhysxRefCounted : PhysxBase
    {
        public abstract PxRefCounted* RefCountedPtr { get; }

        protected bool _isReleased;
        public bool IsReleased => _isReleased;

        public uint ReferenceCount => PxRefCounted_getReferenceCount(RefCountedPtr);

        public void Aquire()
        {
            if (_isReleased)
            {
                PhysxObjectLog.Modified(this, (nint)RefCountedPtr, nameof(Aquire), "ignored (released)");
                return;
            }
            PxRefCounted_acquireReference_mut(RefCountedPtr);
            PhysxObjectLog.Modified(this, (nint)RefCountedPtr, nameof(Aquire), $"refCount={ReferenceCount}");
        }

        public virtual void Release()
        {
            if (_isReleased)
                return;
            _isReleased = true;
            PhysxObjectLog.Released(this, (nint)RefCountedPtr);
            PxRefCounted_release_mut(RefCountedPtr);
        }

        public override unsafe PxBase* BasePtr => (PxBase*)RefCountedPtr;
    }
}