namespace XREngine;

public readonly record struct RvcExperimentalExtensionPlan(
    ERvcExperimentalExtensionPolicy FragmentDensityMap,
    ERvcExperimentalExtensionPolicy MeshShader,
    ERvcExperimentalExtensionPolicy EyeTrackedFoveation,
    ERvcExperimentalExtensionPolicy PeripheralCheckerboard,
    string Diagnostic)
{
    public static RvcExperimentalExtensionPlan Resolve(
        in RvcCapabilityMatrix capabilities,
        bool meshShaderAvailable,
        bool allowPrototypeExtensions)
    {
        ERvcExperimentalExtensionPolicy Policy(bool available)
        {
            if (!available)
                return ERvcExperimentalExtensionPolicy.Disabled;
            return allowPrototypeExtensions
                ? ERvcExperimentalExtensionPolicy.EnabledForPrototype
                : ERvcExperimentalExtensionPolicy.CapabilityAdvertised;
        }

        return new(
            Policy(capabilities.FragmentDensityMapSupported),
            Policy(meshShaderAvailable),
            Policy(capabilities.OpenXrRuntimeFoveationSupported || capabilities.OpenXrQuadViewsSupported),
            allowPrototypeExtensions
                ? ERvcExperimentalExtensionPolicy.EnabledForPrototype
                : ERvcExperimentalExtensionPolicy.Disabled,
            allowPrototypeExtensions
                ? "RVC prototype-only extension paths may be selected by diagnostics."
                : "RVC experimental extension paths are advertised but disabled for production defaults.");
    }
}
