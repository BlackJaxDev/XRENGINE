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
        OwnerGeneration = owner is IRuntimeRendererHost renderer
            ? renderer.BackendGeneration
            : 0;
    }

    public IRenderApiWrapperOwner Owner { get; }
    public long OwnerGeneration { get; }

    private bool disposedValue;

    public abstract bool IsGenerated { get; }
    public abstract void Generate();
    public abstract void Destroy();

    /// <summary>
    /// Permanently detaches this wrapper from its logical object before a renderer
    /// generation is retired. Unlike <see cref="Destroy"/>, retirement must also remove
    /// managed event subscriptions for wrappers that never generated a native handle.
    /// </summary>
    protected internal virtual void Retire()
        => Destroy();

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

    /// <summary>
    /// Rejects a stale wrapper before it can call into a retired collectible backend.
    /// Backend operations may use this at public submission boundaries.
    /// </summary>
    protected void ValidateOwnerGeneration()
    {
        if (Owner is not AbstractRenderer renderer)
            return;
        if (renderer.AcceptsBackendWork && renderer.BackendGeneration == OwnerGeneration)
            return;

        throw new InvalidOperationException(
            $"API wrapper '{GetType().Name}' belongs to retired renderer generation {OwnerGeneration}; active owner generation is {renderer.BackendGeneration}.");
    }
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
