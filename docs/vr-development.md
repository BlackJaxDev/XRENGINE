# VR Development

XRENGINE provides comprehensive VR development capabilities with support for OpenXR, SteamVR, advanced tracking, and VR-optimized rendering.

## Overview

The VR system is built around OpenXR standards with SteamVR integration, providing cross-platform VR development capabilities with advanced features for immersive experiences.

## VR Architecture

### VR State Management
```csharp
public static class VRState
{
    public enum VRMode
    {
        Server,     // VR system awaits inputs from client
        Client,     // VR system sends inputs to server
        Local       // All VR handling in this process
    }
    
    public static VR Api { get; }
    public static bool IsInVR { get; }
    public static ETrackingUniverseOrigin Origin { get; set; }
}
```

### VR Modes

#### Local Mode
All VR input and rendering handled by the main process:
```csharp
public static async Task<bool> InitializeLocal(
    IActionManifest actionManifest,
    VrManifest vrManifest,
    XRWindow window)
```

#### Client Mode
VR input sent to server, rendered frames received:
```csharp
public static async Task<bool> IninitializeClient(
    IActionManifest actionManifest,
    VrManifest vrManifest)
```

#### Server Mode
VR input received, rendered frames sent to client:
```csharp
public static bool InitializeServer()
```

## OpenXR Integration

### OpenXR API
Full OpenXR standard implementation:

```csharp
public unsafe partial class OpenXRAPI : XRBase
{
    public XR Api { get; }
    public XRWindow? Window { get; set; }
    
    public void Initialize();
    public void RenderFrame(DelRenderToFBO renderCallback);
    public void CleanUp();
}
```

### OpenXR Features
- **Cross-Platform**: Works with any OpenXR-compatible headset
- **Multiple Graphics APIs**: OpenGL and Vulkan support
- **Parallel Rendering**: Simultaneous eye rendering when supported
- **Advanced Extensions**: HTC, Valve, and other vendor extensions

### OpenXR Extensions
```csharp
private readonly string[] HTC_Extensions =
[
    HtcxViveTrackerInteraction.ExtensionName,
    HtcFacialTracking.ExtensionName,
    HtcFoveation.ExtensionName,
    HtcPassthrough.ExtensionName,
    HtcAnchor.ExtensionName,
    HtcBodyTracking.ExtensionName,
];
```

## SteamVR Integration

### SteamVR API
Native SteamVR support with action manifest system:

```csharp
public class VR
{
    public event Action<VrDevice>? DeviceDetected;
    public List<VrDevice> TrackedDevices { get; }
    
    public bool TryStart(EVRApplicationType appType);
    public void SetActionManifest(IActionManifest manifest);
    public void UpdateInput(float predictionTime);
}
```

### Action Manifest System
Define VR input actions and bindings:

```csharp
public class ActionManifest<TCategory, TAction> : IActionManifest
{
    public List<Action> Actions { get; set; }
    public List<ActionSet> ActionSets { get; set; }
    public List<DefaultBinding> DefaultBindings { get; set; }
}
```

### VR Actions
```csharp
public enum EVRGameAction
{
    // Hand actions
    LeftHand_Position,
    LeftHand_Rotation,
    LeftHand_Trigger,
    LeftHand_Grip,
    
    RightHand_Position,
    RightHand_Rotation,
    RightHand_Trigger,
    RightHand_Grip,
    
    // Headset actions
    Headset_Position,
    Headset_Rotation,
    
    // Tracker actions
    Tracker1_Position,
    Tracker1_Rotation,
}
```

## VR Components

### VRPlayerCharacterComponent
VR-specific player character with IK and movement:

```csharp
public class VRPlayerCharacterComponent : XRComponent, IRenderable
{
    public bool IsCalibrating { get; set; }
    public Transform? Headset { get; set; }
    public Transform? LeftController { get; set; }
    public Transform? RightController { get; set; }
    
    // VR-specific movement
    public bool UseRoomScale { get; set; }
    public bool UseTeleportation { get; set; }
    public float TeleportDistance { get; set; }
}
```

### VRDeviceModelComponent
Renders VR device models (controllers, trackers):

```csharp
public abstract class VRDeviceModelComponent : ModelComponent
{
    protected abstract DeviceModel? GetRenderModel(VrDevice? device);
    public void LoadModelAsync(DeviceModel? deviceModel);
}
```

### VRTrackerCollectionComponent
Manages VR trackers and body tracking:

```csharp
public class VRTrackerCollectionComponent : XRComponent
{
    public Dictionary<uint, (VrDevice?, VRTrackerTransform)> Trackers { get; }
    
    private void AddRealTracker(VrDevice device);
    private void AddVirtualTracker(string name, Vector3 position);
}
```

## VR Rendering

### Stereo Rendering
Optimized stereo rendering for VR:

```csharp
public class VRViewport : XRViewport
{
    public EVREye Eye { get; set; }
    public Matrix4x4 ProjectionMatrix { get; set; }
    public Matrix4x4 ViewMatrix { get; set; }
}
```

### Single-Pass Stereo
Reduce draw calls by 50%:

```glsl
#extension GL_OVR_multiview2 : require

layout(num_views = 2) in;
void main()
{
    gl_Position = u_mvpMatrix[gl_ViewID_OVR] * a_position;
}
```

### Parallel Eye Rendering
Vulkan-based parallel rendering:

```csharp
if (_parallelRenderingEnabled && Window?.Renderer is VulkanRenderer)
{
    // Parallel rendering path for Vulkan
    RenderEyesInParallel(renderCallback, projectionViews);
}
else
{
    // Sequential rendering path
    for (uint i = 0; i < _viewCount; i++)
    {
        RenderEye(i, renderCallback, projectionViews);
    }
}
```

## VR Tracking

### Device Tracking
Comprehensive tracking for all VR devices:

```csharp
public class VrDevice
{
    public uint DeviceIndex { get; }
    public ETrackedDeviceClass DeviceClass { get; }
    public bool IsConnected { get; }
    public bool IsTracking { get; }
    
    public Matrix4x4 GetPose();
    public Vector3 GetVelocity();
    public Vector3 GetAngularVelocity();
}
```

### Body Tracking
Advanced body tracking with multiple trackers:

```csharp
public class VRTrackerTransform : Transform
{
    public VrDevice? Tracker { get; set; }
    public bool IsVirtual { get; set; }
    
    protected override void UpdateTransform()
    {
        if (Tracker?.IsTracking == true)
        {
            var pose = Tracker.GetPose();
            SetWorldMatrix(pose);
        }
    }
}
```

### Hand Tracking
Finger tracking and gesture recognition:

```csharp
public class VRHandTracking
{
    public Vector3[] FingerPositions { get; }
    public float[] FingerCurves { get; }
    public bool IsTracked { get; }
    
    public bool IsGesture(GestureType gesture);
    public float GetFingerCurve(FingerType finger);
}
```

## VR IK System

### VRIKSolverComponent
VR-specific inverse kinematics:

```csharp
public class VRIKSolverComponent : IKSolverComponent
{
    public IKSolverVR Solver { get; }
    public HumanoidComponent Humanoid { get; }
    
    public void GuessHandOrientations();
}
```

### VR IK Calibration
Accurate VR character calibration:

```csharp
public class VRIKCalibrator
{
    public class Settings
    {
        public float HeadHeight { get; set; }
        public float ArmLength { get; set; }
        public float LegLength { get; set; }
        public float ShoulderWidth { get; set; }
    }
    
    public static void Calibrate(HumanoidComponent humanoid, Settings settings);
}
```

## VR Input

### Input Actions
Define and handle VR input:

```csharp
public class VRAction
{
    public string Name { get; set; }
    public EActionType Type { get; set; }
    public bool IsPressed { get; }
    public float Value { get; }
    public Vector3 Position { get; }
    public Quaternion Rotation { get; }
}
```

### Input Handling
```csharp
public class VRInputHandler
{
    public void UpdateInput();
    public bool GetActionPressed(string actionName);
    public float GetActionValue(string actionName);
    public Vector3 GetActionPosition(string actionName);
}
```

## VR Performance

### Performance Requirements
- **Frame Rate**: 90+ FPS for VR
- **Latency**: < 20ms motion-to-photon
- **Resolution**: High resolution for clarity
- **Tracking**: Sub-millimeter precision

### Performance Optimization
```csharp
public class VRPerformanceSettings
{
    public bool EnableFoveatedRendering { get; set; }
    public bool EnableSinglePassStereo { get; set; }
    public bool EnableParallelRendering { get; set; }
    public int TargetFrameRate { get; set; } = 90;
}
```

### Performance Monitoring
```csharp
public class VRPerformanceMonitor
{
    public float FrameTime { get; }
    public float CPUFrameTime { get; }
    public float GPUFrameTime { get; }
    public int DroppedFrames { get; }
    public float ReprojectionRatio { get; }
}
```

## VR Development Tools

### VR Debug Visualization
```csharp
public class VRDebugVisualizer
{
    public bool ShowTrackingBounds { get; set; }
    public bool ShowControllerModels { get; set; }
    public bool ShowTrackingPoints { get; set; }
    public bool ShowPerformanceMetrics { get; set; }
}
```

### VR Testing
```csharp
public class VRTestSuite
{
    public void TestTrackingAccuracy();
    public void TestInputResponsiveness();
    public void TestRenderingPerformance();
    public void TestComfortMetrics();
}
```

## VR Configuration

### VR Settings
```json
{
  "VRMode": "Local",
  "TrackingUniverseOrigin": "Standing",
  "EnableRoomScale": true,
  "EnableBodyTracking": true,
  "EnableHandTracking": true,
  "EnableEyeTracking": false,
  "EnableFacialTracking": false,
  "TargetFrameRate": 90,
  "EnableReprojection": true
}
```

### Action Manifest Configuration
```json
{
  "actions": [
    {
      "name": "trigger",
      "type": "boolean",
      "binding": "/user/hand/right/input/trigger"
    },
    {
      "name": "grip",
      "type": "boolean", 
      "binding": "/user/hand/right/input/grip"
    }
  ],
  "action_sets": [
    {
      "name": "gameplay",
      "usage": "leftright"
    }
  ]
}
```

## Example: Basic VR Setup

```csharp
// Initialize VR
var actionManifest = new ActionManifest<EVRActionCategory, EVRGameAction>
{
    Actions = GetActions(),
    ActionSets = GetActionSets(),
    DefaultBindings = GetDefaultBindings()
};

var vrManifest = new VrManifest
{
    AppKey = "com.example.vrapp",
    IsDashboardOverlay = false,
    WindowsPath = Environment.ProcessPath
};

// Start VR in local mode
await Engine.VRState.InitializeLocal(actionManifest, vrManifest, window);

// Create VR player
var player = new SceneNode();
var vrPlayer = player.AddComponent<VRPlayerCharacterComponent>();
var humanoid = player.AddComponent<HumanoidComponent>();
var vrIK = player.AddComponent<VRIKSolverComponent>();

// Set up VR tracking
vrPlayer.Headset = headsetTransform;
vrPlayer.LeftController = leftControllerTransform;
vrPlayer.RightController = rightControllerTransform;
```

## Example: VR Input Handling

```csharp
public class VRInputHandler : XRComponent
{
    protected internal override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RegisterTick(ETickGroup.Normal, ETickOrder.Input, UpdateInput);
    }
    
    private void UpdateInput()
    {
        // Handle trigger input
        if (Engine.VRState.Actions["gameplay"]["trigger"].IsPressed)
        {
            // Trigger action
        }
        
        // Handle hand position
        var leftHandPos = Engine.VRState.Actions["gameplay"]["leftHand_position"].Position;
        var rightHandPos = Engine.VRState.Actions["gameplay"]["rightHand_position"].Position;
        
        // Update IK targets
        humanoid.LeftHandTarget.Position = leftHandPos;
        humanoid.RightHandTarget.Position = rightHandPos;
    }
}
```

## Best Practices

### VR Development
- **Test on Real Hardware**: Always test on actual VR devices
- **Maintain 90 FPS**: Critical for VR comfort
- **Reduce Motion Sickness**: Avoid artificial locomotion
- **Optimize for Performance**: Use VR-specific optimizations

### VR Comfort
- **Provide Comfort Options**: Multiple movement methods
- **Use Comfortable Interactions**: Natural hand movements
- **Avoid Rapid Movements**: Smooth, predictable motion
- **Provide Rest Areas**: Allow users to take breaks

### VR Performance
- **Profile Regularly**: Monitor frame times and performance
- **Use VR Optimizations**: Single-pass stereo, foveated rendering
- **Optimize Assets**: Use appropriate LOD and texture compression
- **Test on Target Hardware**: Ensure performance on minimum specs

## Related Documentation
- [Component System](components.md)
- [Rendering System](rendering.md)
- [Animation System](animation.md)
- [Physics System](physics.md) 