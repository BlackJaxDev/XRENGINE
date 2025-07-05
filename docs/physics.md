# Physics System

XRENGINE features a comprehensive physics system with support for multiple physics engines, GPU acceleration, and advanced simulation features optimized for VR and real-time applications.

## Overview

The physics system provides a unified interface for multiple physics engines, allowing developers to choose the best engine for their specific needs while maintaining consistent APIs.

## Supported Physics Engines

### PhysX (Fully Implemented)
- **Status**: ✅ Complete implementation
- **Features**: GPU acceleration, advanced collision detection, character controllers
- **Best for**: High-performance desktop and VR applications

### Jolt Physics (In Development)
- **Status**: 🚧 Partial implementation
- **Features**: High-performance physics simulation, deterministic results
- **Best for**: Games requiring precise physics simulation

### Jitter Physics (In Development)
- **Status**: 🚧 Partial implementation
- **Features**: Lightweight physics, mobile optimization
- **Best for**: Mobile and AR applications

## Architecture

### Physics Scene
The physics scene manages all physics objects and simulation:

```csharp
public abstract class AbstractPhysicsScene
{
    public abstract Vector3 Gravity { get; set; }
    public abstract void Initialize();
    public abstract void StepSimulation();
    public abstract void AddActor(IAbstractPhysicsActor actor);
    public abstract void RemoveActor(IAbstractPhysicsActor actor);
}
```

### Physics Actors
Physics objects in the scene:

```csharp
public interface IAbstractPhysicsActor
{
    void Destroy(bool wakeOnLostTouch = false);
}

public interface IAbstractRigidPhysicsActor : IAbstractPhysicsActor
{
    (Vector3 position, Quaternion rotation) Transform { get; }
    Vector3 LinearVelocity { get; }
    Vector3 AngularVelocity { get; }
    bool IsSleeping { get; }
}
```

## Physics Components

### DynamicRigidBodyComponent
Represents dynamic physics objects that can move and be affected by forces:

```csharp
public class DynamicRigidBodyComponent : XRComponent
{
    public float Mass { get; set; }
    public Vector3 LinearVelocity { get; set; }
    public Vector3 AngularVelocity { get; set; }
    public float LinearDamping { get; set; }
    public float AngularDamping { get; set; }
    public bool IsKinematic { get; set; }
}
```

### StaticRigidBodyComponent
Represents static physics objects that don't move:

```csharp
public class StaticRigidBodyComponent : XRComponent
{
    public IPhysicsGeometry Geometry { get; set; }
    public PhysxMaterial Material { get; set; }
}
```

### CharacterMovement3DComponent
Advanced character controller with physics-based movement:

```csharp
public class CharacterMovement3DComponent : PlayerMovementComponentBase
{
    public float MaxSpeed { get; set; }
    public float JumpHeight { get; set; }
    public float GroundFriction { get; set; }
    public float StepOffset { get; set; }
    public float SlopeLimit { get; set; }
    public bool ConstrainedClimbing { get; set; }
}
```

## Physics Geometry

### Supported Shapes
- **Sphere**: Simple spherical collision
- **Box**: Axis-aligned bounding box
- **Capsule**: Cylindrical shape with rounded ends
- **Convex Hull**: Complex convex shapes
- **Triangle Mesh**: Arbitrary mesh collision

### Geometry Interface
```csharp
public interface IPhysicsGeometry
{
    DataSource GetPhysxStruct();
    PxGeometry* AsPhysxPtr();
    Shape AsJoltShape();
}
```

### Creating Geometry
```csharp
// Sphere
var sphere = new IPhysicsGeometry.Sphere(radius: 1.0f);

// Box
var box = new IPhysicsGeometry.Box(halfExtents: new Vector3(1, 1, 1));

// Capsule
var capsule = new IPhysicsGeometry.Capsule(radius: 0.5f, halfHeight: 1.0f);
```

## Physics Materials

### Material Properties
```csharp
public class PhysxMaterial
{
    public float StaticFriction { get; set; }
    public float DynamicFriction { get; set; }
    public float Restitution { get; set; }
}
```

### Common Materials
- **Default**: Balanced friction and bounce
- **Ice**: Low friction, high slide
- **Rubber**: High friction, high bounce
- **Metal**: Medium friction, low bounce

## Collision Detection

### Raycasting
```csharp
// Single raycast
bool hit = scene.RaycastSingle(ray, layerMask, filter, out hitResult);

// Multiple raycasts
bool hit = scene.RaycastMultiple(ray, layerMask, filter, results);
```

### Sweep Tests
```csharp
// Sweep geometry along direction
bool hit = scene.SweepSingle(geometry, pose, direction, distance, layerMask, filter, results);
```

### Overlap Tests
```csharp
// Check for overlapping objects
bool overlap = scene.OverlapMultiple(geometry, pose, layerMask, filter, results);
```

## Character Controllers

### Capsule Controller
Advanced character controller with climbing, sliding, and collision detection:

```csharp
public class CapsuleController
{
    public float Radius { get; set; }
    public float Height { get; set; }
    public float StepOffset { get; set; }
    public float SlopeLimit { get; set; }
    public Vector3 Position { get; set; }
    
    public void Move(Vector3 displacement);
    public void Resize(float height);
}
```

### Controller Features
- **Step Climbing**: Automatic step detection and climbing
- **Slope Handling**: Sliding on steep slopes
- **Collision Response**: Proper collision handling
- **Height Adjustment**: Dynamic height changes (crouch/prone)

## Joints and Constraints

### Joint Types
- **Fixed Joint**: Rigid connection between objects
- **Hinge Joint**: Rotational constraint around axis
- **Prismatic Joint**: Linear movement along axis
- **Spherical Joint**: Ball and socket constraint
- **Distance Joint**: Maintains distance between objects

### Creating Joints
```csharp
public class PhysxJoint
{
    public abstract PxJoint* JointBase { get; }
    public float BreakForce { get; set; }
    public float BreakTorque { get; set; }
}
```

## GPU Physics

### Physics Chains
GPU-accelerated physics chains for cloth, hair, and soft body simulation:

```csharp
public class PhysicsChainComponent : XRComponent, IRenderable
{
    public Transform? Root { get; set; }
    public float Damping { get; set; }
    public float Elasticity { get; set; }
    public float Stiffness { get; set; }
    public float Inert { get; set; }
}
```

### Compute Shader Physics
- **PhysicsChain.comp**: GPU-accelerated chain physics
- **Particle System**: GPU particle simulation
- **Cloth Simulation**: Real-time cloth physics

## Physics Settings

### Scene Configuration
```json
{
  "PhysicsEngine": "PhysX",
  "Gravity": [0, -9.81, 0],
  "SubstepCount": 2,
  "EnableGPUPhysics": true,
  "MaxBodies": 1024,
  "MaxBodyPairs": 1024,
  "MaxContactConstraints": 1024
}
```

### Performance Settings
- **Substep Count**: Physics simulation frequency
- **GPU Acceleration**: Enable GPU physics simulation
- **Body Limits**: Maximum number of physics objects
- **Contact Limits**: Maximum contact points

## Collision Layers

### Layer System
```csharp
public enum ELayer
{
    Default = 0,
    Player = 1,
    Environment = 2,
    Triggers = 3,
    UI = 4
}
```

### Layer Masks
```csharp
// Create layer mask
var mask = LayerMask.FromLayer(ELayer.Player) | LayerMask.FromLayer(ELayer.Environment);

// Use in queries
bool hit = scene.RaycastSingle(ray, mask, filter, out result);
```

## Physics Debugging

### Debug Visualization
```csharp
public class PhysicsDebugVisualizer
{
    public bool ShowCollisionShapes { get; set; }
    public bool ShowContactPoints { get; set; }
    public bool ShowVelocities { get; set; }
    public bool ShowForces { get; set; }
}
```

### Debug Features
- **Collision Shape Display**: Visualize physics shapes
- **Contact Point Visualization**: Show contact points
- **Velocity Vectors**: Display object velocities
- **Force Visualization**: Show applied forces

## Performance Optimization

### Best Practices
- **Use Appropriate Shapes**: Simple shapes for better performance
- **Limit Dynamic Objects**: Minimize moving physics objects
- **Use Layers**: Organize objects with collision layers
- **GPU Physics**: Use GPU acceleration for complex simulations

### Profiling
- **Physics Time**: Monitor physics simulation time
- **Contact Count**: Track number of contact points
- **Body Count**: Monitor active physics objects
- **Memory Usage**: Track physics memory consumption

## Example: Creating a Physics Object

```csharp
// Create a dynamic physics object
var physicsObject = new SceneNode();
var rigidBody = physicsObject.AddComponent<DynamicRigidBodyComponent>();
var meshRenderer = physicsObject.AddComponent<MeshRenderer>();

// Configure physics properties
rigidBody.Mass = 10.0f;
rigidBody.LinearDamping = 0.1f;
rigidBody.AngularDamping = 0.1f;

// Add collision geometry
var collisionShape = new IPhysicsGeometry.Box(new Vector3(1, 1, 1));
rigidBody.Geometry = collisionShape;

// Add physics material
var material = new PhysxMaterial
{
    StaticFriction = 0.5f,
    DynamicFriction = 0.3f,
    Restitution = 0.2f
};
rigidBody.Material = material;
```

## Example: Character Controller

```csharp
// Create a character
var character = new SceneNode();
var movement = character.AddComponent<CharacterMovement3DComponent>();

// Configure movement
movement.MaxSpeed = 10.0f;
movement.JumpHeight = 2.0f;
movement.GroundFriction = 0.1f;
movement.StepOffset = 0.3f;
movement.SlopeLimit = 0.7f;

// Add character controller
var controller = movement.Controller;
controller.Radius = 0.5f;
controller.Height = 2.0f;
```

## Related Documentation
- [Component System](components.md)
- [Scene System](scene.md)
- [Rendering System](rendering.md)
- [Animation System](animation.md) 