using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Scene;

namespace XREngine
{
    /// <summary>
    /// Determines how world state is handled when exiting play mode.
    /// </summary>
    public enum EStateRestorationMode
    {
        /// <summary>
        /// Serialize the world state before play, restore it after.
        /// Most accurate but slower for large worlds.
        /// </summary>
        SerializeAndRestore,

        /// <summary>
        /// Reload the world from its saved asset file.
        /// Fast but loses any unsaved editor changes.
        /// </summary>
        ReloadFromAsset,

        /// <summary>
        /// Don't restore state at all. Changes made during play persist.
        /// Useful for level editing while playing.
        /// </summary>
        PersistChanges,
    }

    /// <summary>
    /// Configuration for how play mode behaves when entering and exiting.
    /// </summary>
    [Serializable]
    public class PlayModeConfiguration : XRAsset
    {
        private XRWorld? _startupWorld;
        private XRScene? _startupScene;
        private GameMode? _gameModeOverride;
        private bool _reloadGameplayAssemblies = true;
        private EStateRestorationMode _stateRestorationMode = EStateRestorationMode.SerializeAndRestore;
        private bool _simulatePhysics = true;
        private ELocalPlayerIndex _defaultPlayerIndex = ELocalPlayerIndex.One;
        private bool _autoSpawnPlayer = true;
        private bool _pauseOnLostFocus = false;

        /// <summary>
        /// Which world to load when entering play mode.
        /// If null, uses the currently viewed world.
        /// </summary>
        public XRWorld? StartupWorld
        {
            get => _startupWorld;
            set => SetField(ref _startupWorld, value);
        }

        /// <summary>
        /// Which scene within the startup world to begin in.
        /// If null, loads all scenes in the world.
        /// </summary>
        public XRScene? StartupScene
        {
            get => _startupScene;
            set => SetField(ref _startupScene, value);
        }

        /// <summary>
        /// The GameMode to use. Priority order:
        /// 1. This override (if set)
        /// 2. StartupWorld.DefaultGameMode
        /// 3. StartupWorld.Settings.DefaultGameMode
        /// 4. Global default GameMode
        /// </summary>
        public GameMode? GameModeOverride
        {
            get => _gameModeOverride;
            set => SetField(ref _gameModeOverride, value);
        }

        /// <summary>
        /// Whether to reload gameplay assemblies when entering play mode.
        /// This provides isolation but takes longer to enter play.
        /// </summary>
        public bool ReloadGameplayAssemblies
        {
            get => _reloadGameplayAssemblies;
            set => SetField(ref _reloadGameplayAssemblies, value);
        }

        /// <summary>
        /// Whether to serialize and restore the entire world state,
        /// or just reset to the saved asset state.
        /// </summary>
        public EStateRestorationMode StateRestorationMode
        {
            get => _stateRestorationMode;
            set => SetField(ref _stateRestorationMode, value);
        }

        /// <summary>
        /// Whether physics should simulate during play mode.
        /// </summary>
        public bool SimulatePhysics
        {
            get => _simulatePhysics;
            set => SetField(ref _simulatePhysics, value);
        }

        /// <summary>
        /// Which player index to spawn as when entering play.
        /// </summary>
        public ELocalPlayerIndex DefaultPlayerIndex
        {
            get => _defaultPlayerIndex;
            set => SetField(ref _defaultPlayerIndex, value);
        }

        /// <summary>
        /// Whether to automatically spawn a player pawn when entering play mode.
        /// </summary>
        public bool AutoSpawnPlayer
        {
            get => _autoSpawnPlayer;
            set => SetField(ref _autoSpawnPlayer, value);
        }

        /// <summary>
        /// Whether to pause the game when the window loses focus.
        /// </summary>
        public bool PauseOnLostFocus
        {
            get => _pauseOnLostFocus;
            set => SetField(ref _pauseOnLostFocus, value);
        }
    }
}
