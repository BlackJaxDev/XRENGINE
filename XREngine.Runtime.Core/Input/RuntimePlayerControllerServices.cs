using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace XREngine.Input
{
    /// <summary>
    /// Runtime service contract for the player-controller registry.
    /// <para>
    /// Mirrors <c>Engine.State</c>'s local/remote player management so that
    /// lower-layer code (pawns, game modes, networking) can query and mutate
    /// the player roster without depending on the concrete controller assembly.
    /// </para>
    /// </summary>
    public interface IRuntimePlayerControllerServices
    {
        /// <summary>
        /// Fired when a local player slot is filled.
        /// </summary>
        event Action<IPawnController>? LocalPlayerAdded;

        /// <summary>
        /// Fired when a local player slot is vacated.
        /// </summary>
        event Action<IPawnController>? LocalPlayerRemoved;

        /// <summary>
        /// Returns the local player controller for <paramref name="index"/>, or null if unoccupied.
        /// </summary>
        IPawnController? GetLocalPlayer(ELocalPlayerIndex index);

        /// <summary>
        /// Returns the local player controller for <paramref name="index"/>,
        /// creating one if the slot is empty.
        /// </summary>
        /// <param name="index">Player slot (One through Four).</param>
        /// <param name="controllerTypeOverride">
        /// Optional concrete controller type. Must implement <see cref="IPawnController"/>.
        /// When null, the active game mode's preferred type (or the engine default) is used.
        /// </param>
        IPawnController GetOrCreateLocalPlayer(
            ELocalPlayerIndex index,
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
            Type? controllerTypeOverride = null);

        /// <summary>
        /// Removes the local player at <paramref name="index"/> and destroys its controller.
        /// </summary>
        bool RemoveLocalPlayer(ELocalPlayerIndex index);

        /// <summary>
        /// Shorthand for <c>GetOrCreateLocalPlayer(ELocalPlayerIndex.One)</c>.
        /// </summary>
        IPawnController MainPlayer { get; }

        /// <summary>
        /// Number of currently occupied local player slots.
        /// </summary>
        int LocalPlayerCount { get; }

        /// <summary>
        /// All local player slots (fixed-size, may contain nulls for empty slots).
        /// </summary>
        IReadOnlyList<IPawnController?> AllLocalPlayers { get; }

        /// <summary>
        /// Creates a remote player controller for the given server index.
        /// </summary>
        IPawnController CreateRemotePlayer(int serverPlayerIndex);

        /// <summary>
        /// All currently tracked remote player controllers.
        /// </summary>
        IReadOnlyList<IPawnController> RemotePlayers { get; }

        /// <summary>
        /// Adds a remote player to the tracked list.
        /// </summary>
        void AddRemotePlayer(IPawnController player);

        /// <summary>
        /// Removes a remote player from the tracked list.
        /// </summary>
        bool RemoveRemotePlayer(IPawnController player);
    }

    /// <summary>
    /// Static accessor for the player-controller registry seam.
    /// Wired during engine construction, consumed by lower-layer code.
    /// </summary>
    public static class RuntimePlayerControllerServices
    {
        private const string DefaultLocalControllerTypeName = "XREngine.Runtime.InputIntegration.LocalPlayerController, XREngine.Runtime.InputIntegration";
        private const string DefaultRemoteControllerTypeName = "XREngine.Runtime.InputIntegration.RemotePlayerController, XREngine.Runtime.InputIntegration";

        private static readonly object FactorySync = new();
        private static readonly Dictionary<Type, Func<ELocalPlayerIndex, IPawnController>> LocalControllerFactories = [];
        private static readonly Dictionary<Type, Func<int, IPawnController>> RemoteControllerFactories = [];
        private static Type? _defaultLocalControllerType;
        private static Type? _defaultRemoteControllerType;

        public static IRuntimePlayerControllerServices? Current { get; set; }

        /// <summary>
        /// Default concrete type for local player controllers.
        /// Resolved lazily from the integration assembly when first requested.
        /// Used by the factory when no type override is provided and the game mode has no preference.
        /// </summary>
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        public static Type? DefaultLocalControllerType
        {
            get => _defaultLocalControllerType ??= ResolveControllerType(DefaultLocalControllerTypeName);
            set => _defaultLocalControllerType = value;
        }

        /// <summary>
        /// Default concrete type for remote player controllers.
        /// Resolved lazily from the integration assembly when first requested.
        /// </summary>
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        public static Type? DefaultRemoteControllerType
        {
            get => _defaultRemoteControllerType ??= ResolveControllerType(DefaultRemoteControllerTypeName);
            set => _defaultRemoteControllerType = value;
        }

        [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        private static Type? ResolveControllerType(string assemblyQualifiedTypeName)
        {
            Type? type = Type.GetType(assemblyQualifiedTypeName, throwOnError: false);
            return type is not null && typeof(IPawnController).IsAssignableFrom(type)
                ? type
                : null;
        }

        public static void RegisterLocalControllerFactory<TController>(Func<ELocalPlayerIndex, TController> factory, bool makeDefault = false)
            where TController : IPawnController
        {
            ArgumentNullException.ThrowIfNull(factory);
            RegisterLocalControllerFactory(typeof(TController), index => factory(index), makeDefault);
        }

        public static void RegisterLocalControllerFactory(Type controllerType, Func<ELocalPlayerIndex, IPawnController> factory, bool makeDefault = false)
        {
            ArgumentNullException.ThrowIfNull(controllerType);
            ArgumentNullException.ThrowIfNull(factory);

            if (!typeof(IPawnController).IsAssignableFrom(controllerType))
                throw new ArgumentException($"Type must implement {nameof(IPawnController)}.", nameof(controllerType));

            lock (FactorySync)
            {
                LocalControllerFactories[controllerType] = factory;
                if (makeDefault || _defaultLocalControllerType is null)
                    _defaultLocalControllerType = controllerType;
            }
        }

        public static void RegisterRemoteControllerFactory<TController>(Func<int, TController> factory, bool makeDefault = false)
            where TController : IPawnController
        {
            ArgumentNullException.ThrowIfNull(factory);
            RegisterRemoteControllerFactory(typeof(TController), serverPlayerIndex => factory(serverPlayerIndex), makeDefault);
        }

        public static void RegisterRemoteControllerFactory(Type controllerType, Func<int, IPawnController> factory, bool makeDefault = false)
        {
            ArgumentNullException.ThrowIfNull(controllerType);
            ArgumentNullException.ThrowIfNull(factory);

            if (!typeof(IPawnController).IsAssignableFrom(controllerType))
                throw new ArgumentException($"Type must implement {nameof(IPawnController)}.", nameof(controllerType));

            lock (FactorySync)
            {
                RemoteControllerFactories[controllerType] = factory;
                if (makeDefault || _defaultRemoteControllerType is null)
                    _defaultRemoteControllerType = controllerType;
            }
        }

        public static bool TryCreateLocalController(Type controllerType, ELocalPlayerIndex index, out IPawnController? controller)
        {
            ArgumentNullException.ThrowIfNull(controllerType);

            Func<ELocalPlayerIndex, IPawnController>? factory;
            lock (FactorySync)
                LocalControllerFactories.TryGetValue(controllerType, out factory);

            controller = factory?.Invoke(index);
            return controller is not null;
        }

        public static bool TryCreateRemoteController(Type controllerType, int serverPlayerIndex, out IPawnController? controller)
        {
            ArgumentNullException.ThrowIfNull(controllerType);

            Func<int, IPawnController>? factory;
            lock (FactorySync)
                RemoteControllerFactories.TryGetValue(controllerType, out factory);

            controller = factory?.Invoke(serverPlayerIndex);
            return controller is not null;
        }
    }
}
