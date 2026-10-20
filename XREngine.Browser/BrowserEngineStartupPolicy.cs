using XREngine.Data.Vectors;

namespace XREngine.Browser;

/// <summary>Supplies the browser's caller-owned, windowless engine startup policy.</summary>
internal sealed class BrowserEngineStartupPolicy : IRuntimeEngineStartupPolicy
{
    public static BrowserEngineStartupPolicy Instance { get; } = new();

    private BrowserEngineStartupPolicy() { }

    public GameStartupSettings CreateDefaultGameSettings()
    {
        GameStartupSettings settings = new()
        {
            RunWithoutWindows = true,
            LogOutputToFile = false,
            NetworkingType = ENetworkingType.Local,
            TargetUpdatesPerSecond = 60.0f,
            TargetFramesPerSecond = 60.0f,
            FixedFramesPerSecond = 60.0f,
        };
        settings.RecalcChildMatricesLoopTypeOverride.SetOverride(ELoopType.Sequential);
        settings.TickGroupedItemsInParallelOverride.SetOverride(false);
        settings.DefaultUserSettings.PhysicsLibrary = EPhysicsLibrary.Jolt;
        settings.AudioArchitectureV2Override.SetOverride(true);
        settings.AudioTransportOverride.SetOverride(EAudioTransport.WebAudio);
        settings.AudioEffectsOverride.SetOverride(EAudioEffects.Passthrough);
        return settings;
    }

    public IVector2 GetPrimaryDisplaySize()
        => throw new NotSupportedException("A browser world has no desktop primary display; use the canvas surface host.");

    public void PrepareSettings(GameStartupSettings settings)
    {
        if (settings.StartupWindows.Count != 0 || !settings.RunWithoutWindows)
            throw new NotSupportedException("Browser engine startup does not accept desktop windows.");
        if (settings.AudioEffectsOverride is { HasOverride: true, Value: not EAudioEffects.Passthrough })
            throw new NotSupportedException("WebAudio.EffectsUnsupported: browser output supports Passthrough, not SteamAudio or OpenAL EFX.");
        if (settings.AudioTransportOverride is { HasOverride: true, Value: not EAudioTransport.WebAudio })
            throw new NotSupportedException("WebAudio.TransportUnsupported: browser output requires WebAudio.");
    }

    public void PrepareNetworking(GameStartupSettings settings)
    {
        if (settings.NetworkingType != ENetworkingType.Local)
            throw new NotSupportedException("Browser realtime networking requires the WebSocket transport leaf.");
    }
}
