using XREngine;

namespace XREngine.Rendering.GI.Contracts;

/// <summary>
/// Immutable selection snapshot shared by resource layout, graph construction, bindings, and runtime creation.
/// </summary>
public sealed class GlobalIlluminationPlan
{
    internal GlobalIlluminationPlan(
        EGlobalIlluminationMode requestedMode,
        GlobalIlluminationProviderDescriptor? provider,
        IGlobalIlluminationHostAdapter host,
        GlobalIlluminationSupportResult support)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(support);

        RequestedMode = requestedMode;
        Provider = provider;
        Host = host;
        Support = support;
        ResourceLayoutIdentity = provider is null
            ? $"gi-disabled:{host.HostId}"
            : $"gi:{host.HostId}:{provider.Id}:{(ulong)host.Capabilities:X}";
    }

    public EGlobalIlluminationMode RequestedMode { get; }
    public GlobalIlluminationProviderDescriptor? Provider { get; }
    public IGlobalIlluminationHostAdapter Host { get; }
    public GlobalIlluminationSupportResult Support { get; }
    public string ResourceLayoutIdentity { get; }

    public bool IsDisabled => Provider is null;
    public bool IsSupported => Support.State == EGlobalIlluminationSupportState.Supported;

    /// <summary>
    /// True when native opaque shading must export material surfaces for a
    /// provider-owned screen-space resolve. This is a binding requirement, not
    /// an algorithm identity.
    /// </summary>
    public bool RequiresNativeMaterialSurfaceExports
        => IsSupported && Provider is not null &&
            (Host.Capabilities & EGlobalIlluminationHostCapability.NativeOpaqueSurface) != 0 &&
            (Provider.RequiredCapabilities & EGlobalIlluminationHostCapability.ScreenSpaceDiffuseOutput) != 0;

    /// <summary>
    /// Indicates that the native probe/IBL bindings remain required by the
    /// selected plan. DDGI retains them for its existing reflection/specular
    /// path while its diffuse result is resolved separately.
    /// </summary>
    public bool RequiresNativeProbeIblBindings
        => IsSupported && Provider is not null &&
            (Host.Capabilities & EGlobalIlluminationHostCapability.ProbeSampling) != 0;

    /// <summary>
    /// Indicates that the selected provider supplies opaque-surface diffuse
    /// radiance separately, so the baseline probe diffuse term must be omitted
    /// while probe specular remains available.
    /// </summary>
    public bool ReplacesProbeDiffuse
        => IsSupported && Provider is not null &&
            (Provider.Contributions & EGlobalIlluminationContribution.Diffuse) != 0 &&
            (Provider.SupportedConsumers & EGlobalIlluminationConsumer.DeferredOpaque) != 0 &&
            (Provider.RequiredCapabilities & EGlobalIlluminationHostCapability.ScreenSpaceDiffuseOutput) != 0;

    /// <summary>
    /// Returns an explicit result for a requested contribution consumer. This
    /// prevents a screen-space deferred output from being treated as a universal
    /// forward, transparent, or world-space sampling resource.
    /// </summary>
    public GlobalIlluminationSupportResult GetConsumerSupport(EGlobalIlluminationConsumer consumer)
    {
        if (!IsSupported || Provider is null)
            return GlobalIlluminationSupportResult.Unsupported(Support.Diagnostic);
        if (consumer == EGlobalIlluminationConsumer.None ||
            (Provider.SupportedConsumers & consumer) != consumer)
        {
            return GlobalIlluminationSupportResult.Unsupported(
                $"GI provider '{Provider.DisplayName}' does not support consumer '{consumer}'. " +
                $"Supported consumers: '{Provider.SupportedConsumers}'.");
        }

        return GlobalIlluminationSupportResult.Supported(
            $"GI provider '{Provider.DisplayName}' supports consumer '{consumer}'.");
    }

    /// <summary>
    /// Identifies a deliberately temporary route where existing commands still execute while Phase 2 extracts the module.
    /// </summary>
    public bool Selects(EGlobalIlluminationMode mode)
        => RequestedMode == mode;

    /// <summary>
    /// Returns true only when the requested provider was admitted by the host
    /// and represents this exact mode. Unsupported selections never schedule
    /// resources or graph work merely because their source code exists.
    /// </summary>
    public bool IsSelectedAndSupported(EGlobalIlluminationMode mode)
        => IsSupported && Selects(mode);
}
