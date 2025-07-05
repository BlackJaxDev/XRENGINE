# Component System

XRENGINE uses a component-based architecture where SceneNodes contain components that provide specific functionality. Components register into the tick system and are executed in parallel groups.

## Overview

The component system is the foundation of XRENGINE's object-oriented design. Each component:
- Inherits from `XRComponent`
- Registers for specific tick groups and orders
- Can be added to SceneNodes in the scene hierarchy
- Provides specific functionality (rendering, physics, animation, etc.)

## Core Component Classes

### XRComponent
The base class for all components in the engine.

```csharp
public abstract class XRComponent : XRBase
{
    // Component lifecycle
    protected internal virtual void OnComponentActivated() { }
    protected internal virtual void OnComponentDeactivated() { }
    
    // Tick registration
    protected void RegisterTick(ETickGroup group, ETickOrder order, Action callback);
    protected void UnregisterTick(ETickGroup group, ETickOrder order, Action callback);
}
```

## Tick System

Components register for specific tick groups that execute in parallel:

### Tick Groups
- **Normal**: General game logic, input handling, animation
- **PostPhysics**: Operations that depend on physics simulation
- **Late**: Final updates, UI, rendering preparation
- **FixedUpdate**: Physics simulation, character movement

### Tick Orders
- **Scene**: Scene graph operations
- **Animation**: Animation and IK calculations
- **Physics**: Physics simulation
- **Rendering**: Render command generation

## Major Component Categories

### Rendering Components

#### ModelComponent
Renders 3D models with materials and supports LOD systems.

```csharp
public class ModelComponent : XRComponent, IRenderable
{
    public Model? Model { get; set; }
    public bool CastShadows { get; set; }
    public bool ReceiveShadows { get; set; }
}
```

#### MeshRenderer
Handles mesh rendering with shader programs and materials.

```csharp
public class MeshRenderer : XRComponent, IRenderable
{
    public XRMesh? Mesh { get; set; }
    public XRMaterial? Material { get; set; }
}
```

### Physics Components

#### DynamicRigidBodyComponent
Represents dynamic physics objects that can move and be affected by forces.

```csharp
public class DynamicRigidBodyComponent : XRComponent
{
    public float Mass { get; set; }
    public Vector3 LinearVelocity { get; set; }
    public Vector3 AngularVelocity { get; set; }
}
```

#### StaticRigidBodyComponent
Represents static physics objects that don't move.

```csharp
public class StaticRigidBodyComponent : XRComponent
{
    public IPhysicsGeometry Geometry { get; set; }
}
```

#### CharacterMovement3DComponent
Handles character movement with physics-based character controller.

```csharp
public class CharacterMovement3DComponent : PlayerMovementComponentBase
{
    public float MaxSpeed { get; set; }
    public float JumpHeight { get; set; }
    public float GroundFriction { get; set; }
}
```

### Animation Components

#### HumanoidComponent
Manages humanoid character animation and IK.

```csharp
public class HumanoidComponent : XRComponent, IRenderable
{
    public HumanoidSettings Settings { get; set; }
    public bool SolveIK { get; set; }
    
    // Body parts
    public BodySide Left { get; }
    public BodySide Right { get; }
}
```

#### VRIKSolverComponent
VR-specific inverse kinematics solver.

```csharp
public class VRIKSolverComponent : IKSolverComponent
{
    public IKSolverVR Solver { get; }
    public HumanoidComponent Humanoid { get; }
}
```

#### AnimStateMachineComponent
Manages animation state machines and blending.

```csharp
public class AnimStateMachineComponent : XRComponent
{
    public AnimStateMachine StateMachine { get; set; }
    public HumanoidComponent? Humanoid { get; set; }
}
```

### VR Components

#### VRPlayerCharacterComponent
VR-specific player character with IK and movement.

```csharp
public class VRPlayerCharacterComponent : XRComponent, IRenderable
{
    public bool IsCalibrating { get; set; }
    public Transform? Headset { get; set; }
    public Transform? LeftController { get; set; }
    public Transform? RightController { get; set; }
}
```

#### VRDeviceModelComponent
Renders VR device models (controllers, trackers).

```csharp
public abstract class VRDeviceModelComponent : ModelComponent
{
    protected abstract DeviceModel? GetRenderModel(VrDevice? device);
}
```

### Audio Components

#### AudioSourceComponent
Plays 3D spatial audio.

```csharp
public class AudioSourceComponent : XRComponent
{
    public AudioClip? Clip { get; set; }
    public float Volume { get; set; }
    public float Pitch { get; set; }
    public bool Loop { get; set; }
}
```

### Camera Components

#### CameraComponent
Represents a camera in the scene.

```csharp
public class CameraComponent : XRComponent
{
    public float FieldOfView { get; set; }
    public float NearClipPlane { get; set; }
    public float FarClipPlane { get; set; }
    public ECameraProjectionType ProjectionType { get; set; }
}
```

## Component Lifecycle

1. **Creation**: Component is instantiated
2. **Activation**: `OnComponentActivated()` is called when added to scene
3. **Tick Registration**: Component registers for tick groups
4. **Execution**: Component executes during tick phases
5. **Deactivation**: `OnComponentDeactivated()` is called when removed

## Best Practices

### Component Design
- Keep components focused on a single responsibility
- Use interfaces like `IRenderable` for shared functionality
- Register for appropriate tick groups to ensure correct execution order
- Clean up resources in `OnComponentDeactivated()`

### Performance
- Minimize allocations in tick methods
- Use object pooling for frequently created/destroyed components
- Register for specific tick orders to avoid unnecessary processing

### Dependencies
- Use `RequireComponents` attribute to ensure required components exist
- Use `OneComponentAllowed` attribute to prevent multiple instances
- Access sibling components through `GetSiblingComponent<T>()`

## Example: Creating a Custom Component

```csharp
public class CustomComponent : XRComponent
{
    private float _timer = 0f;
    
    protected internal override void OnComponentActivated()
    {
        base.OnComponentActivated();
        
        // Register for normal tick group
        RegisterTick(ETickGroup.Normal, ETickOrder.Scene, Update);
    }
    
    protected internal override void OnComponentDeactivated()
    {
        base.OnComponentDeactivated();
        
        // Clean up if needed
    }
    
    private void Update()
    {
        _timer += Engine.Delta;
        
        // Custom logic here
        if (_timer > 1.0f)
        {
            _timer = 0f;
            // Do something every second
        }
    }
}
```

## Related Documentation
- [Scene System](scene.md)
- [Rendering System](rendering.md)
- [Physics System](physics.md)
- [Animation System](animation.md) 