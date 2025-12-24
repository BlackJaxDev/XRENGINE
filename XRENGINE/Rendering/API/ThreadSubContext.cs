using XREngine.Data.Vectors;

namespace XREngine.Rendering
{
    public abstract partial class WindowContext
    {
        protected internal abstract class ThreadSubContext(IntPtr? controlHandle, Thread thread)
        {
            protected Thread _thread = thread;
            protected IntPtr? _controlHandle = controlHandle;

            public IVector2 Size { get; private set; }

            public abstract void Generate();
            public abstract bool IsCurrent();
            public abstract bool IsContextDisposed();
            public abstract void OnSwapBuffers();
            public virtual void OnResized(IVector2 size) => Size = size;
            public abstract void SetCurrent(bool current);
            public abstract void Dispose();
            internal abstract void VsyncChanged(EVSyncMode vsyncMode);
        }
    }
}
