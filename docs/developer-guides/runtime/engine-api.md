# Engine API Reference

The `Engine` facade in `XREngine.Runtime.Bootstrap` composes application
startup, the game loop, and installed backends. Lower projects expose
`RuntimeEngine` in `XREngine.Runtime.Rendering` and managed contracts in
Runtime.Core, Data, Input, and Audio. Public namespaces can stay the same after
a type moves to another project; see [Runtime Project Organization](../../architecture/runtime/project-organization.md).
The implementation entry points are [Engine.cs](../../../XREngine.Runtime.Bootstrap/Engine/Engine.cs)
and [RuntimeEngine.cs](../../../XREngine.Runtime.Rendering/Runtime/RuntimeEngine.cs).

## Engine Class

The Bootstrap-owned application facade that connects lower runtime systems to
desktop rendering, audio, input, physics, and XR implementations.

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
    
    // Configuration
    public static UserSettings UserSettings { get; set; }
    public static GameStartupSettings GameSettings { get; set; }
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

The lower `RuntimeEngine.Windows` and `RuntimeEngine.VRState` expose window and
VR state without returning native window or XR runtime objects.

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
public static XRWindow CreateWindow(GameWindowStartupSettings windowSettings);
public static void CreateWindows(List<GameWindowStartupSettings> windows);
public static void RemoveWindow(XRWindow window);
```

`GameWindowStartupSettings` and these creation methods live in Bootstrap;
`XRWindow` is a renderer-neutral facade in Runtime.Rendering. The desktop
window module owns the actual native window, event pump, and handles.
See [Engine.Windows.cs](../../../XREngine.Runtime.Bootstrap/RenderingHost/Engine.Windows.cs)
and [XRWindow.cs](../../../XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs).

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
    public float GainScale { get; set; }
    public EAudioTransport DefaultTransport { get; set; }
    public EAudioEffects DefaultEffects { get; set; }
    public ListenerContext NewListener(string? name = null);
}
```

`AudioManager` and its listener/source/buffer contracts live in
`XREngine.Audio`. Bootstrap registers the OpenAL, NAudio, and Steam Audio
implementations before creating listeners; scene audio components live in
`XREngine.Runtime.AudioIntegration`.
See [AudioManager.cs](../../../XREngine.Audio/AudioManager.cs) for the current
listener and backend-selection API.

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
public sealed class RuntimeVrState
{
    public VRRuntime ActiveRuntime { get; set; }
    public bool IsInVR { get; set; }
    public bool IsOpenVRActive { get; }
    public bool IsOpenXRActive { get; }
    public Task<bool> InitializeLocal(IRuntimeOpenVrActionManifest actions,
        RuntimeOpenVrApplicationManifest manifest, XRWindow window);
}
```

Read this state through `RuntimeEngine.VRState`. OpenVR action and application
manifests are neutral contracts in `XREngine.Data/Input`; native action objects
and runtime ownership stay in `XREngine.Runtime.XR.OpenVR`. OpenXR's lower
surface is `IOpenXrRuntime`, implemented by `XREngine.Runtime.XR.OpenXR`.
Bootstrap installs the lifecycle and input services used by `InitializeLocal`.
See [RuntimeVrState.cs](../../../XREngine.Runtime.Rendering/Runtime/RuntimeVrState.cs)
and [IVRGameStartupSettings.cs](../../../XREngine.Runtime.Core/Settings/RuntimeStartupContracts/IVRGameStartupSettings.cs).

## Rendering System

Manages rendering state and pipeline.

### Properties
```csharp
public static partial class Rendering
{
    public static EngineSettings Settings { get; set; }
    public static EngineSettings GlobalDefaultSettings { get; set; }
    public static EngineSettings? ProjectDefaultSettings { get; set; }
    public static EngineSettings DefaultSettings { get; set; }
    public static event Action? SettingsChanged;
    
    public enum ELoopType
    {
        Sequential,
        Asynchronous,
        Parallel
    }
}
```

`GlobalDefaultSettings` is saved outside any project as `engine_defaults.asset`.
When a project is loaded, `ProjectDefaultSettings` points at `Config/engine_defaults.asset`
and becomes the current `Settings` object.

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
    public EAudioTransport AudioTransport { get; set; }
    public EAudioEffects AudioEffects { get; set; }
    public bool AudioArchitectureV2 { get; set; }
    public int AudioSampleRate { get; set; }
    public EPhysicsLibrary PhysicsLibrary { get; set; }
    public float? TargetFramesPerSecond { get; set; }
    public float? UnfocusedTargetFramesPerSecond { get; set; }
    public IVector2 WindowedResolution { get; set; }
    public bool DisableAudioOnDefocus { get; set; }
    public double DebugOutputRecencySeconds { get; set; }
}
```

`AudioTransport`, `AudioEffects`, `AudioArchitectureV2`, and
`AudioSampleRate` are the current audio composition settings. The older
`AudioLibrary` value remains serialized for compatibility; backend selection
uses the transport/effects settings.

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
    RuntimeOpenVrApplicationManifest? VRManifest { get; set; }
    IRuntimeOpenVrActionManifest? ActionManifest { get; }
    EVRRuntime VRRuntime { get; set; }
    bool StartVrOnLaunch { get; set; }
    EVrViewRenderMode VrViewRenderMode { get; set; }
    bool EnableOpenXrVulkanParallelRendering { get; set; }
    string GameName { get; set; }
    (Environment.SpecialFolder folder, string relativePath)[] GameSearchPaths { get; set; }
}

public class VRGameStartupSettings<TCategory, TAction> : GameStartupSettings, IVRGameStartupSettings
    where TCategory : struct, Enum
    where TAction : struct, Enum
{
    public RuntimeOpenVrApplicationManifest? VRManifest { get; set; }
    public RuntimeOpenVrActionManifest<TCategory, TAction>? ActionManifest { get; set; }
    public EVRRuntime VRRuntime { get; set; }
    public bool StartVrOnLaunch { get; set; }
    public EVrViewRenderMode VrViewRenderMode { get; set; }
    public bool EnableOpenXrVulkanParallelRendering { get; set; }
    public string GameName { get; set; }
    public (Environment.SpecialFolder folder, string relativePath)[] GameSearchPaths { get; set; }
    IRuntimeOpenVrActionManifest? IVRGameStartupSettings.ActionManifest => ActionManifest;
}
```

`IVRGameStartupSettings` is a lower Runtime.Core contract. The generic startup
settings type is composed in Bootstrap; its manifest value types live in Data.
Native OpenVR and OpenXR implementations are installed by their respective XR
modules rather than exposed through these settings.

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
    VRRuntime = EVRRuntime.Auto,
    StartVrOnLaunch = true,
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

// Create VR player
var vrPlayer = new SceneNode("VRPlayer");
var humanoid = vrPlayer.AddComponent<HumanoidComponent>();
var vrIK = vrPlayer.AddComponent<VRIKSolverComponent>();

// Add the player to your world, then start with the configured XR runtime.
Engine.Run(vrSettings, gameState);
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
- [Component API](../components/component-api.md)
- [Scene Architecture](../../architecture/scene/overview.md)
- [Rendering Runtime Overview](../../architecture/rendering/runtime-overview.md)
- [Physics Architecture](../../architecture/physics/overview.md)
- [Animation API](../animation/animation-api.md)
- [VR Developer Guide](../vr/vr-development.md)
