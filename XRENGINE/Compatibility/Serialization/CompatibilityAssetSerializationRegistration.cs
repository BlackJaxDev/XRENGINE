using XREngine.Data;
using XREngine.Serialization;

namespace XREngine;

/// <summary>Installs the facade's remaining serialized settings compatibility aliases.</summary>
public static class CompatibilityAssetSerializationRegistration
{
    public static IDisposable Install()
        => RegistrationLeaseGroup.Create(static leases =>
        {
            leases.Add(FacadePublishedCookedAssetRegistration.Install());
            leases.Add(YamlEnumAliasRegistry.Install(
                "XRENGINE facade settings",
                "JobSystem",
                EDebugShapePopulationMode.Tasks));
        });
}
