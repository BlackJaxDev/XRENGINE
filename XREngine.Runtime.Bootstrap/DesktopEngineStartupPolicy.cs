using XREngine.Data;
using XREngine.Data.Vectors;
using XREngine.Networking;
using XREngine.Rendering;
using XREngine.Runtime.Platform.Desktop;

namespace XREngine.Runtime.Bootstrap;

/// <summary>Applies desktop display defaults, sandbox settings, and managed launch handoffs.</summary>
internal sealed class DesktopEngineStartupPolicy : IRuntimeEngineStartupPolicy
{
    public static DesktopEngineStartupPolicy Instance { get; } = new();
    private bool _environmentRealtimeHandoffApplied;

    private DesktopEngineStartupPolicy() { }

    /// <summary>
    /// Installs this policy as the engine startup policy and installs the desktop camera defaults.
    /// Desktop hosts use GT7 tonemapping. The shared default stays Mobius, because the cooked web
    /// Default output route supports only Mobius. The install runs before the host creates any
    /// render pipeline, so editor, desktop and VR eye cameras start with GT7.
    /// </summary>
    public static IDisposable Install()
        => RegistrationLeaseGroup.Create(static leases =>
        {
            leases.Add(RuntimeEngineStartupPolicyServices.Install(Instance));
            leases.Add(TonemappingSettings.InstallHostDefault(ETonemappingType.GT7));
        });

    public IVector2 GetPrimaryDisplaySize() => DesktopPlatformBackend.GetPrimaryDisplaySize();

    public GameStartupSettings CreateDefaultGameSettings()
    {
        const int width = 1920;
        const int height = 1080;
        IVector2 display = GetPrimaryDisplaySize();
        return new GameStartupSettings
        {
            StartupWindows =
            [
                new()
                {
                    WindowTitle = "XRENGINE",
                    TargetWorld = new Scene.XRWorld(),
                    WindowState = EWindowState.Windowed,
                    X = display.X / 2 - width / 2,
                    Y = display.Y / 2 - height / 2,
                    Width = width,
                    Height = height,
                }
            ],
            DefaultUserSettings = new UserSettings { VSync = EVSyncMode.Off },
            TargetUpdatesPerSecond = 90.0f,
            TargetFramesPerSecond = 90.0f,
            FixedFramesPerSecond = 45.0f,
        };
    }

    public void PrepareSettings(GameStartupSettings settings)
    {
        if (Engine.CurrentProject is null)
            Engine.LoadSandboxSettings();
    }

    public void PrepareNetworking(GameStartupSettings settings)
    {
        if (settings.NetworkingType == ENetworkingType.Client
            && ManagedClientWorldLoader.IsConfigured
            && !ManagedClientWorldLoader.WasApplied
            && !settings.IgnoreEnvironmentRealtimeHandoffs)
            throw new InvalidOperationException("Managed client configuration was supplied, but its verified world was not loaded before networking startup.");

        if (!_environmentRealtimeHandoffApplied
            && !ManagedClientWorldLoader.IsConfigured
            && !settings.IgnoreEnvironmentRealtimeHandoffs
            && DesktopRealtimeJoinHandoffLoader.TryApplyFromEnvironment(settings, out _, out string? handoffSource))
        {
            _environmentRealtimeHandoffApplied = true;
            Debug.Networking("[Realtime Handoff] Applied join payload from {0}.", handoffSource ?? "<unknown>");
        }
    }
}
