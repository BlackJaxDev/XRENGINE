namespace XREngine.Cutscenes;

/// <summary>Installs the runtime type contracts of the cutscene asset, track, and clip types.</summary>
public static class CutsceneSerializationRegistration
{
    /// <summary>Registers the contracts. Dispose the result to remove them.</summary>
    public static IDisposable Install()
        => global::XREngine.Generated.GeneratedRuntimeContracts_XREngine_Runtime_CutsceneIntegration.Install();
}
