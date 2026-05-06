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

    /// <summary>
    /// Same as <see cref="TryPrepareForRendering"/> but also returns the most recent stage result
    /// (e.g. "Ready", "ProgramsPending", "BuffersPending", "GenerateFailed", "MaterialMissing").
    /// </summary>
    bool TryPrepareForRendering(out string reason)
    {
        bool ok = TryPrepareForRendering();
        reason = ok ? "Ready" : "Pending";
        return ok;
    }

    /// <summary>
    /// Optional supplemental detail describing the most recent prepare attempt
    /// (e.g. variant counts, revision numbers, which program slots were null).
    /// Empty when not implemented or no detail captured.
    /// </summary>
    string LastPrepareDetail => string.Empty;
}
