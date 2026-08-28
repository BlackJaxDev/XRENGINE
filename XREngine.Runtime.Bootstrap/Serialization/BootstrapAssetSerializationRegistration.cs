using XREngine.Data;
using XREngine.Serialization;

namespace XREngine;

/// <summary>Installs Bootstrap-owned settings serializers and persisted enum aliases.</summary>
public static class BootstrapAssetSerializationRegistration
{
    public static IDisposable Install()
        => RegistrationLeaseGroup.Create(static leases =>
        {
            leases.Add(BootstrapPublishedCookedAssetRegistration.Install());
            leases.Add(YamlEnumAliasRegistry.Install(
                "XREngine.Runtime.Bootstrap",
                "JobSystem",
                EDebugShapePopulationMode.Tasks));
        });
}
