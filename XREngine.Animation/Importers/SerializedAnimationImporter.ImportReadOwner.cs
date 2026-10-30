using XREngine.Data;

namespace XREngine.Animation.Importers;

public static partial class AnimYamlImporter
{
    private sealed class ImportReadOwner(
        RuntimeAssetReadLease read,
        IRuntimeFileDiscovery? discovery,
        long discoveryGeneration)
    {
        public RuntimeAssetReadLease Read { get; } = read;
        public IRuntimeFileDiscovery? Discovery { get; } = discovery;

        public void EnsureDiscoveryCurrent()
        {
            if (!RuntimeFileDiscoveryServices.IsCurrent(Discovery, discoveryGeneration))
                throw new OperationCanceledException(
                    "AssetSource.StaleFileDiscovery: the file discovery installation changed during the animation import.");
        }
    }
}
