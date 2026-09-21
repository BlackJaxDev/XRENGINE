using XREngine;
using XREngine.Components.Lights;
using XREngine.Rendering.GI.DDGI;
using XREngine.Rendering.GI.LightProbes;
using XREngine.Rendering.GI.RadianceCascades;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.GI.Contracts;

/// <summary>
/// The sole serialized-selection boundary. Hosts resolve plans here and never choose providers by concrete type.
/// </summary>
public static class GlobalIlluminationProviderRegistry
{
    private static readonly IReadOnlyDictionary<EGlobalIlluminationMode, GlobalIlluminationProviderDescriptor> Descriptors =
        new Dictionary<EGlobalIlluminationMode, GlobalIlluminationProviderDescriptor>
        {
            [EGlobalIlluminationMode.LightProbesAndIbl] = Supported(
                "light-probes-ibl", "Light Probes and IBL", EGlobalIlluminationMode.LightProbesAndIbl,
                EGlobalIlluminationHostCapability.ProbeSampling,
                EGlobalIlluminationContribution.Diffuse | EGlobalIlluminationContribution.Specular,
                EGlobalIlluminationConsumer.DeferredOpaque | EGlobalIlluminationConsumer.ForwardOpaque |
                EGlobalIlluminationConsumer.Transparent,
                "Existing probe/IBL integration is available through the generic PBR-lighting host contract."),
            [EGlobalIlluminationMode.DDGI] = Supported(
                "ddgi", "DDGI", EGlobalIlluminationMode.DDGI,
                EGlobalIlluminationHostCapability.ScreenSpaceDiffuseOutput,
                EGlobalIlluminationContribution.Diffuse | EGlobalIlluminationContribution.Debug,
                EGlobalIlluminationConsumer.DeferredOpaque,
                "Experimental DDGI supplies deferred-opaque screen composition only; forward, transparent, and world-space consumers are unsupported.",
                typeof(DDGIVolumeComponent),
                EGlobalIlluminationProviderFeature.Authoring |
                EGlobalIlluminationProviderFeature.DebugPresentation |
                EGlobalIlluminationProviderFeature.Baking),
            [EGlobalIlluminationMode.PathTracing] = Unavailable(
                "restir", "ReSTIR / Path Tracing", EGlobalIlluminationMode.PathTracing,
                "ReSTIR exists in source but has no verified modular host support."),
            [EGlobalIlluminationMode.VoxelConeTracing] = Unavailable(
                "voxel-cone-tracing", "Voxel Cone Tracing", EGlobalIlluminationMode.VoxelConeTracing,
                "Voxel cone tracing exists in source but has no verified modular host support."),
            [EGlobalIlluminationMode.LightVolumes] = Unavailable(
                "light-volumes", "Light Volumes", EGlobalIlluminationMode.LightVolumes,
                "Light-volume source is not evidence of a complete LPV provider or modular host support."),
            [EGlobalIlluminationMode.RadianceCascades] = Experimental(
                "radiance-cascades", "Radiance Cascades", EGlobalIlluminationMode.RadianceCascades,
                EGlobalIlluminationHostCapability.ScreenSpaceDiffuseOutput,
                EGlobalIlluminationContribution.Diffuse | EGlobalIlluminationContribution.Debug,
                EGlobalIlluminationConsumer.DeferredOpaque,
                "Radiance cascades have a modular material-shaded screen resolve, but no live cascade injection, propagation, or update implementation; the provider remains unsupported until authored-volume lifecycle and both-host runtime validation are complete.",
                typeof(RadianceCascadeComponent),
                EGlobalIlluminationProviderFeature.Authoring |
                EGlobalIlluminationProviderFeature.DebugPresentation),
            [EGlobalIlluminationMode.SurfelGI] = Unavailable(
                "surfel-gi", "Surfel GI", EGlobalIlluminationMode.SurfelGI,
                "Surfel GI exists in source but has no verified modular host support."),
        };

    public static GlobalIlluminationPlan Resolve(
        EGlobalIlluminationMode requestedMode,
        IGlobalIlluminationHostAdapter host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (requestedMode == EGlobalIlluminationMode.None)
            return new(requestedMode, null, host, GlobalIlluminationSupportResult.Supported("GI is disabled."));

        if (!Descriptors.TryGetValue(requestedMode, out GlobalIlluminationProviderDescriptor? descriptor))
        {
            return new(requestedMode, null, host,
                GlobalIlluminationSupportResult.Unsupported($"No GI provider is registered for selection '{requestedMode}'."));
        }

        if (host.IsMinimalOutput)
        {
            return new(requestedMode, descriptor, host,
                GlobalIlluminationSupportResult.Unsupported(
                    $"GI provider '{descriptor.DisplayName}' requires surface output, but host '{host.HostId}' is minimal-output."));
        }

        if ((host.Capabilities & descriptor.RequiredCapabilities) != descriptor.RequiredCapabilities)
        {
            return new(requestedMode, descriptor, host,
                GlobalIlluminationSupportResult.Unsupported(
                    $"GI provider '{descriptor.DisplayName}' requires '{descriptor.RequiredCapabilities}', which host '{host.HostId}' does not supply."));
        }

        return new(requestedMode, descriptor, host, descriptor.StaticSupport);
    }

    public static GlobalIlluminationProviderDescriptor GetRequiredDescriptor(EGlobalIlluminationMode selectionMode)
        => Descriptors.TryGetValue(selectionMode, out GlobalIlluminationProviderDescriptor? descriptor)
            ? descriptor
            : throw new InvalidOperationException($"No GI provider is registered for selection '{selectionMode}'.");

    /// <summary>
    /// Lets a selected provider contribute resources without exposing provider types to a host pipeline.
    /// </summary>
    public static void DeclareResources(
        RenderPipelineResourceLayoutBuilder builder,
        in GlobalIlluminationModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!context.Plan.IsSupported || context.Plan.Provider?.ModuleFactory is not { } factory)
            return;

        factory().DeclareResources(builder, context);
    }

    /// <summary>
    /// Lets the selected provider own its generation-key variant. Hosts never
    /// inspect provider layout, authored volume, or baked-asset details.
    /// </summary>
    public static RenderPipelineResourceVariant BuildResourceVariant(
        GlobalIlluminationPlan plan,
        XRViewport? viewport)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.IsSupported && plan.Provider?.ModuleFactory is { } factory
            ? factory().BuildResourceVariant(viewport)
            : default;
    }

    /// <summary>
    /// Lets a selected provider contribute graph commands at a declared host anchor.
    /// </summary>
    public static void ContributePasses(
        ViewportRenderCommandContainer commands,
        in GlobalIlluminationModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (!context.Plan.IsSupported || context.Plan.Provider?.ModuleFactory is not { } factory)
            return;

        factory().ContributePasses(commands, context);
    }

    private static GlobalIlluminationProviderDescriptor Supported(
        string id,
        string displayName,
        EGlobalIlluminationMode selectionMode,
        EGlobalIlluminationHostCapability requiredCapabilities,
        EGlobalIlluminationContribution contributions,
        EGlobalIlluminationConsumer supportedConsumers,
        string diagnostic,
        Type? settingsType = null,
        EGlobalIlluminationProviderFeature features = EGlobalIlluminationProviderFeature.None)
        => new(id, displayName, selectionMode, requiredCapabilities, contributions, supportedConsumers,
            settingsType, features,
            moduleFactory: selectionMode switch
            {
                EGlobalIlluminationMode.LightProbesAndIbl => static () => new LightProbesAndIblGlobalIlluminationModule(),
                EGlobalIlluminationMode.DDGI => static () => new DDGIGlobalIlluminationModule(),
                _ => null,
            },
            GlobalIlluminationSupportResult.Supported(diagnostic));

    private static GlobalIlluminationProviderDescriptor Unavailable(
        string id,
        string displayName,
        EGlobalIlluminationMode selectionMode,
        string diagnostic,
        Type? settingsType = null,
        EGlobalIlluminationProviderFeature features = EGlobalIlluminationProviderFeature.None)
        => new(id, displayName, selectionMode, EGlobalIlluminationHostCapability.None,
            EGlobalIlluminationContribution.None, EGlobalIlluminationConsumer.None, settingsType, features, moduleFactory: null,
            GlobalIlluminationSupportResult.Unsupported(diagnostic));

    private static GlobalIlluminationProviderDescriptor Experimental(
        string id,
        string displayName,
        EGlobalIlluminationMode selectionMode,
        EGlobalIlluminationHostCapability requiredCapabilities,
        EGlobalIlluminationContribution contributions,
        EGlobalIlluminationConsumer supportedConsumers,
        string diagnostic,
        Type? settingsType = null,
        EGlobalIlluminationProviderFeature features = EGlobalIlluminationProviderFeature.None)
        => new(id, displayName, selectionMode, requiredCapabilities, contributions, supportedConsumers,
            settingsType, features,
            moduleFactory: selectionMode == EGlobalIlluminationMode.RadianceCascades
                ? static () => new RadianceCascadesGlobalIlluminationModule()
                : null,
            GlobalIlluminationSupportResult.Unsupported(diagnostic));
}
