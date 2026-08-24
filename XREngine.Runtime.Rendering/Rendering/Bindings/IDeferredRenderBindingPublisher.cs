namespace XREngine.Rendering;

/// <summary>
/// Preserves a publisher's short-lived producer scope until a backend drains a
/// deferred render request.
/// </summary>
/// <remarks>
/// The token must identify preallocated publisher-owned state. Capturing and
/// activating a publication are render hot-path operations and must not
/// allocate. An activated publication must always be deactivated in a
/// <see langword="finally"/> block.
/// </remarks>
public interface IDeferredRenderBindingPublisher : IRenderBindingPublisher
{
    /// <summary>Captures the current producer scope and returns its token.</summary>
    ulong CaptureDeferredPublication();

    /// <summary>Activates a previously captured producer scope.</summary>
    bool TryActivateDeferredPublication(ulong token);

    /// <summary>Restores the state saved by a successful activation.</summary>
    void DeactivateDeferredPublication(ulong token);
}
