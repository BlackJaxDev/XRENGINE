using XREngine.Data.Core;

namespace XREngine.Rendering
{
    /// <summary>
    /// This is the base class for all objects that are allocated by the rendering api (opengl, vulkan, etc).
    /// </summary>
    [RuntimeOnly]
    public abstract class AbstractRenderAPIObject(XRWindow window) : XRBase, IDisposable
    {
        public XRWindow Window { get; } = window;

        private bool disposedValue;

        public abstract bool IsGenerated { get; }
        public abstract void Generate();
        public abstract void Destroy();

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                Destroy();
                disposedValue = true;
            }
        }

        // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        ~AbstractRenderAPIObject()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: false);
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        public abstract string GetDescribingName();

        public virtual nint GetHandle() => 0;
    }
}
