# Engine API Reference

XRENGINE's core engine API provides the foundation for all engine functionality, including initialization, game loop management, and core systems.

## Engine Class

The main engine class that manages all core systems and provides the primary API.

### Static Properties

```csharp
public static partial class Engine
{
    // Core systems
    public static Time Time { get; }
    public static AudioManager Audio { get; }
    public static BaseNetworkingManager? Networking { get; }
    public static partial class Rendering { get; }
    public static partial class State { get; }
    public static partial class VRState { get; }
    
    // Configuration
    public static UserSettings UserSettings { get; set; }
    public static GameStartupSettings GameSettings { get; set; }
    public static IEventListReadOnly<XRWindow> Windows { get; }
    public static IReadOnlyCollection<XRWorldInstance> WorldInstances { get; }
    
    // Utilities
    public static AssetManager Assets { get; }
    public static Random Random { get; }
    public static CodeProfiler Profiler { get; }
    
    // State flags
    public static bool StartingUp { get; }
    public static bool ShuttingDown { get; }
    public static bool IsRenderThread { get; }
    public static int RenderThreadId { get; }
    
    // Events
    public static event Action<bool>? FocusChanged;
}
```

### Core Methods

#### Initialization
```csharp
public static void Run(GameStartupSettings startupSettings, GameState state);
public static bool Initialize(GameStartupSettings startupSettings, GameState state, bool beginPlayingAllWorlds = true);
public static void ShutDown();
internal static void Cleanup();
```

#### Game Loop
```csharp
public static void RunGameLoop();
public static void BlockForRendering();
private static bool IsEngineStillActive();
```

#### Window Management
```csharp
public static void CreateWindow(GameWindowStartupSettings windowSettings);
public static void CreateWindows(List<GameWindowStartupSettings> windows);
public static void RemoveWindow(XRWindow window);
```

#### World Management
```csharp
public static void BeginPlayAllWorlds();
public static void EndPlayAllWorlds();
```

#### Asset Management
```csharp
public static T LoadOrGenerateAsset<T>(Func<T>? generateFactory, string assetName, bool allowLoading, params string[] folderNames) where T : XRAsset, new();
public static GameState LoadOrGenerateGameState(Func<GameState>? generateFactory = null, string assetName = "state.asset", bool allowLoading = true);
public static GameStartupSettings LoadOrGenerateGameSettings(Func<GameStartupSettings>? generateFactory = null, string assetName = "startup.asset", bool allowLoading = true);
```

## Time System

Manages time, delta time, and frame timing.

### Properties
```csharp
public static class Time
{
    public static EngineTimer Timer { get; }
    
    // Delta time properties
    public static float UndilatedDelta { get; }
    public static float Delta { get; }
    public static float SmoothedUndilatedDelta { get; }
    public static float SmoothedDelta { get; }
    public static float FixedDelta { get; }
    public static float ElapsedTime { get; }
    
    // Initialization
    public static void Initialize(GameStartupSettings gameSettings, UserSettings userSettings);
}
```

### Timer Class
```csharp
public class EngineTimer
{
    public float TargetFramesPerSecond { get; set; }
    public float TargetRenderFrequency { get; set; }
    public float UnfocusedTargetFramesPerSecond { get; set; }
    
    // Events
    public event Action? PreUpdateFrame;
    public event Action? UpdateFrame;
    public event Action? PostUpdateFrame;
    public event Action? FixedUpdate;
    public event Action? SwapBuffers;
    public event Action? RenderFrame;
    public event Action? CollectVisible;
    
    public void RunGameLoop();
    public void BlockForRendering(Func<bool> isActive);
    public void Stop();
    public float Time();
}
```

## Audio System

Manages audio playback and 3D spatial audio.

### Properties
```csharp
public class AudioManager
{
    public bool Enabled { get; set; }
    public float MasterVolume { get; set; }
    public float MusicVolume { get; set; }
    public float SFXVolume { get; set; }
    
    // Audio methods would be implemented here
    public void PlaySound(AudioClip clip, Vector3 position);
    public void PlayMusic(AudioClip clip, bool loop = true);
    public void StopMusic();
    public void FadeIn(float duration);
    public void FadeOut(float duration);
}
```

## Input System

Manages input from various devices including VR controllers.

### Properties
```csharp
public static class Input
{
    public static bool IsKeyPressed(Key key);
    public static bool IsMouseButtonPressed(MouseButton button);
    public static Vector2 MousePosition { get; }
    public static Vector2 MouseDelta { get; }
}
```

### VR Input
```csharp
public static partial class VRState
{
    public static VR Api { get; }
    public static Dictionary<string, Dictionary<string, OpenVR.NET.Input.Action>> Actions { get; }
    public static ETrackingUniverseOrigin Origin { get; set; }
    public static VRIKCalibrator.Settings CalibrationSettings { get; set; }
    
    public enum VRMode
    {
        Server,
        Client,
        Local
    }
    
    // VR initialization
    public static async Task InitializeLocal(IActionManifest actionManifest, VrManifest? vrManifest, XRWindow window);
}
```

## Rendering System

Manages rendering state and pipeline.

### Properties
```csharp
public static partial class Rendering
{
    public static EngineSettings Settings { get; set; }
    public static event Action? SettingsChanged;
    
    public enum ELoopType
    {
        Sequential,
        Asynchronous,
        Parallel
    }
}
```

### Settings
```csharp
public class EngineSettings : XRAsset
{
    public ERenderLibrary RenderLibrary { get; set; }
    public bool PreferNVStereo { get; set; }
    public bool EnableVSync { get; set; }
    public int TargetFramesPerSecond { get; set; }
    public ELoopType RecalcChildMatricesLoopType { get; set; }
    public bool RenderTransformDebugInfo { get; set; }
    public bool RenderTransformLines { get; set; }
    public bool RenderTransformPoints { get; set; }
    public bool RenderTransformCapsules { get; set; }
    public bool RenderMesh3DBounds { get; set; }
    public Color TransformLineColor { get; set; }
    public Color TransformPointColor { get; set; }
    public Color TransformCapsuleColor { get; set; }
}
```

## State Management

Manages game state and player information.

### Properties
```csharp
public static partial class State
{
    public static bool IsEditor { get; }
    public static bool IsPlaying { get; }
    public static JobManager Jobs { get; }
    
    // Local player management
    public static LocalPlayerController?[] LocalPlayers { get; }
    public static LocalPlayerController? GetLocalPlayer(ELocalPlayerIndex index);
    public static LocalPlayerController GetOrCreateLocalPlayer(ELocalPlayerIndex index);
    public static bool RemoveLocalPlayer(ELocalPlayerIndex index);
    
    // Events
    public static event Action<LocalPlayerController>? LocalPlayerAdded;
    public static event Action<LocalPlayerController>? LocalPlayerRemoved;
}
```

## User Settings

Configuration and user preferences.

### Properties
```csharp
public class UserSettings : XRBase
{
    public EWindowState WindowState { get; set; }
    public EVSyncMode VSync { get; set; }
    public EEngineQuality TextureQuality { get; set; }
    public EEngineQuality ModelQuality { get; set; }
    public EEngineQuality SoundQuality { get; set; }
    public ERenderLibrary RenderLibrary { get; set; }
    public EAudioLibrary AudioLibrary { get; set; }
    public EPhysicsLibrary PhysicsLibrary { get; set; }
    public float? TargetFramesPerSecond { get; set; }
    public float? UnfocusedTargetFramesPerSecond { get; set; }
    public IVector2 WindowedResolution { get; set; }
    public bool DisableAudioOnDefocus { get; set; }
    public double DebugOutputRecencySeconds { get; set; }
}
```

## Game Startup Settings

Configuration for engine initialization.

### Properties
```csharp
public class GameStartupSettings : XRAsset
{
    public List<GameWindowStartupSettings> StartupWindows { get; set; }
    public EOutputVerbosity OutputVerbosity { get; set; }
    public bool UseIntegerWeightingIds { get; set; }
    public UserSettings DefaultUserSettings { get; set; }
    public ETwoPlayerPreference TwoPlayerViewportPreference { get; set; }
    public EThreePlayerPreference ThreePlayerViewportPreference { get; set; }
    public string TexturesFolder { get; set; }
    public float? TargetUpdatesPerSecond { get; set; }
    public float FixedFramesPerSecond { get; set; }
    public bool RunVRInPlace { get; set; }
    public Dictionary<int, string> LayerNames { get; set; }
    public EMaxMirrorRecursionCount MaxMirrorRecursionCount { get; set; }
    
    // Networking
    public ENetworkingType NetworkingType { get; set; }
    public string UdpMulticastGroupIP { get; set; }
    public int UdpMulticastPort { get; set; }
    public int UdpClientRecievePort { get; set; }
    public int UdpServerSendPort { get; set; }
    public string ServerIP { get; set; }
    
    public enum ENetworkingType
    {
        Server,
        Client,
        P2PClient,
        Local
    }
    
    public enum EMaxMirrorRecursionCount
    {
        None = 0,
        One = 1,
        Two = 2,
        Four = 4,
        Eight = 8,
        Sixteen = 16
    }
}
```

### Window Settings
```csharp
public class GameWindowStartupSettings : XRBase
{
    public string? WindowTitle { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public EWindowState WindowState { get; set; }
    public bool VSync { get; set; }
    public bool TransparentFramebuffer { get; set; }
    public XRWorld? TargetWorld { get; set; }
    public ELocalPlayerIndexMask LocalPlayers { get; set; }
}
```

### VR Settings
```csharp
public interface IVRGameStartupSettings
{
    VrManifest? VRManifest { get; set; }
    IActionManifest? ActionManifest { get; }
    string GameName { get; set; }
    (Environment.SpecialFolder folder, string relativePath)[] GameSearchPaths { get; set; }
}

public class VRGameStartupSettings<TCategory, TAction> : GameStartupSettings, IVRGameStartupSettings
    where TCategory : struct, Enum
    where TAction : struct, Enum
{
    public VrManifest? VRManifest { get; set; }
    public ActionManifest<TCategory, TAction>? ActionManifest { get; set; }
    public string GameName { get; set; }
    public (Environment.SpecialFolder folder, string relativePath)[] GameSearchPaths { get; set; }
    IActionManifest? IVRGameStartupSettings.ActionManifest => ActionManifest;
}
```

## Game State

Base class for game-specific state.

### Properties
```csharp
public class GameState : XRAsset
{
    public List<GameWindowStartupSettings>? Windows { get; set; }
    public List<XRWorldInstance>? Worlds { get; set; }
}
```

## Example: Basic Engine Setup

```csharp
// Create startup settings
var settings = new GameStartupSettings
{
    StartupWindows = new List<GameWindowStartupSettings>
    {
        new GameWindowStartupSettings
        {
            WindowTitle = "My XR Game",
            Width = 1920,
            Height = 1080,
            VSync = false,
            TargetWorld = new XRWorld("GameWorld")
        }
    },
    DefaultUserSettings = new UserSettings
    {
        TargetFramesPerSecond = 90.0f,
        VSync = EVSyncMode.Off
    },
    TargetUpdatesPerSecond = 90.0f,
    FixedFramesPerSecond = 45.0f
};

// Create game state
var gameState = new GameState();

// Initialize and run engine
Engine.Run(settings, gameState);
```

## Example: Custom Game State

```csharp
public class MyGameState : GameState
{
    private SceneNode player;
    private CameraComponent camera;
    
    public override void Initialize()
    {
        base.Initialize();
        
        // Create camera
        var cameraNode = new SceneNode("Camera");
        camera = cameraNode.AddComponent<CameraComponent>();
        camera.FieldOfView = 90.0f;
        camera.NearClipPlane = 0.1f;
        camera.FarClipPlane = 1000.0f;
        
        // Create player
        player = new SceneNode("Player");
        player.AddComponent<PlayerComponent>();
        player.AddComponent<HumanoidComponent>();
        
        // Add to world
        var world = new XRWorld("GameWorld");
        world.Scenes.Add(new XRScene("MainScene", cameraNode, player));
        
        Worlds = new List<XRWorldInstance> { XRWorldInstance.GetOrInitWorld(world) };
    }
}
```

## Example: VR Integration

```csharp
// VR startup settings
var vrSettings = new VRGameStartupSettings<EVRActionCategory, EVRGameAction>
{
    GameName = "VR Game",
    ActionManifest = CreateActionManifest(),
    VRManifest = CreateVRManifest(),
    RunVRInPlace = true,
    StartupWindows = new List<GameWindowStartupSettings>
    {
        new GameWindowStartupSettings
        {
            WindowTitle = "VR Game",
            Width = 1920,
            Height = 1080,
            TargetWorld = new XRWorld("VRWorld")
        }
    }
};

// Initialize VR
await Engine.VRState.InitializeLocal(vrSettings.ActionManifest, vrSettings.VRManifest, window);

// Create VR player
var vrPlayer = new SceneNode("VRPlayer");
var humanoid = vrPlayer.AddComponent<HumanoidComponent>();
var vrIK = vrPlayer.AddComponent<VRIKSolverComponent>();
```

## Performance Monitoring

### Engine Profiling
```csharp
public static class Engine
{
    public static CodeProfiler Profiler { get; }
    
    public static void BeginProfile(string name);
    public static void EndProfile(string name);
    public static void ResetProfiles();
}
```

### Frame Timing
```csharp
public static class Time
{
    public static float UndilatedDelta { get; }
    public static float Delta { get; }
    public static float SmoothedDelta { get; }
    public static float FixedDelta { get; }
    public static float ElapsedTime { get; }
}
```

## Error Handling

### Debug Output
```csharp
public static class Debug
{
    public static void Out(string message, int level = 0);
    public static void LogWarning(string message, int level = 0);
    public static void LogError(string message, int level = 0);
    public static void Assert(bool condition, string message);
}
```

### Exception Handling
```csharp
public static class Engine
{
    public static event Action<Exception>? UnhandledException;
    
    public static void HandleException(Exception ex);
    public static void SetExceptionHandler(Action<Exception> handler);
}
```

## Configuration Files

### Engine Configuration
```json
{
  "Engine": {
    "TargetFramesPerSecond": 90,
    "UnfocusedTargetFramesPerSecond": 30,
    "RenderLibrary": "OpenGL",
    "VSync": "Off",
    "WindowTitle": "XRENGINE",
    "TransparentFramebuffer": false
  }
}
```

### User Settings
```json
{
  "UserSettings": {
    "Audio": {
      "MasterVolume": 1.0,
      "MusicVolume": 0.8,
      "SFXVolume": 1.0,
      "DisableAudioOnDefocus": true
    },
    "Graphics": {
      "RenderLibrary": "OpenGL",
      "VSync": "Off",
      "TargetFramesPerSecond": 90,
      "TextureQuality": "Highest",
      "ModelQuality": "Highest"
    }
  }
}
```

## Related Documentation
- [Component System](../components.md)
- [Scene System](../scene.md)
- [Rendering System](../rendering.md)
- [Physics System](../physics.md)
- [Animation System](../animation.md)
- [VR Development](../vr-development.md) 