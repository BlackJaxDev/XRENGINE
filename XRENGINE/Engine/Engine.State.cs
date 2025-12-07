using Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Core.Files;
using XREngine.Input;
using XREngine.Native;

namespace XREngine
{
    public static partial class Engine
    {
        /// <summary>
        /// Whether the engine is running in editor mode (as opposed to standalone game).
        /// This is set at startup and does not change during runtime.
        /// </summary>
        public static bool IsEditor { get; internal set; } = true;
        
        /// <summary>
        /// Whether the game is currently playing (simulation running).
        /// Delegates to PlayMode.IsPlaying for consistency.
        /// </summary>
        public static bool IsPlaying => PlayMode.IsPlaying;
        
        public static JobManager Jobs { get; } = new JobManager();

        public static GameState LoadOrGenerateGameState(
            Func<GameState>? generateFactory = null,
            string assetName = "state.asset",
            bool allowLoading = true)
            => LoadOrGenerateAsset(() => generateFactory?.Invoke() ?? new GameState(), assetName, allowLoading);

        public static GameStartupSettings LoadOrGenerateGameSettings(
            Func<GameStartupSettings>? generateFactory = null,
            string assetName = "startup.asset",
            bool allowLoading = true)
            => LoadOrGenerateAsset(() => generateFactory?.Invoke() ?? GenerateGameSettings(), assetName, allowLoading);

        public static T LoadOrGenerateGameState<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
            Func<T>? generateFactory = null,
            string assetName = "state.asset",
            bool allowLoading = true) where T : GameState, new()
            => LoadOrGenerateAsset(() => generateFactory?.Invoke() ?? new T(), assetName, allowLoading);

        public static T LoadOrGenerateGameSettings<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
            Func<T>? generateFactory = null,
            string assetName = "startup.asset",
            bool allowLoading = true) where T : GameStartupSettings, new()
            => LoadOrGenerateAsset(() => generateFactory?.Invoke() ?? new T(), assetName, allowLoading);

        public static T LoadOrGenerateAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
            Func<T>? generateFactory,
            string assetName,
            bool allowLoading,
            params string[] folderNames) where T : XRAsset, new()
        {
            T? asset = null;
            if (allowLoading)
            {
                asset = Assets.LoadGameAsset<T>([.. folderNames, assetName]);
                if (asset != null)
                    return asset;
            }
            asset = generateFactory?.Invoke() ?? Activator.CreateInstance<T>();
            asset.Name = assetName;
            /*Task.Run(() => */Assets.SaveGameAssetTo(asset, folderNames)/*)*/;
            return asset;
        }

        private static GameStartupSettings GenerateGameSettings()
        {
            int w = 1920;
            int h = 1080;
            float updateHz = 90.0f;
            float renderHz = 90.0f;
            float fixedHz = 45.0f;

            int primaryX = NativeMethods.GetSystemMetrics(0);
            int primaryY = NativeMethods.GetSystemMetrics(1);

            return new GameStartupSettings()
            {
                StartupWindows =
                [
                    new()
                    {
                        WindowTitle = "FREAK ENGINE",
                        TargetWorld = new Scene.XRWorld(),
                        WindowState = EWindowState.Windowed,
                        X = primaryX / 2 - w / 2,
                        Y = primaryY / 2 - h / 2,
                        Width = w,
                        Height = h,
                    }
                ],
                OutputVerbosity = EOutputVerbosity.Verbose,
                DefaultUserSettings = new UserSettings()
                {
                    TargetFramesPerSecond = renderHz,
                    VSync = EVSyncMode.Off,
                },
                TargetUpdatesPerSecond = updateHz,
                FixedFramesPerSecond = fixedHz,
            };
        }

        public static class State
        {
            /// <summary>
            /// Called when a local player is first created.
            /// </summary>
            public static event Action<LocalPlayerController>? LocalPlayerAdded;
            /// <summary>
            /// Called when a local player is removed.
            /// </summary>
            public static event Action<LocalPlayerController>? LocalPlayerRemoved;

            //Only up to 4 local players, because we only support up to 4 players split screen, realistically. If that.
            public static LocalPlayerController?[] LocalPlayers { get; } = new LocalPlayerController[4];

            public static bool RemoveLocalPlayer(ELocalPlayerIndex index)
            {
                var player = LocalPlayers[(int)index];
                if (player is null)
                    return false;

                LocalPlayers[(int)index] = null;
                LocalPlayerRemoved?.Invoke(player);
                player.Destroy();
                return true;
            }

            /// <summary>
            /// Retrieves or creates a local player controller for the given index.
            /// </summary>
            /// <param name="index">Player slot to fetch.</param>
            /// <param name="controllerTypeOverride">Optional controller type to force for this request.</param>
            /// <returns>The resolved local player controller.</returns>
            public static LocalPlayerController GetOrCreateLocalPlayer(ELocalPlayerIndex index, Type? controllerTypeOverride = null)
            {
                var existing = LocalPlayers[(int)index];
                var desiredType = ResolveLocalPlayerControllerType(controllerTypeOverride);

                if (existing is not null)
                {
                    if (desiredType.IsInstanceOfType(existing))
                        return existing;

                    // Preserve viewport bindings when swapping controller types so input devices stay wired up.
                    var viewportsToReassign = _windows
                        .SelectMany(w => w.Viewports)
                        .Where(vp => vp.AssociatedPlayer == existing)
                        .ToArray();

                    RemoveLocalPlayer(index);

                    var replacement = AddLocalPlayer(index, desiredType);

                    foreach (var viewport in viewportsToReassign)
                        viewport.AssociatedPlayer = replacement;

                    return replacement;
                }

                return AddLocalPlayer(index, desiredType);
            }

            /// <summary>
            /// This property returns the main player, which is the first player and should always exist.
            /// </summary>
            public static LocalPlayerController MainPlayer => GetOrCreateLocalPlayer(ELocalPlayerIndex.One);

            private static LocalPlayerController AddLocalPlayer(ELocalPlayerIndex index, Type controllerType)
            {
                var player = InstantiateLocalPlayerController(controllerType, index);
                LocalPlayers[(int)index] = player;
                LocalPlayerAdded?.Invoke(player);
                return player;
            }

            private static Type ResolveLocalPlayerControllerType(Type? controllerTypeOverride)
            {
                if (controllerTypeOverride is not null)
                    return controllerTypeOverride;

                if (Engine.PlayMode.ActiveGameMode?.DefaultPlayerControllerClass is Type gameModePreferred)
                    return gameModePreferred;

                return typeof(LocalPlayerController);
            }

            private static LocalPlayerController InstantiateLocalPlayerController(Type controllerType, ELocalPlayerIndex index)
            {
                if (!typeof(LocalPlayerController).IsAssignableFrom(controllerType))
                    throw new ArgumentException($"Controller type {controllerType.FullName} must inherit from LocalPlayerController", nameof(controllerType));

                LocalPlayerController? player;
                var ctorWithIndex = controllerType.GetConstructor(new[] { typeof(ELocalPlayerIndex) });
                if (ctorWithIndex is not null)
                    player = ctorWithIndex.Invoke(new object[] { index }) as LocalPlayerController;
                else
                    player = Activator.CreateInstance(controllerType) as LocalPlayerController;

                if (player is null)
                    throw new InvalidOperationException($"Failed to instantiate controller of type {controllerType.FullName}");

                player.LocalPlayerIndex = index;
                return player;
            }

            /// <summary>
            /// Gets the local player controller for the given index, if it exists.
            /// </summary>
            /// <param name="index"></param>
            /// <returns></returns>
            public static LocalPlayerController? GetLocalPlayer(ELocalPlayerIndex index)
                => LocalPlayers.TryGet((int)index);

            /// <summary>
            /// All remote players that are connected to this server, this p2p client, or the server this client is connected to.
            /// </summary>
            public static List<RemotePlayerController> RemotePlayers { get; } = [];
        }
    }
}
