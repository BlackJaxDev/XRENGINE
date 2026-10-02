using XREngine.Data.Core;

namespace XREngine.Core.Files;

/// <summary>Optional admission and lifetime accounting for synchronous asset hydration.</summary>
public interface IRuntimeAssetIntegrationSource
{
    /// <summary>Reads verified bytes and admits one non-awaiting hydration batch. Dispose the lease after publication or rollback.</summary>
    Task<RuntimeAssetIntegration> ReadForIntegrationAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Records only an actually published ownership batch. Managed allocation includes temporary hydration allocations.</summary>
    void RetainAsset(string path, int serializedBytes, long managedAllocationBytes, ObjectCacheOwnership ownership);

    /// <summary>Removes accounting after the corresponding ownership batch was successfully destroyed.</summary>
    void ReleaseAsset(string path);
}
