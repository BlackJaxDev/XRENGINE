using System.Globalization;
using XREngine.Fbx;
using XREngine.ControlPlane;
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
    private static ManagedServerWorker? ManagedWorker;
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

    private static int Main(string[] args)
    {
        try
        {
            return MainCore(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Server configuration error: {ex.Message}");
            return 64;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"Server configuration error: {ex.Message}");
            return 64;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Server initialization failed: {ex.Message}");
            return 70;
        }
    }

    private static int MainCore(string[] args)
    {
        using IDisposable modelAssetPipelineRegistration =
            ModelAssetPipelineRegistration.Install(Engine.Assets, typeof(XRPrefabSource));
        using IDisposable applicationServices =
            RuntimeApplicationBootstrap.Install(RuntimeApplicationProfile.HeadlessServer);
        Engine.ConfigureMemoryPolicy(EngineMemoryProfile.HeadlessServer);

        if (TryCreateWorldPackage(args))
            return 0;

        ManagedWorker = ManagedServerWorker.TryLoadFromEnvironment();
        if (ManagedWorker is null && (args.Length != 1 || !string.Equals(args[0], "--development", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Dedicated server startup requires XRE_MANAGED_WORKER_CONFIG_FILE or the explicit --development profile.");
        if (ManagedWorker is not null)
        {
            Engine.ServerMaximumPlayers = ManagedWorker.MaxPlayers;
            Engine.ServerBindAddress = ManagedWorker.BindAddress;
            Engine.ServerRequiresManagedUdpTransport = true;
        }

        Engine.ServerSessionResolver = ResolveServerSession;
        Engine.ServerJoinAdmissionResolver = ResolveServerJoin;
        Engine.ManagedAdmissionVerifierResolver = request => ManagedWorker?.TryGetManagedAdmissionVerifier(request, out var verifier) == true ? verifier : null;
        Engine.ManagedAdmissionCommit = (request, verifier, admit) => ManagedWorker?.CommitManagedAdmission(request, verifier, admit) == true;
        Engine.ServerPlayerConnected = player => ManagedWorker?.RecordConnected(player);
        Engine.ServerPlayerDisconnected = player => ManagedWorker?.RecordDisconnected(player);

        UnitTestingWorldSettings? unitTestSettings = ManagedWorker is null ? UnitTestingWorldSettingsStore.Load(false) : null;
        if (unitTestSettings is not null)
            UnitTestingWorldSettingsStore.ApplyWorldKindOverride(unitTestSettings);
        ConfigureFbxTraceLogging(unitTestSettings);
        XRWorld targetWorld = ManagedWorker?.LoadVerifiedWorld() ?? BootstrapWorldFactory.CreateServerDefaultWorld();
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
            ManagedWorker?.StartPolling();
            Engine.Run(startupSettings, Engine.LoadOrGenerateGameState());
            if (Environment.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "Engine initialization did not complete. Review the preceding startup diagnostic and startup-failure.log for the root cause.");
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            Engine.BeforeCreateWindows -= initializeServerWorld;
            ManagedWorker?.Dispose();
            ManagedWorker = null;
            Engine.ServerMaximumPlayers = null;
            Engine.ServerBindAddress = null;
            Engine.ServerRequiresManagedUdpTransport = false;
            Engine.ServerPlayerConnected = null;
            Engine.ServerPlayerDisconnected = null;
        }

        return 0;
    }

    private static void ConfigureFbxTraceLogging(UnitTestingWorldSettings? settings)
    {
        FbxTrace.LogSink = static message => Debug.Meshes(message);
        FbxTrace.ProfilerScopeFactory = static scopeName => Engine.Profiler.Start(scopeName);

        if (settings is null || settings.FbxLogVerbosity == UnitTestFbxLogVerbosity.UseEnvironment)
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

        Debug.Meshes($"FBX trace logging configured: setting={settings?.FbxLogVerbosity.ToString() ?? "managed-default"}, effective={FbxTrace.Verbosity}, category={ELogCategory.Meshes}.");
    }

    private static ServerJoinAdmissionResult? ResolveServerJoin(PlayerJoinRequest request)
    {
        if (ManagedWorker is not null)
            return ManagedWorker.ResolveJoin(request, ResolveServerSession(request));

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

        WorldAssetIdentity worldAsset = ManagedWorker?.WorldAsset ?? WorldAssetIdentityProvider.Create(worldInstance.TargetWorld, CurrentProtocolVersion);
        return new ServerSessionContext(ManagedWorker?.SessionId ?? ServerSessionId, worldInstance, worldAsset);
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
        var settings = new GameStartupSettings
        {
            StartupWindows = [],
            RunWithoutWindows = true,
            OutputVerbosityOverride = new XREngine.Data.Core.OverrideableSetting<EOutputVerbosity>(EOutputVerbosity.Verbose, true),
            UdpClientRecievePort = 5001,
            UdpServerBindPort = ManagedWorker?.BindPort ?? UdpBindPort,
            UdpServerSendPort = ManagedWorker?.BindPort ?? UdpAdvertisedPort,
            UdpMulticastGroupIP = UdpMulticastGroup,
            UdpMulticastPort = UdpMulticastPort,
            MultiplayerSessionId = ManagedWorker?.SessionId ?? ServerSessionId,
            NetworkingType = ENetworkingType.Server,
            DefaultUserSettings = new UserSettings
            {
                VSync = EVSyncMode.Off,
            },
        };

        if (ManagedWorker is null)
            UnitTestingWorldSettingsStore.ApplyStartupOverrides(settings, RuntimeBootstrapState.Settings);
        if (ManagedWorker is not null)
        {
            settings.TargetUpdatesPerSecond = ManagedWorker.TickRate;
            settings.FixedFramesPerSecond = 1000.0f / ManagedWorker.FixedDeltaMilliseconds;
        }
        return settings;
    }

    private static bool TryCreateWorldPackage(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], "--create-world-package", StringComparison.OrdinalIgnoreCase))
            return false;
        if (args.Length != 4 || !string.Equals(args[2], "--world-name", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(args[1]) || string.IsNullOrWhiteSpace(args[3]))
            throw new ArgumentException("Usage: --create-world-package <directory> --world-name <name>");

        string directory = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(directory);
        XRWorld world = CreateManagedPackageWorld(args[3].Trim());
        world.Name = args[3].Trim();
        world.FilePath = Path.Combine(directory, "World.asset");
        Engine.Assets.SaveImmediate(world);

        WorldAssetIdentity asset = WorldAssetIdentityProvider.Create(world, CurrentProtocolVersion);
        WorldPackageManifest manifest = WorldPackageManifestBuilder.CreateFromDirectory(
            directory,
            asset,
            packageId: asset.WorldId,
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["worldEntryPoint"] = "World.asset",
                ["gameBootstrapId"] = "world-v1",
            },
            worldEntryPoint: "World.asset",
            gameBootstrapId: "world-v1",
            buildVersion: CurrentProtocolVersion);
        File.WriteAllText(Path.Combine(directory, "world-package.json"), System.Text.Json.JsonSerializer.Serialize(manifest, XreControlPlaneJsonContext.Default.WorldPackageManifest));
        Console.WriteLine($"Created verified world package '{asset.WorldId}' at '{directory}'.");
        return true;
    }

    /// <summary>Creates the small authored managed-package baseline without discovery side channels.</summary>
    private static XRWorld CreateManagedPackageWorld(string worldName)
    {
        var scene = new XRScene("Managed Server Scene");
        scene.RootNodes.Add(new SceneNode("Managed Server Root"));
        return new XRWorld(worldName, new CustomGameMode { DefaultPlayerPawnClass = null }, scene);
    }
}
