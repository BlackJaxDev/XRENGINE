namespace XREngine.Rendering;

/// <summary>
/// Allocation-free handle to one publisher-owned deferred render scope.
/// </summary>
public readonly record struct DeferredRenderBindingPublication(
    IDeferredRenderBindingPublisher? Publisher,
    ulong Token)
{
    /// <summary>Gets whether this handle carries a producer scope.</summary>
    public bool IsEmpty => Publisher is null || Token == 0;

    /// <summary>Activates the captured scope when one is present.</summary>
    public bool TryActivate()
        => IsEmpty || Publisher!.TryActivateDeferredPublication(Token);

    /// <summary>Restores a successfully activated scope.</summary>
    public void Deactivate()
    {
        if (!IsEmpty)
            Publisher!.DeactivateDeferredPublication(Token);
    }
}
