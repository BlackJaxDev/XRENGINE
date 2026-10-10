namespace XREngine.Core.Files;

/// <summary>Prepares source-owned runtime resources before a catalog root or its dependencies hydrate.</summary>
public interface IRuntimeAssetPreparationSource
{
    /// <summary>Completes the root's resource admission or fails before its object graph is constructed.</summary>
    Task PrepareAssetAsync(string catalogPath, CancellationToken cancellationToken = default);
}
