using XREngine;

namespace XREngine.Rendering.GI.Contracts;

/// <summary>
/// Registry-owned immutable metadata for one selectable GI representation.
/// </summary>
public sealed class GlobalIlluminationProviderDescriptor
{
    public GlobalIlluminationProviderDescriptor(
        string id,
        string displayName,
        EGlobalIlluminationMode selectionMode,
        EGlobalIlluminationHostCapability requiredCapabilities,
        EGlobalIlluminationContribution contributions,
        EGlobalIlluminationConsumer supportedConsumers,
        Type? settingsType,
        EGlobalIlluminationProviderFeature features,
        Func<IGlobalIlluminationModule>? moduleFactory,
        GlobalIlluminationSupportResult staticSupport)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(staticSupport);

        Id = id;
        DisplayName = displayName;
        SelectionMode = selectionMode;
        RequiredCapabilities = requiredCapabilities;
        Contributions = contributions;
        SupportedConsumers = supportedConsumers;
        SettingsType = settingsType;
        Features = features;
        ModuleFactory = moduleFactory;
        StaticSupport = staticSupport;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public EGlobalIlluminationMode SelectionMode { get; }
    public EGlobalIlluminationHostCapability RequiredCapabilities { get; }
    public EGlobalIlluminationContribution Contributions { get; }

    /// <summary>
    /// Consumers for which this provider can supply the declared contribution.
    /// Omitted consumers are explicitly unsupported; they do not inherit a
    /// screen-space result merely because it was selected for deferred opaque shading.
    /// </summary>
    public EGlobalIlluminationConsumer SupportedConsumers { get; }
    /// <summary>Optional provider-owned authoring component or settings schema.</summary>
    public Type? SettingsType { get; }

    /// <summary>
    /// Registered authoring, debug, and baking surfaces. These flags do not
    /// override <see cref="StaticSupport"/> or advertise unfinished providers as runnable.
    /// </summary>
    public EGlobalIlluminationProviderFeature Features { get; }

    /// <summary>
    /// Creates the provider-owned module. A null factory means the descriptor is
    /// explicitly unsupported and cannot allocate resources or graph work.
    /// </summary>
    public Func<IGlobalIlluminationModule>? ModuleFactory { get; }

    /// <summary>
    /// Reports static host-independent availability. Per-resource readiness belongs to the module runtime.
    /// </summary>
    public GlobalIlluminationSupportResult StaticSupport { get; }
}
