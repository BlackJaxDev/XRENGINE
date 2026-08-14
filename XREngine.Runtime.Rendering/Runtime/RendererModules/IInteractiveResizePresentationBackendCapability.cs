namespace XREngine.Rendering;

/// <summary>
/// Provides immutable backend evidence that a native modal resize callback may
/// keep presenting the window's last completed package through compositor scaling.
/// </summary>
public interface IInteractiveResizePresentationBackendCapability
{
    bool TryGetInteractiveResizePresentationPackage(
        out ulong presentationPackageId,
        out EInteractiveResizeDispatchReason unavailableReason);
}
