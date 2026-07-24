namespace XREngine.Rendering;

/// <summary>
/// Describes whether OpenXR owns the native graphics device used by a renderer.
/// </summary>
public interface IOpenXrDeviceOwnershipBackendCapability
{
    bool UsesOpenXrManagedDeviceCreation { get; }
}
