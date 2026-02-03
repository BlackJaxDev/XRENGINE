using MemoryPack;
using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine
{
    /// <summary>
    /// GameMode defines the rules and behavior of a game session.
    /// Override this class to implement custom game logic, spawning, and player management.
    /// </summary>
    public abstract class GameMode : XRAsset
    {
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        protected Type? _defaultPlayerControllerClass = typeof(LocalPlayerController);
        protected Type? _defaultPlayerPawnClass = typeof(FlyingCameraPawnComponent);

        [property: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        public Type? PlayerControllerClass => _defaultPlayerControllerClass;
        public Type? PlayerPawnClass => _defaultPlayerPawnClass;

        /// <summary>
        /// The world instance this GameMode is managing.
        /// Set when entering play mode.
        /// </summary>
        [YamlIgnore]
        [MemoryPackIgnore]
        [Description("The world instance this GameMode is managing.")]
        public XRWorldInstance? WorldInstance { get; internal set; }

        /// <summary>
        /// Queue of pending possessions per pawn.
        /// </summary>
        [YamlIgnore]
        [MemoryPackIgnore]
        [Description("Queue of pending possessions per pawn.")]
        public Dictionary<PawnComponent, Queue<ELocalPlayerIndex>> PossessionQueue { get; } = [];

        /// <summary>
        /// Tracks pawns that were auto-spawned by this GameMode.
        /// These pawns will be destroyed when play mode ends.
        /// </summary>
        private HashSet<PawnComponent> AutoSpawnedPawns { get; } = [];

        protected void TrackAutoSpawnedPawn(PawnComponent pawn)
        {
            if (pawn is null)
                return;

            AutoSpawnedPawns.Add(pawn);
        }

        /// <summary>
        /// Whether this GameMode is currently active (in play mode).
        /// </summary>
        public bool IsActive { get; private set; }

        #region Lifecycle Methods

        /// <summary>
        /// Called when play mode begins for this GameMode's world.
        /// Override to spawn initial pawns, set up game rules, initialize state, etc.
        /// </summary>
        public virtual void OnBeginPlay()
        {
            IsActive = true;

            // Default behavior: spawn a pawn for the default player if auto-spawn is enabled
            if (Engine.PlayMode.Configuration.AutoSpawnPlayer)
                SpawnDefaultPlayerPawn(Engine.PlayMode.Configuration.DefaultPlayerIndex);
            
            Debug.Out($"GameMode.OnBeginPlay - World: {WorldInstance?.TargetWorld?.Name ?? "null"}");
        }

        /// <summary>
        /// Called when play mode ends.
        /// Override to clean up game state, save progress, etc.
        /// </summary>
        public virtual void OnEndPlay()
        {
            // Clear all possession queues
            foreach (var kvp in PossessionQueue)
                kvp.Key.PreUnpossessed -= OnPawnUnPossessing;
            
            PossessionQueue.Clear();

            // Unpossess all pawns
            foreach (var player in Engine.State.LocalPlayers)
                if (player?.ControlledPawn is not null)
                    player.ControlledPawn.Controller = null;

            // Destroy all auto-spawned pawns
            foreach (var pawn in AutoSpawnedPawns)
            {
                if (pawn is not null && !pawn.IsDestroyed && pawn.SceneNode is not null)
                {
                    Debug.Out($"Destroying auto-spawned pawn: {pawn.Name}");
                    pawn.SceneNode.Destroy();
                }
            }
            AutoSpawnedPawns.Clear();

            IsActive = false;

            Debug.Out($"GameMode.OnEndPlay - World: {WorldInstance?.TargetWorld?.Name ?? "null"}");
        }

        /// <summary>
        /// Called each frame during play mode.
        /// Override to implement per-frame game logic.
        /// </summary>
        /// <param name="deltaTime">Time since last frame in seconds.</param>
        public virtual void Tick(float deltaTime)
        {
            // Override in derived classes for custom tick behavior
        }

        /// <summary>
        /// Called each fixed update during play mode (physics rate).
        /// Override to implement physics-rate game logic.
        /// </summary>
        /// <param name="fixedDeltaTime">Fixed time step in seconds.</param>
        public virtual void FixedTick(float fixedDeltaTime)
        {
            // Override in derived classes for custom fixed tick behavior
        }

        #endregion

        #region Pawn Spawning

        /// <summary>
        /// Factory method for creating the default pawn for a player.
        /// Override to customize pawn creation for your game.
        /// </summary>
        /// <param name="playerIndex">The player index to create a pawn for.</param>
        /// <returns>The created pawn component, or null if no pawn should be spawned.</returns>
        public virtual PawnComponent? CreateDefaultPawn(ELocalPlayerIndex playerIndex)
        {
            if (_defaultPlayerPawnClass is null)
                return null;

            if (WorldInstance is null)
            {
                Debug.LogWarning("Cannot spawn default pawn without an active world instance.");
                return null;
            }

            var pawnNodeName = $"Player{(int)playerIndex + 1}_Pawn";
            var pawnNode = new SceneNode(WorldInstance, pawnNodeName);

            if (pawnNode.AddComponent(_defaultPlayerPawnClass) is not PawnComponent pawnComponent)
            {
                var pawnClassName = _defaultPlayerPawnClass.FullName ?? _defaultPlayerPawnClass.Name ?? _defaultPlayerPawnClass.ToString();
                Debug.LogWarning($"Failed to create pawn of type {pawnClassName} for player {playerIndex}.");
                pawnNode.Destroy();
                return null;
            }

            WorldInstance.RootNodes.Add(pawnNode);

            // If the world is already playing, manually run begin-play/activation so late-spawned pawns are fully initialized.
            if (WorldInstance.PlayState == XRWorldInstance.EPlayState.Playing)
            {
                pawnNode.OnBeginPlay();
                if (pawnNode.IsActiveSelf)
                    pawnNode.OnActivated();
            }
            return pawnComponent;
        }

        /// <summary>
        /// Gets the spawn location for a player.
        /// Override to customize spawn point selection.
        /// </summary>
        /// <param name="playerIndex">The player index to get a spawn point for.</param>
        /// <returns>Spawn position and rotation.</returns>
        public virtual (System.Numerics.Vector3 Position, System.Numerics.Quaternion Rotation) GetSpawnPoint(ELocalPlayerIndex playerIndex)
        {
            // Default: spawn at origin
            return (System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity);
        }

        /// <summary>
        /// Spawns and possesses the default pawn for a player.
        /// The spawned pawn is tracked and will be destroyed when play mode ends.
        /// </summary>
        /// <param name="playerIndex">The player index to spawn for.</param>
        /// <returns>The spawned pawn, or null if spawning failed.</returns>
        protected virtual PawnComponent? SpawnDefaultPlayerPawn(ELocalPlayerIndex playerIndex)
        {
            EnsureLocalPlayerController(playerIndex);

            var pawn = CreateDefaultPawn(playerIndex);
            if (pawn is not null)
            {
                // Track the auto-spawned pawn for cleanup when play ends
                TrackAutoSpawnedPawn(pawn);

                // Apply spawn transform
                var (position, rotation) = GetSpawnPoint(playerIndex);
                if (pawn.SceneNode?.GetTransformAs<Transform>(false) is Transform transform)
                {
                    transform.SetWorldTranslation(position);
                    transform.SetWorldRotation(rotation);
                }

                // Possess the pawn
                ForcePossession(pawn, playerIndex);
                
                Debug.Out($"Spawned default pawn for player {playerIndex}");
            }
            return pawn;
        }

        #endregion

        #region Possession

        /// <summary>
        /// Immediately possesses the given pawn with the provided player.
        /// </summary>
        /// <param name="pawnComponent">The pawn to possess.</param>
        /// <param name="possessor">The player index that will possess the pawn.</param>
        public void ForcePossession(PawnComponent pawnComponent, ELocalPlayerIndex possessor)
        {
            Debug.Out($"[GameMode] ForcePossession called: pawn={pawnComponent?.Name}, player={possessor}");
            var localPlayer = Engine.State.GetOrCreateLocalPlayer(possessor);
            if (localPlayer != null)
            {
                Debug.Out($"[GameMode] LocalPlayer found, viewport={localPlayer.Viewport?.GetHashCode()}, current pawn={localPlayer.ControlledPawn?.Name}");
                localPlayer.ControlledPawn = pawnComponent;
                Debug.Out($"[GameMode] After possession: ControlledPawn={localPlayer.ControlledPawn?.Name}, Controller={pawnComponent?.Controller?.GetType().Name}");
            }
            else
                Debug.LogWarning($"Failed to possess pawn: could not resolve local player for index {possessor}");
        }

        /// <summary>
        /// Queues the given pawn for possession by the provided player.
        /// The player won't possess the pawn until all other players in the queue have gained and released possession of the pawn first.
        /// </summary>
        /// <param name="pawnComponent">The pawn to queue for possession.</param>
        /// <param name="possessor">The player index to queue.</param>
        public void EnqueuePossession(PawnComponent pawnComponent, ELocalPlayerIndex possessor)
        {
            if (pawnComponent.Controller is null)
                ForcePossession(pawnComponent, possessor);
            else
            {
                if (!PossessionQueue.ContainsKey(pawnComponent))
                    PossessionQueue[pawnComponent] = new Queue<ELocalPlayerIndex>();

                PossessionQueue[pawnComponent].Enqueue(possessor);
                pawnComponent.PreUnpossessed += OnPawnUnPossessing;
            }
        }

        private void OnPawnUnPossessing(PawnComponent pawnComponent)
        {
            if (!PossessionQueue.TryGetValue(pawnComponent, out Queue<ELocalPlayerIndex>? value))
                return;
            
            var possessor = value.Dequeue();
            if (value.Count == 0)
            {
                PossessionQueue.Remove(pawnComponent);
                pawnComponent.PreUnpossessed -= OnPawnUnPossessing;
            }
            ForcePossession(pawnComponent, possessor);
        }

        /// <summary>
        /// Ensures a controller exists for the given player index using the configured controller type.
        /// </summary>
        /// <param name="playerIndex">The target player index.</param>
        /// <returns>The resolved controller, or null if creation failed.</returns>
        protected virtual LocalPlayerController? EnsureLocalPlayerController(ELocalPlayerIndex playerIndex)
            => Engine.State.GetOrCreateLocalPlayer(playerIndex, _defaultPlayerControllerClass);

        #endregion
    }
}