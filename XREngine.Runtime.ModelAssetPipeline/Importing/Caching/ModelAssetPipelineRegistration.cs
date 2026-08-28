using XREngine.Core.Files.Caching;
using XREngine.Data;
using XREngine.Rendering.Models;

namespace XREngine.Rendering.Models.Caching;

/// <summary>Installs ModelAssetPipeline-owned model cache and importer policies.</summary>
public static class ModelAssetPipelineRegistration
{
    /// <summary>Installs the complete runtime model import and cache surface.</summary>
    public static IDisposable Install(AssetManager assets, Type modelAssetType)
    {
        ArgumentNullException.ThrowIfNull(assets);
        IRuntimeThirdPartyAssetLoadingServices fallback = RuntimeThirdPartyAssetLoadingServices.Current;
        IRuntimeModelImportServices modelImportFallback = RuntimeModelImportServices.Current;
        return RegistrationLeaseGroup.Create(leases =>
        {
            leases.Add(Install(modelAssetType, new ModelCachePolicyServices(assets)));
            leases.Add(RuntimeModelImportServices.Install(
                new ModelAssetPipelineRuntimeModelImportServices(assets, modelImportFallback)));
            leases.Add(RuntimeModelSceneLoadingServices.Install(
                new ModelAssetPipelineRuntimeModelSceneLoadingServices()));
            leases.Add(RuntimeThirdPartyAssetLoadingServices.Install(
                new ModelPrefabAssetLoadingServices(assets, fallback)));
        });
    }

    public static IDisposable Install(Type modelAssetType, IModelCachePolicyServices services)
    {
        ArgumentNullException.ThrowIfNull(modelAssetType);
        ArgumentNullException.ThrowIfNull(services);

        return RegistrationLeaseGroup.Create(leases =>
        {
            leases.Add(ThirdPartyCacheCodecRegistry.Install(new ModelBinaryCacheCodec(modelAssetType)));
            leases.Add(ThirdPartyCachePathPolicies.Install(new ModelCachePathPolicy(modelAssetType, services)));
            leases.Add(ThirdPartyAssetTypeRegistry.Install(
                "XREngine.Runtime.ModelAssetPipeline",
                modelAssetType,
                typeof(ModelImportOptions),
                ModelPrefabSourceExtensions.All));
        });
    }
}
