using XREngine.Core.Files.Caching;
using XREngine.Data;

namespace XREngine.Rendering.Models.Caching;

/// <summary>Installs ModelingBridge-owned model cache and importer policies.</summary>
public static class ModelingAssetRegistration
{
    public static IDisposable Install(Type modelAssetType, IModelCachePolicyServices services)
    {
        ArgumentNullException.ThrowIfNull(modelAssetType);
        ArgumentNullException.ThrowIfNull(services);

        return RegistrationLeaseGroup.Create(leases =>
        {
            leases.Add(ThirdPartyCacheCodecRegistry.Install(new ModelBinaryCacheCodec(modelAssetType)));
            leases.Add(ThirdPartyCachePathPolicies.Install(new ModelCachePathPolicy(modelAssetType, services)));
            leases.Add(ThirdPartyAssetTypeRegistry.Install("XREngine.Runtime.ModelingBridge", modelAssetType));
        });
    }
}
