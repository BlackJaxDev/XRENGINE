# Editor/Play Mode Architecture

This document outlines the architecture for switching between editor mode and play mode in XRENGINE. The design addresses state management, world lifecycle, physics simulation control, assembly isolation for gameplay code, and proper state reset when transitioning between modes.

## Overview

The editor/play mode system provides:
1. **Global state tracking** for the current mode and transitions
2. **World state snapshot and restoration** for clean mode switching
3. **Assembly isolation** using `AssemblyLoadContext` for gameplay code hot-reload
4. **Physics simulation control** that only runs during play mode
5. **GameMode lifecycle management** with proper begin/end play hooks
6. **Startup world configuration** for determining what loads when play begins

## Mode States

```
┌─────────────────────────────────────────────────────────────────┐
│                         Mode State Machine                      │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│   ┌──────────┐    EnterPlay()    ┌───────────────┐              │
│   │   Edit   │ ───────────────►  │ EnteringPlay  │              │
│   └──────────┘                   └───────────────┘              │
│        ▲                                │                       │
│        │                                │ (transition complete) │
│        │                                ▼                       │
│   ┌──────────────┐   ExitPlay()   ┌──────────┐                  │
│   │ ExitingPlay  │ ◄───────────── │   Play   │                  │
│   └──────────────┘                └──────────┘                  │
│        │                                                        │
│        │ (transition complete)                                  │
│        ▼                                                        │
│   ┌──────────┐                                                  │
│   │   Edit   │                                                  │
│   └──────────┘                                                  │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

## Core Types

### PlayModeManager (Static Manager)

The central coordinator for play mode transitions. Lives in the engine core so both editor and standalone games can use it.

```csharp
// Engine.PlayMode partial class in XRENGINE/Engine/Engine.PlayMode.cs
public static partial class Engine
{
    public static class PlayMode
    {
        // Current state
        public static EPlayModeState State { get; private set; }
        public static bool IsPlaying => State == EPlayModeState.Play;
        public static bool IsEditing => State == EPlayModeState.Edit;
        public static bool IsTransitioning => State == EPlayModeState.EnteringPlay 
                                            || State == EPlayModeState.ExitingPlay;
        
        // Events for state changes
        public static event Action<EPlayModeState>? StateChanged;
        public static event Action? PreEnterPlay;
        public static event Action? PostEnterPlay;
        public static event Action? PreExitPlay;
        public static event Action? PostExitPlay;
        
        // Configuration
        public static PlayModeConfiguration Configuration { get; set; }
        
        // Methods
        public static Task EnterPlayModeAsync();
        public static Task ExitPlayModeAsync();
        public static void TogglePlayMode();
    }
}

public enum EPlayModeState
{
    Edit,           // Normal editor mode - no simulation
    EnteringPlay,   // Transitioning: saving state, loading assemblies
    Play,           // Full simulation running
    ExitingPlay,    // Transitioning: unloading assemblies, restoring state
}
```

### PlayModeConfiguration

Configuration for how play mode behaves:

```csharp
// XRENGINE/Engine/PlayModeConfiguration.cs
public class PlayModeConfiguration : XRAsset
{
    /// <summary>
    /// Which world to load when entering play mode.
    /// If null, uses the currently viewed world.
    /// </summary>
    public XRWorld? StartupWorld { get; set; }
    
    /// <summary>
    /// Which scene within the startup world to begin in.
    /// If null, loads all scenes in the world.
    /// </summary>
    public XRScene? StartupScene { get; set; }
    
    /// <summary>
    /// The GameMode to use. Priority order:
    /// 1. This override (if set)
    /// 2. StartupWorld.DefaultGameMode
    /// 3. StartupWorld.Settings.DefaultGameMode
    /// 4. Global default GameMode
    /// </summary>
    public GameMode? GameModeOverride { get; set; }
    
    /// <summary>
    /// Whether to reload gameplay assemblies when entering play mode.
    /// This provides isolation but takes longer to enter play.
    /// </summary>
    public bool ReloadGameplayAssemblies { get; set; } = true;
    
    /// <summary>
    /// Whether to serialize and restore the entire world state,
    /// or just reset to the saved asset state.
    /// </summary>
    public EStateRestorationMode StateRestorationMode { get; set; } 
        = EStateRestorationMode.SerializeAndRestore;
    
    /// <summary>
    /// Whether physics should simulate during play mode.
    /// </summary>
    public bool SimulatePhysics { get; set; } = true;
    
    /// <summary>
    /// Which player index to spawn as when entering play.
    /// </summary>
    public ELocalPlayerIndex DefaultPlayerIndex { get; set; } 
        = ELocalPlayerIndex.One;
}

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
```

### WorldStateSnapshot

Captures the state of a world for later restoration:

```csharp
// XRENGINE/Engine/WorldStateSnapshot.cs
public class WorldStateSnapshot
{
    /// <summary>
    /// The world this snapshot is for.
    /// </summary>
    public XRWorld SourceWorld { get; }
    
    /// <summary>
    /// Serialized scene data for each scene in the world.
    /// </summary>
    public Dictionary<XRScene, byte[]> SerializedScenes { get; }
    
    /// <summary>
    /// World settings at time of snapshot.
    /// </summary>
    public byte[] SerializedSettings { get; }
    
    /// <summary>
    /// Creates a snapshot of the given world.
    /// </summary>
    public static WorldStateSnapshot Capture(XRWorld world);
    
    /// <summary>
    /// Restores the world to the captured state.
    /// </summary>
    public void Restore();
}
```

## Play Mode Lifecycle

### Entering Play Mode

```
EnterPlayModeAsync()
├── 1. Validate state (must be in Edit mode)
├── 2. Set State = EnteringPlay
├── 3. Fire PreEnterPlay event
├── 4. Capture world state snapshot (if configured)
├── 5. Load/reload gameplay assemblies (if configured)
├── 6. Determine startup world:
│   ├── Configuration.StartupWorld (if set)
│   ├── Currently viewed world (fallback)
│   └── Create default world (last resort)
├── 7. Determine GameMode:
│   ├── Configuration.GameModeOverride (if set)
│   ├── StartupWorld.DefaultGameMode (if set)
│   ├── StartupWorld.Settings.DefaultGameMode (if set)
│   └── Create default GameMode (last resort)
├── 8. Initialize GameMode
├── 9. Call BeginPlay on all world instances
│   ├── Initialize VisualScene
│   ├── Initialize PhysicsScene
│   ├── Activate all scene nodes
│   └── Link timer callbacks (including physics step)
├── 10. Enable physics simulation
├── 11. Spawn player pawn (via GameMode)
├── 12. Set State = Play
└── 13. Fire PostEnterPlay event
```

### Exiting Play Mode

```
ExitPlayModeAsync()
├── 1. Validate state (must be in Play mode)
├── 2. Set State = ExitingPlay
├── 3. Fire PreExitPlay event
├── 4. Disable physics simulation
├── 5. Despawn player pawns
├── 6. Call EndPlay on all world instances
│   ├── Unlink timer callbacks
│   ├── Deactivate all scene nodes
│   ├── Destroy PhysicsScene
│   └── Destroy VisualScene
├── 7. Shutdown GameMode
├── 8. Unload gameplay assemblies (if loaded)
├── 9. Restore world state (based on configuration):
│   ├── SerializeAndRestore: Apply snapshot
│   ├── ReloadFromAsset: Reload world from disk
│   └── PersistChanges: Do nothing
├── 10. Set State = Edit
└── 11. Fire PostExitPlay event
```

## Assembly Isolation

### GameplayAssemblyManager

Manages loading gameplay code in an isolated `AssemblyLoadContext`:

```csharp
// XRENGINE/Engine/GameplayAssemblyManager.cs
public static class GameplayAssemblyManager
{
    private static GameplayAssemblyLoadContext? _currentContext;
    private static readonly List<Assembly> _loadedAssemblies = [];
    
    public static IReadOnlyList<Assembly> LoadedAssemblies => _loadedAssemblies;
    
    public static event Action<Assembly>? AssemblyLoaded;
    public static event Action<Assembly>? AssemblyUnloading;
    public static event Action? AllAssembliesUnloaded;
    
    /// <summary>
    /// Loads gameplay assemblies into an isolated context.
    /// </summary>
    public static async Task LoadAssembliesAsync(IEnumerable<string> assemblyPaths)
    {
        // Unload previous if exists
        await UnloadAssembliesAsync();
        
        // Create new collectible context
        _currentContext = new GameplayAssemblyLoadContext();
        
        foreach (var path in assemblyPaths)
        {
            var assembly = _currentContext.LoadFromAssemblyPath(path);
            _loadedAssemblies.Add(assembly);
            AssemblyLoaded?.Invoke(assembly);
        }
    }
    
    /// <summary>
    /// Unloads all gameplay assemblies and their context.
    /// </summary>
    public static async Task UnloadAssembliesAsync()
    {
        if (_currentContext is null)
            return;
            
        foreach (var assembly in _loadedAssemblies)
            AssemblyUnloading?.Invoke(assembly);
            
        _loadedAssemblies.Clear();
        
        // Trigger unload
        _currentContext.Unload();
        _currentContext = null;
        
        // Force GC to actually unload
        for (int i = 0; i < 10; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            
            // Check if context is truly unloaded
            if (_currentContext?.IsCollectible == true)
                await Task.Delay(100);
            else
                break;
        }
        
        AllAssembliesUnloaded?.Invoke();
    }
}

public class GameplayAssemblyLoadContext : AssemblyLoadContext
{
    public GameplayAssemblyLoadContext() : base(isCollectible: true) { }
    
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // First try to load from the default context (engine assemblies)
        try
        {
            return Default.LoadFromAssemblyName(assemblyName);
        }
        catch
        {
            // Fall back to resolving from assembly path
            return null;
        }
    }
}
```

## Physics Integration

### Physics Simulation Control

Physics should only step during play mode:

```csharp
// Modify XRWorldInstance.cs
public partial class XRWorldInstance : XRObjectBase
{
    private bool _physicsEnabled = false;
    
    /// <summary>
    /// Whether physics simulation is currently active for this world.
    /// </summary>
    public bool PhysicsEnabled
    {
        get => _physicsEnabled;
        set
        {
            if (_physicsEnabled == value)
                return;
            _physicsEnabled = value;
            PhysicsEnabledChanged?.Invoke(value);
        }
    }
    
    public event Action<bool>? PhysicsEnabledChanged;
    
    public void FixedUpdate()
    {
        TickGroup(ETickGroup.PrePhysics);
        
        // Only step physics if enabled (play mode)
        if (PhysicsEnabled && Engine.PlayMode.IsPlaying)
        {
            PhysicsScene.StepSimulation();
        }
        
        TickGroup(ETickGroup.DuringPhysics);
        TickGroup(ETickGroup.PostPhysics);
    }
}
```

## GameMode Integration

### Enhanced GameMode

GameMode should have lifecycle hooks for play mode:

* **DefaultPlayerControllerClass** – type of `LocalPlayerController` to instantiate per local player (defaults to the stock controller).
* **DefaultPlayerPawnClass** – pawn component type that gets auto-spawned for the default player (defaults to `FlyingCameraPawnComponent`).

```csharp
// Enhanced XRENGINE/GameMode.cs
public class GameMode
{
    public XRWorldInstance? WorldInstance { get; internal set; }
    
    // Existing possession system...
    public Dictionary<PawnComponent, Queue<ELocalPlayerIndex>> PossessionQueue { get; } = [];
    
    /// <summary>
    /// Called when play mode begins for this GameMode's world.
    /// Override to spawn initial pawns, set up game rules, etc.
    /// </summary>
    public virtual void OnBeginPlay()
    {
        // Default: spawn a pawn for player one if configured
        SpawnDefaultPlayerPawn();
    }
    
    /// <summary>
    /// Called when play mode ends.
    /// Override to clean up game state.
    /// </summary>
    public virtual void OnEndPlay()
    {
        // Default: clear possession queues
        PossessionQueue.Clear();
    }
    
    /// <summary>
    /// Called each frame during play mode.
    /// </summary>
    public virtual void Tick(float deltaTime)
    {
    }
    
    /// <summary>
    /// Factory method for creating the default pawn for a player.
    /// Override to customize pawn creation.
    /// </summary>
    public virtual PawnComponent? CreateDefaultPawn(ELocalPlayerIndex playerIndex)
    {
        return null; // Override in game-specific GameMode
    }
    
    protected virtual void SpawnDefaultPlayerPawn()
    {
        var pawn = CreateDefaultPawn(ELocalPlayerIndex.One);
        if (pawn is not null)
        {
            ForcePossession(pawn, ELocalPlayerIndex.One);
        }
    }
    
    // Existing methods...
    public void ForcePossession(PawnComponent pawnComponent, ELocalPlayerIndex possessor) { ... }
    public void EnqueuePossession(PawnComponent pawnComponent, ELocalPlayerIndex possessor) { ... }
}
```

## Editor Integration

### EditorPlayModeController

Editor-specific play mode handling:

```csharp
// XREngine.Editor/EditorPlayModeController.cs
public static class EditorPlayModeController
{
    private static WorldStateSnapshot? _editModeSnapshot;
    
    static EditorPlayModeController()
    {
        // Subscribe to play mode events
        Engine.PlayMode.PreEnterPlay += OnPreEnterPlay;
        Engine.PlayMode.PostEnterPlay += OnPostEnterPlay;
        Engine.PlayMode.PreExitPlay += OnPreExitPlay;
        Engine.PlayMode.PostExitPlay += OnPostExitPlay;
    }
    
    private static void OnPreEnterPlay()
    {
        // Disable undo recording during play
        Undo.SuppressRecording();
        
        // Save current world state
        var currentWorld = GetCurrentEditorWorld();
        if (currentWorld is not null)
        {
            _editModeSnapshot = WorldStateSnapshot.Capture(currentWorld);
        }
        
        // Disable editor gizmos/tools
        Selection.Clear();
        TransformTool.Disable();
    }
    
    private static void OnPostEnterPlay()
    {
        // Update UI to show play mode indicators
        // (toolbar buttons, status bar, etc.)
    }
    
    private static void OnPreExitPlay()
    {
        // Nothing special needed here
    }
    
    private static void OnPostExitPlay()
    {
        // Re-enable undo recording
        Undo.ResumeRecording();
        
        // Restore world state based on configuration
        if (Engine.PlayMode.Configuration.StateRestorationMode 
            == EStateRestorationMode.SerializeAndRestore)
        {
            _editModeSnapshot?.Restore();
        }
        _editModeSnapshot = null;
        
        // Re-enable editor tools
        TransformTool.Enable();
        
        // Clear undo history for play session
        Undo.ClearHistory();
    }
    
    private static XRWorld? GetCurrentEditorWorld()
    {
        // Get the world currently being viewed in the editor
        return Engine.Windows.FirstOrDefault()?.TargetWorld;
    }
}
```

### Updated EditorState

Replace the existing `EditorState` with the engine's `PlayMode`:

```csharp
// XREngine.Editor/EditorState.cs - Updated to delegate to Engine.PlayMode
namespace XREngine.Editor;

public static class EditorState
{
    // Deprecated - use Engine.PlayMode.State instead
    [Obsolete("Use Engine.PlayMode.State instead")]
    public static EPlayModeState CurrentState => Engine.PlayMode.State;
    
    public static bool InEditMode => Engine.PlayMode.IsEditing;
    public static bool InPlayMode => Engine.PlayMode.IsPlaying;
    public static bool IsTransitioning => Engine.PlayMode.IsTransitioning;
    
    // Convenience methods for editor
    public static void EnterPlayMode() => Engine.PlayMode.EnterPlayModeAsync();
    public static void ExitPlayMode() => Engine.PlayMode.ExitPlayModeAsync();
    public static void TogglePlayMode() => Engine.PlayMode.TogglePlayMode();
}
```

## Startup World Resolution

### Priority Order for Determining Startup World

When entering play mode, the system determines which world to load:

```
1. PlayModeConfiguration.StartupWorld (explicit override)
   └── Used when testing a specific world regardless of editor view

2. Game project's configured startup world (from GameStartupSettings)
   └── The "main menu" or "initial" world defined by the game

3. Currently viewed world in editor
   └── Most common case - play the world you're editing

4. First available world instance
   └── Fallback if no other world is configured

5. Create empty default world
   └── Last resort - should rarely happen
```

### GameMode Resolution

```
1. PlayModeConfiguration.GameModeOverride
   └── Testing with a specific GameMode

2. StartupWorld.DefaultGameMode
   └── World-specific GameMode

3. StartupWorld.Settings.DefaultGameMode
   └── WorldSettings-defined GameMode

4. GameStartupSettings.DefaultGameMode (project default)
   └── Game-wide default GameMode

5. new GameMode()
   └── Base GameMode with no custom behavior
```

## Usage Examples

### Basic Play/Pause

```csharp
// In editor toolbar or menu
if (ImGui.Button(Engine.PlayMode.IsPlaying ? "⏹ Stop" : "▶ Play"))
{
    Engine.PlayMode.TogglePlayMode();
}

// Show current state
ImGui.Text($"Mode: {Engine.PlayMode.State}");
```

### Custom GameMode

```csharp
public class MyGameMode : GameMode
{
    public override void OnBeginPlay()
    {
        base.OnBeginPlay();
        
        // Spawn player at spawn point
        var spawnPoint = FindSpawnPoint();
        var pawn = CreatePlayerPawn(spawnPoint.Position);
        ForcePossession(pawn, ELocalPlayerIndex.One);
        
        // Initialize game state
        Score = 0;
        Lives = 3;
    }
    
    public override void OnEndPlay()
    {
        // Save high score
        SaveHighScore();
        
        base.OnEndPlay();
    }
    
    public override PawnComponent? CreateDefaultPawn(ELocalPlayerIndex playerIndex)
    {
        var node = new SceneNode("Player");
        var pawn = node.AddComponent<FirstPersonPawnComponent>();
        WorldInstance?.RootNodes.Add(node);
        return pawn;
    }
}
```

### Configuring Play Mode

```csharp
// Configure to always start from main menu
Engine.PlayMode.Configuration = new PlayModeConfiguration
{
    StartupWorld = mainMenuWorld,
    ReloadGameplayAssemblies = true,
    StateRestorationMode = EStateRestorationMode.SerializeAndRestore,
    SimulatePhysics = true,
};

// Or configure to test current world in-place
Engine.PlayMode.Configuration = new PlayModeConfiguration
{
    StartupWorld = null, // Use current editor world
    ReloadGameplayAssemblies = false, // Faster iteration
    StateRestorationMode = EStateRestorationMode.PersistChanges,
    SimulatePhysics = true,
};
```

### Responding to Mode Changes

```csharp
// In a component that needs to behave differently in edit vs play
public class MyComponent : XRComponent
{
    protected override void OnComponentActivated()
    {
        Engine.PlayMode.StateChanged += OnPlayModeChanged;
    }
    
    protected override void OnComponentDeactivated()
    {
        Engine.PlayMode.StateChanged -= OnPlayModeChanged;
    }
    
    private void OnPlayModeChanged(EPlayModeState newState)
    {
        if (newState == EPlayModeState.Play)
        {
            // Enable gameplay behavior
            EnableAI();
        }
        else if (newState == EPlayModeState.Edit)
        {
            // Enable editor preview behavior
            DisableAI();
            ShowEditorGizmos();
        }
    }
}
```

## Implementation Checklist

### Phase 1: Core Infrastructure
- [x] Create `Engine.PlayMode` static class
- [x] Create `EPlayModeState` enum
- [x] Create `PlayModeConfiguration` class
- [x] Create `WorldStateSnapshot` class with serialization

### Phase 2: World Lifecycle
- [x] Modify `XRWorldInstance.BeginPlay()` to respect play mode
- [x] Modify `XRWorldInstance.EndPlay()` to respect play mode
- [x] Add `PhysicsEnabled` property to `XRWorldInstance`
- [x] Modify `FixedUpdate` to check physics enabled state

### Phase 3: GameMode Enhancement
- [x] Add lifecycle hooks to `GameMode`
- [ ] Implement default pawn spawning
- [x] Add `WorldInstance` reference to `GameMode`

### Phase 4: Assembly Management
- [x] Enhance `GameplayAssemblyManager` (based on existing `GameCSProjLoader`)
- [x] Implement collectible context lifecycle
- [x] Add assembly load/unload events

### Phase 5: Editor Integration
- [x] Create `EditorPlayModeController`
- [x] Integrate with undo system
- [x] Update `EditorState` to delegate to `Engine.PlayMode`
- [x] Add play/pause/stop toolbar buttons (via ImGui State Panel)
- [x] Add keyboard shortcuts (F5 for play, F6 for step)

### Phase 6: UI/UX
- [x] Visual indication of current mode (color-coded state in State Panel)
- [x] Add ImGui "Engine State" panel showing:
  - Play mode state with color-coded status
  - Play/Pause/Stop/Step controls
  - Active GameMode information
  - Player count and controller details
  - World instances with physics/playing status
  - Windows and viewport assignments
- [ ] Disable certain editor panels during play
- [ ] Add "Play from here" functionality
- [x] Add pause functionality (separate from edit mode)

## Future Considerations

1. **Pause Mode**: Separate from edit mode, allows inspection without full stop
2. **Multi-world Play**: Supporting multiple world instances during play
3. **Network Play**: Handling play mode in multiplayer scenarios
4. **PIE (Play In Editor)**: Running game in a separate viewport within editor
5. **Simulate Mode**: Physics and animation without full gameplay
