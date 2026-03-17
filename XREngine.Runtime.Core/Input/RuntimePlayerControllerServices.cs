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
        public static IRuntimePlayerControllerServices? Current { get; set; }

        /// <summary>
        /// Default concrete type for local player controllers.
        /// Set by the integration assembly (e.g. Runtime.InputIntegration) at startup.
        /// Used by the factory when no type override is provided and the game mode has no preference.
        /// </summary>
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        public static Type? DefaultLocalControllerType { get; set; }

        /// <summary>
        /// Default concrete type for remote player controllers.
        /// Set by the integration assembly at startup.
        /// </summary>
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        public static Type? DefaultRemoteControllerType { get; set; }
    }
}
