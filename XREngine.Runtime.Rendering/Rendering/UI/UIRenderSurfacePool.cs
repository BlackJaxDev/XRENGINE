using XREngine.Data.Rendering;

namespace XREngine.Rendering.UI;

/// <summary>Bounded reusable color targets for canvas and UI layer producers.</summary>
internal sealed class UIRenderSurfacePool : IDisposable
{
    private const int MaximumSlots = 4;
    private const ulong MaximumBytes = 128UL * 1024 * 1024;
    private readonly object _gate = new();
    private readonly List<UIRenderSurface> _surfaces = [];
    private readonly EPixelInternalFormat _internalFormat;
    private readonly EPixelFormat _format;
    private readonly EPixelType _type;
    private readonly uint _bytesPerPixel;
    private UIRenderSurface? _published;
    private ulong _nextGeneration;
    private ulong _allocatedBytes;
    private bool _disposed;

    internal UIRenderSurfacePool(EPixelInternalFormat internalFormat,
        EPixelFormat format, EPixelType type, uint bytesPerPixel)
    {
        if (bytesPerPixel is 0 or > 16)
            throw new ArgumentOutOfRangeException(nameof(bytesPerPixel));
        _internalFormat = internalFormat;
        _format = format;
        _type = type;
        _bytesPerPixel = bytesPerPixel;
    }

    /// <summary>Returns a writer claim. The caller must Release it after retaining a packet claim.</summary>
    internal bool TryReserve(uint width, uint height, out UIRenderSurface? surface,
        out string? failure)
        => TryReserve(width, height, null, out surface, out failure);

    /// <summary>Reserves a surface with exact native renderer affinity.</summary>
    internal bool TryReserve(uint width, uint height, AbstractRenderer? owner,
        out UIRenderSurface? surface, out string? failure)
    {
        surface = null;
        failure = null;
        if (width == 0 || height == 0 || width > 16384 || height > 16384)
        {
            failure = "UI.RenderSurface.InvalidSize: the requested extent is outside the supported range.";
            return false;
        }
        ulong requestedBytes = checked((ulong)width * height * _bytesPerPixel);
        if (requestedBytes > MaximumBytes)
        {
            failure = "UI.RenderSurface.Capacity: one target exceeds the bounded surface budget.";
            return false;
        }
        lock (_gate)
        {
            if (owner is not null && (!ReferenceEquals(AbstractRenderer.Current, owner) ||
                owner.BackendGeneration == 0))
            {
                failure = "UI.RenderSurface.OwnerMismatch: the native surface owner is not current.";
                return false;
            }
            SweepRetiredOwnerSurfaces(owner);
            if (_disposed)
            {
                failure = "UI.RenderSurface.Disposed: the producer pool has stopped.";
                return false;
            }
            UIRenderSurface? available = null;
            for (int index = 0; index < _surfaces.Count; index++)
            {
                UIRenderSurface candidate = _surfaces[index];
                if (!ReferenceEquals(candidate, _published) && candidate.IsPoolOnly &&
                    MatchesOwner(candidate, owner) &&
                    candidate.Width == width && candidate.Height == height)
                {
                    available = candidate;
                    break;
                }
            }
            if (available is null)
                for (int index = 0; index < _surfaces.Count; index++)
                {
                    UIRenderSurface candidate = _surfaces[index];
                    if (ReferenceEquals(candidate, _published) || !candidate.IsPoolOnly ||
                        !MatchesOwner(candidate, owner))
                        continue;
                    ulong oldBytes = checked((ulong)candidate.Width * candidate.Height * _bytesPerPixel);
                    if (_allocatedBytes - oldBytes + requestedBytes > MaximumBytes)
                        continue;
                    try { candidate.Resize(width, height); }
                    catch (Exception error)
                    {
                        failure = $"UI.RenderSurface.ResizeFailed: {error.Message}";
                        return false;
                    }
                    _allocatedBytes = _allocatedBytes - oldBytes + requestedBytes;
                    available = candidate;
                    break;
                }
            if (available is null)
            {
                if (_surfaces.Count >= MaximumSlots ||
                    _allocatedBytes + requestedBytes > MaximumBytes)
                {
                    failure = "UI.RenderSurface.Capacity: all bounded generations are retained.";
                    return false;
                }
                try
                {
                    if (owner is null)
                        available = new UIRenderSurface(width, height, _internalFormat, _format, _type);
                    else
                    {
                        using var creationOwner = GenericRenderObject.PushApiWrapperCreationOwner(owner);
                        available = new UIRenderSurface(width, height, _internalFormat, _format, _type);
                    }
                    available.OwnerRenderer = owner;
                    available.OwnerBackendGeneration = owner?.BackendGeneration ?? 0;
                    _surfaces.Add(available);
                    _allocatedBytes += requestedBytes;
                }
                catch (Exception error)
                {
                    failure = $"UI.RenderSurface.AllocationFailed: {error.Message}";
                    return false;
                }
            }
            ulong generation = checked(++_nextGeneration);
            available.Reserve(generation);
            surface = available;
            return true;
        }
    }

    private static bool MatchesOwner(UIRenderSurface surface, AbstractRenderer? owner)
        => !surface.OwnerRetired && ReferenceEquals(surface.OwnerRenderer, owner) &&
            surface.OwnerBackendGeneration == (owner?.BackendGeneration ?? 0);

    /// <summary>Retires one native owner after known GPU idle without borrowing its slots elsewhere.</summary>
    internal void RetireOwner(AbstractRenderer owner, long backendGeneration)
    {
        lock (_gate)
        {
            foreach (UIRenderSurface surface in _surfaces)
                if (ReferenceEquals(surface.OwnerRenderer, owner) &&
                    surface.OwnerBackendGeneration == backendGeneration)
                    surface.OwnerRetired = true;
            SweepRetiredOwnerSurfaces(owner);
        }
    }

    private void SweepRetiredOwnerSurfaces(AbstractRenderer? currentOwner)
    {
        for (int index = _surfaces.Count - 1; index >= 0; index--)
        {
            UIRenderSurface surface = _surfaces[index];
            if (!surface.OwnerRetired || !surface.IsPoolOnly ||
                !ReferenceEquals(surface.OwnerRenderer, currentOwner) ||
                (!RuntimeEngine.IsRenderThread && RuntimeEngine.RenderThreadId != 0))
                continue;
            ulong bytes = checked((ulong)surface.Width * surface.Height * _bytesPerPixel);
            surface.Release();
            _surfaces.RemoveAt(index);
            _allocatedBytes -= bytes;
            if (ReferenceEquals(surface, _published))
                _published = null;
        }
    }

    /// <summary>
    /// Promotes a reserved generation after the caller supplies the completion fence
    /// for the exact required producer batch that wrote this surface. The pool
    /// cannot infer fence provenance from a backend fence alone.
    /// </summary>
    internal bool TryPublish(UIRenderSurface surface, ulong generation, XRGpuFence? producerFence,
        AbstractRenderer? renderer = null)
    {
        if (producerFence is null || producerFence.IsDisposed ||
            producerFence.SubmissionStatus != EGpuFenceSubmissionStatus.Submitted ||
            producerFence.Poll() != EGpuFenceStatus.Signaled)
            return false;
        lock (_gate)
        {
            renderer ??= AbstractRenderer.Current;
            if (_disposed || !_surfaces.Contains(surface) ||
                !surface.TryComplete(generation, renderer, renderer?.BackendGeneration ?? 0))
                return false;
            _published = surface;
            return true;
        }
    }

    /// <summary>Promotes the exact UI receipt after its authoring, artifact, and GPU use retire.</summary>
    internal bool TryPublish(UIRenderSurface surface, ulong generation,
        UIReceiptCompletionProbe producer)
    {
        if (!producer.IsCompleted)
            return false;
        lock (_gate)
        {
            if (_disposed || !_surfaces.Contains(surface) ||
                !surface.TryComplete(generation, producer.Renderer, producer.BackendGeneration))
                return false;
            _published = surface;
            return true;
        }
    }

    internal int SlotCount
    {
        get { lock (_gate) return _surfaces.Count; }
    }

    public void Dispose()
    {
        UIRenderSurface[] detached;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            detached = _surfaces.ToArray();
            _surfaces.Clear();
            _published = null;
            _allocatedBytes = 0;
        }
        for (int index = 0; index < detached.Length; index++)
            detached[index].Release();
    }
}
