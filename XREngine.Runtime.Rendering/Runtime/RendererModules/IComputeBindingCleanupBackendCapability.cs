namespace XREngine.Rendering;

/// <summary>
/// Backend operation used to clear compute-storage binding state after a stable compute pass.
/// </summary>
public interface IComputeBindingCleanupBackendCapability
{
    /// <summary>
    /// Clears storage-buffer bindings from zero through <paramref name="maxBinding"/>, inclusive.
    /// </summary>
    void ClearStorageBufferBindings(uint maxBinding);
}
