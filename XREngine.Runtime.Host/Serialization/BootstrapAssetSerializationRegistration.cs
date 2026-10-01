using XREngine.Data;
using XREngine.Serialization;

namespace XREngine;

/// <summary>Installs shared host settings serializers and persisted enum aliases.</summary>
public static class BootstrapAssetSerializationRegistration
{
    public static IDisposable Install()
        => RegistrationLeaseGroup.Create(static leases =>
        {
            leases.Add(BootstrapPublishedCookedAssetRegistration.Install());
            leases.Add(YamlEnumAliasRegistry.Install(
                "XREngine.Runtime.Host",
                "JobSystem",
                EDebugShapePopulationMode.Tasks));
        });
}
