using System.Globalization;
using XREngine.Fbx;
using XREngine.Rendering;
using XREngine.Rendering.Models.Caching;
using XREngine.Runtime.Bootstrap;
using XREngine.Runtime.Bootstrap.Builders;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using static XREngine.GameStartupSettings;

namespace XREngine.Networking;

/// <summary>
/// Dedicated realtime server entry point. Instance discovery, allocation, and asset delivery live outside
/// this engine process; this executable only accepts direct UDP joins against its loaded world.
/// </summary>
public static class Program
{
    private static readonly Guid ServerSessionId = ResolveConfiguredSessionId();
    private static readonly string? RequiredSessionToken = GetOptionalEnvironmentValue(XREngineEnvironmentVariables.SessionToken);
    private static readonly string UdpMulticastGroup = GetOptionalEnvironmentValue(XREngineEnvironmentVariables.UdpMulticastGroup) ?? "239.0.0.222";
    private static readonly int UdpMulticastPort = GetOptionalIntEnvironmentValue(XREngineEnvironmentVariables.UdpMulticastPort) ?? 5000;
    private static readonly int UdpBindPort = GetOptionalIntEnvironmentValue(XREngineEnvironmentVariables.UdpBindPort)
        ?? GetOptionalIntEnvironmentValue(XREngineEnvironmentVariables.UdpServerBindPort)
        ?? 5000;
    private static readonly int UdpAdvertisedPort = GetOptionalIntEnvironmentValue(XREngineEnvironmentVariables.UdpAdvertisedPort)
        ?? GetOptionalIntEnvironmentValue(XREngineEnvironmentVariables.UdpServerSendPort)
        ?? UdpBindPort;

    private static void Main()
    {
        using IDisposable modelAssetPipelineRegistration =
            ModelAssetPipelineRegistration.Install(Engine.Assets, typeof(XRPrefabSource));
        using IDisposable applicationServices =
            RuntimeApplicationBootstrap.Install(RuntimeApplicationProfile.HeadlessServer);
        Engine.ConfigureMemoryPolicy(EngineMemoryProfile.HeadlessServer);

        Engine.ServerSessionResolver = ResolveServerSession;
        Engine.ServerJoinAdmissionResolver = ResolveServerJoin;

        UnitTestingWorldSettings settings = UnitTestingWorldSettingsStore.Load(false);
        UnitTestingWorldSettingsStore.ApplyWorldKindOverride(settings);
        ConfigureFbxTraceLogging(settings);
        XRWorld targetWorld = BootstrapWorldFactory.CreateServerDefaultWorld();
        Action<GameStartupSettings, GameState> initializeServerWorld = (_, _) =>
            Engine.GetOrCreateWorld(targetWorld);
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Engine.ShutDown();
        };

        Engine.BeforeCreateWindows += initializeServerWorld;
        Console.CancelKeyPress += cancelHandler;
        GameStartupSettings startupSettings = GetEngineSettings();
        RuntimeStartupPolicy.ValidateProfile(
            RuntimeApplicationProfile.HeadlessServer,
            RuntimeStartupPolicy.Normalize(startupSettings));
        try
        {
            Engine.Run(startupSettings, Engine.LoadOrGenerateGameState());
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            Engine.BeforeCreateWindows -= initializeServerWorld;
        }
    }

    private static void ConfigureFbxTraceLogging(UnitTestingWorldSettings settings)
    {
        FbxTrace.LogSink = static message => Debug.Meshes(message);
        FbxTrace.ProfilerScopeFactory = static scopeName => Engine.Profiler.Start(scopeName);

        if (settings.FbxLogVerbosity == UnitTestFbxLogVerbosity.UseEnvironment)
            FbxTrace.RefreshFromEnvironment();
        else
        {
            FbxTrace.Verbosity = settings.FbxLogVerbosity switch
            {
                UnitTestFbxLogVerbosity.Off => FbxLogVerbosity.Off,
                UnitTestFbxLogVerbosity.Errors => FbxLogVerbosity.Errors,
                UnitTestFbxLogVerbosity.Warnings => FbxLogVerbosity.Warnings,
                UnitTestFbxLogVerbosity.Info => FbxLogVerbosity.Info,
                UnitTestFbxLogVerbosity.Verbose => FbxLogVerbosity.Verbose,
                _ => FbxLogVerbosity.Off,
            };
        }

        Debug.Meshes($"FBX trace logging configured: setting={settings.FbxLogVerbosity}, effective={FbxTrace.Verbosity}, category={ELogCategory.Meshes}.");
    }

    private static ServerJoinAdmissionResult? ResolveServerJoin(PlayerJoinRequest request)
    {
        AdmissionFailureReason sessionFailure = RealtimeAdmissionValidator.ValidateSession(
            request,
            ServerSessionId,
            RequiredSessionToken,
            out string sessionFailureMessage);
        if (sessionFailure != AdmissionFailureReason.None)
            return new ServerJoinAdmissionResult(null, sessionFailure, sessionFailureMessage);

        ServerSessionContext? session = ResolveServerSession(request);
        return session is null
            ? new ServerJoinAdmissionResult(null, AdmissionFailureReason.SessionNotFound, "No local world instance is ready for realtime joins.")
            : new ServerJoinAdmissionResult(session);
    }

    private static ServerSessionContext? ResolveServerSession(PlayerJoinRequest request)
    {
        RuntimeWorld? worldInstance = Engine.WorldInstances.FirstOrDefault();
        if (worldInstance?.TargetWorld is null)
            return null;

        WorldAssetIdentity worldAsset = WorldAssetIdentityProvider.Create(worldInstance.TargetWorld, CurrentProtocolVersion);
        return new ServerSessionContext(ServerSessionId, worldInstance, worldAsset);
    }

    private static Guid ResolveConfiguredSessionId()
    {
        string? configured = GetOptionalEnvironmentValue(XREngineEnvironmentVariables.SessionId);
        return Guid.TryParse(configured, out Guid sessionId) ? sessionId : Guid.NewGuid();
    }

    private static string? GetOptionalEnvironmentValue(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int? GetOptionalIntEnvironmentValue(string name)
    {
        string? value = GetOptionalEnvironmentValue(name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed is > 0 and <= 65535
            ? parsed
            : null;
    }

    private static string CurrentProtocolVersion { get; } = typeof(Engine).Assembly.GetName().Version?.ToString() ?? "dev";

    private static GameStartupSettings GetEngineSettings()
    {
        UnitTestingWorldSettings unitTestSettings = RuntimeBootstrapState.Settings;
        var settings = new GameStartupSettings
        {
            StartupWindows = [],
            RunWithoutWindows = true,
            OutputVerbosityOverride = new XREngine.Data.Core.OverrideableSetting<EOutputVerbosity>(EOutputVerbosity.Verbose, true),
            UdpClientRecievePort = 5001,
            UdpServerBindPort = UdpBindPort,
            UdpServerSendPort = UdpAdvertisedPort,
            UdpMulticastGroupIP = UdpMulticastGroup,
            UdpMulticastPort = UdpMulticastPort,
            MultiplayerSessionId = ServerSessionId,
            NetworkingType = ENetworkingType.Server,
            DefaultUserSettings = new UserSettings
            {
                VSync = EVSyncMode.Off,
            },
        };

        UnitTestingWorldSettingsStore.ApplyStartupOverrides(settings, unitTestSettings);
        return settings;
    }
}
