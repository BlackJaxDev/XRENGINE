using System.Diagnostics.CodeAnalysis;

namespace XREngine.Core.Files;

/// <summary>Immutable asset identities and virtual roots supplied by a read-only runtime package.</summary>
public interface IRuntimeAssetCatalog
{
    string EngineAssetsRoot { get; }
    string GameAssetsRoot { get; }
    bool TryGetAsset(string path, [NotNullWhen(true)] out RuntimeAssetCatalogEntry? entry);
}
