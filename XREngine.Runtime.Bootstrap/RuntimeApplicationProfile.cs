namespace XREngine.Runtime.Bootstrap;

/// <summary>
/// Explicit application composition profile. It determines which optional
/// adapters and local-device services are installed; it does not describe
/// renderer or input implementation details.
/// </summary>
public sealed record RuntimeApplicationProfile(
    string Name,
    RuntimeAdapterProfile AdapterProfile,
    bool AllowsWindows,
    bool AllowsVr,
    bool RegisterRendererBackends)
{
    public bool AllowsLocalInput => AdapterProfile.HasFlag(RuntimeAdapterProfile.Input);
    public bool AllowsAudio => AdapterProfile.HasFlag(RuntimeAdapterProfile.Audio);

    public RuntimeApplicationCapabilities ToCapabilities()
        => new(
            IsConfigured: true,
            AllowsLocalInput,
            AllowsWindows,
            AllowsAudio,
            AllowsVr,
            RegisterRendererBackends);

    /// <summary>Dedicated-server composition with no local devices or renderer backend modules.</summary>
    public static RuntimeApplicationProfile HeadlessServer { get; } = new(
        nameof(HeadlessServer),
        RuntimeAdapterProfile.Animation | RuntimeAdapterProfile.ModelAssetPipeline,
        AllowsWindows: false,
        AllowsVr: false,
        RegisterRendererBackends: false);

    /// <summary>Complete editor composition, including local input, audio, windows, and VR.</summary>
    public static RuntimeApplicationProfile Editor { get; } = new(
        nameof(Editor),
        RuntimeAdapterProfile.All,
        AllowsWindows: true,
        AllowsVr: true,
        RegisterRendererBackends: true);

    /// <summary>Complete standalone desktop-client composition without VR startup.</summary>
    public static RuntimeApplicationProfile DesktopClient { get; } = new(
        nameof(DesktopClient),
        RuntimeAdapterProfile.All,
        AllowsWindows: true,
        AllowsVr: false,
        RegisterRendererBackends: true);

    /// <summary>Complete standalone VR-client composition.</summary>
    public static RuntimeApplicationProfile VrClient { get; } = new(
        nameof(VrClient),
        RuntimeAdapterProfile.All,
        AllowsWindows: true,
        AllowsVr: true,
        RegisterRendererBackends: true);
}
