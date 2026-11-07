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
    private int _retired;

    public bool IsRetired => Volatile.Read(ref _retired) != 0;

    public abstract bool IsGenerated { get; }
    public abstract void Generate();
    public abstract void Destroy();

    /// <summary>
    /// Permanently detaches this wrapper from its logical object before a renderer
    /// generation is retired. Unlike <see cref="Destroy"/>, retirement must also remove
    /// managed event subscriptions for wrappers that never generated a native handle.
    /// </summary>
    protected internal void Retire()
    {
        if (Interlocked.Exchange(ref _retired, 1) != 0)
            return;

        try
        {
            OnRetiring();
        }
        finally
        {
            Destroy();
        }
    }

    /// <summary>Releases managed links before terminal native retirement.</summary>
    protected virtual void OnRetiring() { }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                // TODO: dispose managed state (managed objects)
            }

            Retire();
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
    /// Prepares the object before a LOD selection can use it, outside a recorded draw, and tells if it can draw now. The
    /// default is <see cref="TryPrepareForRendering(out string)"/>. A backend whose draw needs more than that preparation
    /// (for example, a pipeline or descriptor sets) can do more here.
    /// </summary>
    bool TryPrewarmForSelection(out string reason)
        => TryPrepareForRendering(out reason);

    /// <summary>
    /// Starts the non-blocking preparation of a draw with <paramref name="material"/>, if it is not started, and gives the
    /// state of the backend objects that the draw needs: the program link and, on backends that use them, the graphics
    /// pipelines of the passes that draw the renderer. Call it on the render thread, inside a window renderer scope. It must
    /// not wait for a link or a compile and must not allocate when nothing changed. The default gives
    /// <see cref="ERenderDrawReadiness.NotSupported"/>, so a backend that does not report the state is never ready by
    /// accident.
    /// </summary>
    /// <param name="material">The material of the draw, or null for the material of the renderer.</param>
    /// <param name="reason">The reason of the state, for diagnostics.</param>
    ERenderDrawReadiness QueryDrawReadiness(XRMaterial? material, out string reason)
    {
        reason = "the render backend does not report draw readiness";
        return ERenderDrawReadiness.NotSupported;
    }

    /// <summary>
    /// Optional supplemental detail describing the most recent prepare attempt
    /// (e.g. variant counts, revision numbers, which program slots were null).
    /// Empty when not implemented or no detail captured.
    /// </summary>
    string LastPrepareDetail => string.Empty;
}
