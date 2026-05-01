using XREngine.Data.Core;

namespace XREngine.Rendering;

/// <summary>
/// This is the base class for all objects that are allocated by the rendering api (opengl, vulkan, etc).
/// </summary>
[RuntimeOnly]
public abstract class AbstractRenderAPIObject : XRBase, IDisposable
{
    protected AbstractRenderAPIObject(IRenderApiWrapperOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Owner = owner;
    }

    public IRenderApiWrapperOwner Owner { get; }

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

            Destroy();
            disposedValue = true;
        }
    }

    ~AbstractRenderAPIObject()
    {
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public abstract string GetDescribingName();

    public virtual nint GetHandle() => 0;
}

public interface IRenderPreparationState
{
    bool IsPreparedForRendering { get; }
    bool TryPrepareForRendering();
}
