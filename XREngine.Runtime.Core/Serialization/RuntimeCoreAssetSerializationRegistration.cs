using XREngine.Core.Engine;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Scene;
using YamlDotNet.Serialization;

namespace XREngine.Serialization;

/// <summary>
/// Installs Runtime.Core-owned asset services, scene import identity, and transform YAML support.
/// </summary>
public static class RuntimeCoreAssetSerializationRegistration
{
    public static IDisposable Install(AssetManager assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        return RegistrationLeaseGroup.Create(leases =>
        {
            leases.Add(AssetSerializationServices.Install(new AssetManagerAssetSerializationServices(assets)));
            leases.Add(CookedBinaryObjectLifecycleServices.Install(RuntimeCookedBinaryObjectLifecycleServices.Instance));
            leases.Add(YamlSerializationContributions.Install(new RuntimeCoreYamlContribution()));
            leases.Add(ThirdPartyAssetTypeRegistry.Install("XREngine.Runtime.Core", typeof(XRScene)));
        });
    }

    private sealed class RuntimeCoreYamlContribution : IYamlSerializationContribution
    {
        public string OwnerName => "XREngine.Runtime.Core";

        public IEnumerable<IYamlTypeConverter> CreateTypeConverters()
            => [new TransformBaseYamlTypeConverter()];
    }

}
