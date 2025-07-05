# Physics API Reference

XRENGINE's physics API provides a unified interface for multiple physics engines with support for rigid body dynamics, character controllers, and GPU acceleration.

## Core Physics Classes

### AbstractPhysicsScene
Base class for physics scene management.

```csharp
public abstract class AbstractPhysicsScene : XRBase
{
    public abstract Vector3 Gravity { get; set; }
    public abstract void Initialize();
    public abstract void StepSimulation(float deltaTime);
    public abstract void AddActor(IAbstractPhysicsActor actor);
    public abstract void RemoveActor(IAbstractPhysicsActor actor);
    
    public abstract bool RaycastSingle(Ray ray, LayerMask layerMask, out HitResult result);
    public abstract bool RaycastMultiple(Ray ray, LayerMask layerMask, List<HitResult> results);
    public abstract bool SweepSingle(IPhysicsGeometry geometry, Pose pose, Vector3 direction, float distance, LayerMask layerMask, out HitResult result);
    public abstract bool OverlapMultiple(IPhysicsGeometry geometry, Pose pose, LayerMask layerMask, List<OverlapResult> results);
}
```

### PhysicsScene
Concrete implementation of physics scene.

```csharp
public class PhysicsScene : AbstractPhysicsScene
{
    public PhysXScene? PhysXScene { get; }
    public JoltPhysicsWorld? JoltWorld { get; }
    public JitterWorld? JitterWorld { get; }
    
    public override void Initialize();
    public override void StepSimulation(float deltaTime);
    public override void AddActor(IAbstractPhysicsActor actor);
    public override void RemoveActor(IAbstractPhysicsActor actor);
}
```

## Physics Actors

### IAbstractPhysicsActor
Base interface for all physics objects.

```csharp
public interface IAbstractPhysicsActor
{
    void Destroy(bool wakeOnLostTouch = false);
    bool IsActive { get; }
    LayerMask CollisionLayer { get; set; }
}
```

### IAbstractRigidPhysicsActor
Interface for rigid body physics objects.

```csharp
public interface IAbstractRigidPhysicsActor : IAbstractPhysicsActor
{
    (Vector3 position, Quaternion rotation) Transform { get; }
    Vector3 LinearVelocity { get; set; }
    Vector3 AngularVelocity { get; set; }
    float Mass { get; set; }
    bool IsSleeping { get; }
    bool IsKinematic { get; set; }
    
    void AddForce(Vector3 force, EForceMode mode = EForceMode.Force);
    void AddTorque(Vector3 torque, EForceMode mode = EForceMode.Force);
    void SetLinearVelocity(Vector3 velocity);
    void SetAngularVelocity(Vector3 velocity);
}
```

### PhysXRigidActor
PhysX-specific rigid body implementation.

```csharp
public class PhysXRigidActor : IAbstractRigidPhysicsActor
{
    public PxRigidActor* Actor { get; }
    public PhysxMaterial? Material { get; set; }
    public IPhysicsGeometry? Geometry { get; set; }
    
    public void SetGeometry(IPhysicsGeometry geometry);
    public void SetMaterial(PhysxMaterial material);
    public void SetCollisionLayer(LayerMask layer);
}
```

## Physics Geometry

### IPhysicsGeometry
Interface for collision shapes.

```csharp
public interface IPhysicsGeometry
{
    DataSource GetPhysxStruct();
    PxGeometry* AsPhysxPtr();
    Shape AsJoltShape();
    JitterShape AsJitterShape();
}
```

### Geometry Types

#### Sphere
```csharp
public class Sphere : IPhysicsGeometry
{
    public float Radius { get; set; }
    
    public Sphere(float radius);
}
```

#### Box
```csharp
public class Box : IPhysicsGeometry
{
    public Vector3 HalfExtents { get; set; }
    
    public Box(Vector3 halfExtents);
    public Box(float halfExtent);
}
```

#### Capsule
```csharp
public class Capsule : IPhysicsGeometry
{
    public float Radius { get; set; }
    public float HalfHeight { get; set; }
    
    public Capsule(float radius, float halfHeight);
}
```

#### ConvexHull
```csharp
public class ConvexHull : IPhysicsGeometry
{
    public List<Vector3> Vertices { get; }
    public List<int> Indices { get; }
    
    public ConvexHull(List<Vector3> vertices, List<int> indices);
}
```

#### TriangleMesh
```csharp
public class TriangleMesh : IPhysicsGeometry
{
    public List<Vector3> Vertices { get; }
    public List<int> Indices { get; }
    
    public TriangleMesh(List<Vector3> vertices, List<int> indices);
}
```

## Physics Materials

### PhysxMaterial
PhysX material properties.

```csharp
public class PhysxMaterial : XRBase
{
    public float StaticFriction { get; set; }
    public float DynamicFriction { get; set; }
    public float Restitution { get; set; }
    
    public PhysxMaterial(float staticFriction = 0.5f, float dynamicFriction = 0.5f, float restitution = 0.0f);
}
```

### Common Materials
```csharp
public static class PhysicsMaterials
{
    public static PhysxMaterial Default { get; } = new PhysxMaterial(0.5f, 0.5f, 0.0f);
    public static PhysxMaterial Ice { get; } = new PhysxMaterial(0.02f, 0.02f, 0.1f);
    public static PhysxMaterial Rubber { get; } = new PhysxMaterial(0.8f, 0.8f, 0.8f);
    public static PhysxMaterial Metal { get; } = new PhysxMaterial(0.6f, 0.4f, 0.1f);
}
```

## Character Controllers

### CapsuleController
Advanced character controller with climbing and sliding.

```csharp
public class CapsuleController : XRBase
{
    public float Radius { get; set; }
    public float Height { get; set; }
    public float StepOffset { get; set; }
    public float SlopeLimit { get; set; }
    public Vector3 Position { get; set; }
    public Vector3 Velocity { get; set; }
    
    public void Move(Vector3 displacement);
    public void Resize(float height);
    public bool IsGrounded();
    public Vector3 GetGroundNormal();
}
```

### CharacterMovement3DComponent
Component-based character movement.

```csharp
public class CharacterMovement3DComponent : PlayerMovementComponentBase
{
    public float MaxSpeed { get; set; }
    public float JumpHeight { get; set; }
    public float GroundFriction { get; set; }
    public float StepOffset { get; set; }
    public float SlopeLimit { get; set; }
    public bool ConstrainedClimbing { get; set; }
    
    public CapsuleController Controller { get; }
    
    protected override void UpdateMovement();
    protected override void UpdateJump();
}
```

## Joints and Constraints

### PhysxJoint
Base class for PhysX joints.

```csharp
public class PhysxJoint : XRBase
{
    public abstract PxJoint* JointBase { get; }
    public float BreakForce { get; set; }
    public float BreakTorque { get; set; }
    
    public void SetBreakForce(float force);
    public void SetBreakTorque(float torque);
    public bool IsBroken();
}
```

### Joint Types

#### FixedJoint
```csharp
public class FixedJoint : PhysxJoint
{
    public FixedJoint(IAbstractRigidPhysicsActor actor0, IAbstractRigidPhysicsActor actor1, Transform localPose0, Transform localPose1);
    
    public override PxJoint* JointBase { get; }
}
```

#### HingeJoint
```csharp
public class HingeJoint : PhysxJoint
{
    public float Angle { get; }
    public float Velocity { get; set; }
    public float LowerLimit { get; set; }
    public float UpperLimit { get; set; }
    
    public HingeJoint(IAbstractRigidPhysicsActor actor0, IAbstractRigidPhysicsActor actor1, Transform localPose0, Transform localPose1, Vector3 axis);
    
    public override PxJoint* JointBase { get; }
}
```

#### PrismaticJoint
```csharp
public class PrismaticJoint : PhysxJoint
{
    public float Position { get; }
    public float Velocity { get; set; }
    public float LowerLimit { get; set; }
    public float UpperLimit { get; set; }
    
    public PrismaticJoint(IAbstractRigidPhysicsActor actor0, IAbstractRigidPhysicsActor actor1, Transform localPose0, Transform localPose1, Vector3 axis);
    
    public override PxJoint* JointBase { get; }
}
```

#### SphericalJoint
```csharp
public class SphericalJoint : PhysxJoint
{
    public Quaternion Rotation { get; }
    public Vector3 AngularVelocity { get; set; }
    public float ConeLimit { get; set; }
    
    public SphericalJoint(IAbstractRigidPhysicsActor actor0, IAbstractRigidPhysicsActor actor1, Transform localPose0, Transform localPose1);
    
    public override PxJoint* JointBase { get; }
}
```

## Collision Detection

### Raycasting
```csharp
public class PhysicsScene
{
    public bool RaycastSingle(Ray ray, LayerMask layerMask, out HitResult result);
    public bool RaycastMultiple(Ray ray, LayerMask layerMask, List<HitResult> results);
    public bool RaycastAll(Ray ray, LayerMask layerMask, List<HitResult> results);
}
```

### HitResult
```csharp
public struct HitResult
{
    public Vector3 Point { get; set; }
    public Vector3 Normal { get; set; }
    public float Distance { get; set; }
    public IAbstractPhysicsActor? Actor { get; set; }
    public int FaceIndex { get; set; }
}
```

### Sweep Tests
```csharp
public class PhysicsScene
{
    public bool SweepSingle(IPhysicsGeometry geometry, Pose pose, Vector3 direction, float distance, LayerMask layerMask, out HitResult result);
    public bool SweepMultiple(IPhysicsGeometry geometry, Pose pose, Vector3 direction, float distance, LayerMask layerMask, List<HitResult> results);
}
```

### Overlap Tests
```csharp
public class PhysicsScene
{
    public bool OverlapSingle(IPhysicsGeometry geometry, Pose pose, LayerMask layerMask, out OverlapResult result);
    public bool OverlapMultiple(IPhysicsGeometry geometry, Pose pose, LayerMask layerMask, List<OverlapResult> results);
}
```

### OverlapResult
```csharp
public struct OverlapResult
{
    public IAbstractPhysicsActor? Actor { get; set; }
    public int FaceIndex { get; set; }
}
```

## Collision Layers

### LayerMask
```csharp
public struct LayerMask
{
    public uint Mask { get; set; }
    
    public static LayerMask FromLayer(ELayer layer);
    public static LayerMask operator |(LayerMask a, LayerMask b);
    public static LayerMask operator &(LayerMask a, LayerMask b);
    public static LayerMask operator ~(LayerMask a);
}
```

### Layer Enum
```csharp
public enum ELayer
{
    Default = 0,
    Player = 1,
    Environment = 2,
    Triggers = 3,
    UI = 4,
    Water = 5,
    Debris = 6
}
```

## GPU Physics

### PhysicsChainComponent
GPU-accelerated physics chains for cloth and hair.

```csharp
public class PhysicsChainComponent : XRComponent, IRenderable
{
    public Transform? Root { get; set; }
    public float Damping { get; set; }
    public float Elasticity { get; set; }
    public float Stiffness { get; set; }
    public float Inert { get; set; }
    
    public List<ChainLink> Links { get; }
    
    protected override void UpdatePhysics();
}
```

### ChainLink
```csharp
public struct ChainLink
{
    public Vector3 Position { get; set; }
    public Vector3 Velocity { get; set; }
    public Vector3 Force { get; set; }
    public float Mass { get; set; }
}
```

## Physics Components

### DynamicRigidBodyComponent
Component for dynamic physics objects.

```csharp
public class DynamicRigidBodyComponent : XRComponent
{
    public float Mass { get; set; }
    public Vector3 LinearVelocity { get; set; }
    public Vector3 AngularVelocity { get; set; }
    public float LinearDamping { get; set; }
    public float AngularDamping { get; set; }
    public bool IsKinematic { get; set; }
    
    public IPhysicsGeometry? Geometry { get; set; }
    public PhysxMaterial? Material { get; set; }
    
    public IAbstractRigidPhysicsActor? RigidBody { get; }
    
    protected internal override void OnComponentActivated();
    protected internal override void OnComponentDeactivated();
}
```

### StaticRigidBodyComponent
Component for static physics objects.

```csharp
public class StaticRigidBodyComponent : XRComponent
{
    public IPhysicsGeometry? Geometry { get; set; }
    public PhysxMaterial? Material { get; set; }
    
    public IAbstractPhysicsActor? StaticBody { get; }
    
    protected internal override void OnComponentActivated();
    protected internal override void OnComponentDeactivated();
}
```

## Physics Debugging

### PhysicsDebugVisualizer
```csharp
public class PhysicsDebugVisualizer : XRComponent
{
    public bool ShowCollisionShapes { get; set; }
    public bool ShowContactPoints { get; set; }
    public bool ShowVelocities { get; set; }
    public bool ShowForces { get; set; }
    public bool ShowJointLimits { get; set; }
    
    protected internal override void OnComponentActivated();
    protected internal override void OnComponentDeactivated();
}
```

### Physics Profiler
```csharp
public class PhysicsProfiler
{
    public float SimulationTime { get; }
    public int ActiveBodies { get; }
    public int ContactCount { get; }
    public float MemoryUsage { get; }
    
    public void BeginFrame();
    public void EndFrame();
    public void Reset();
}
```

## Example: Creating a Physics Object

```csharp
// Create a dynamic physics object
var physicsObject = new SceneNode();
var rigidBody = physicsObject.AddComponent<DynamicRigidBodyComponent>();

// Configure physics properties
rigidBody.Mass = 10.0f;
rigidBody.LinearDamping = 0.1f;
rigidBody.AngularDamping = 0.1f;

// Add collision geometry
var collisionShape = new Box(new Vector3(1, 1, 1));
rigidBody.Geometry = collisionShape;

// Add physics material
var material = new PhysxMaterial
{
    StaticFriction = 0.5f,
    DynamicFriction = 0.3f,
    Restitution = 0.2f
};
rigidBody.Material = material;

// Add force
rigidBody.RigidBody?.AddForce(new Vector3(0, 100, 0), EForceMode.Impulse);
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

// Move character
controller.Move(new Vector3(1, 0, 0) * Engine.Time.Delta);
```

## Example: Physics Queries

```csharp
public class PhysicsQuerySystem : XRComponent
{
    protected internal override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RegisterTick(ETickGroup.Normal, ETickOrder.Physics, UpdateQueries);
    }
    
    private void UpdateQueries()
    {
        var camera = Camera.Main;
        var ray = camera.ScreenPointToRay(Engine.Input.MousePosition);
        
        // Raycast
        if (Engine.PhysicsScene.RaycastSingle(ray, LayerMask.FromLayer(ELayer.Environment), out var hit))
        {
            Debug.Out($"Hit point: {hit.Point}, Distance: {hit.Distance}");
        }
        
        // Sweep test
        var sphere = new Sphere(1.0f);
        var pose = new Pose(camera.Position, Quaternion.Identity);
        
        if (Engine.PhysicsScene.SweepSingle(sphere, pose, camera.Forward, 10.0f, LayerMask.FromLayer(ELayer.Environment), out var sweepHit))
        {
            Debug.Out($"Sweep hit at: {sweepHit.Point}");
        }
    }
}
```

## Example: Joints

```csharp
// Create two rigid bodies
var body1 = new SceneNode();
var rigidBody1 = body1.AddComponent<DynamicRigidBodyComponent>();
rigidBody1.Geometry = new Box(new Vector3(1, 1, 1));

var body2 = new SceneNode();
var rigidBody2 = body2.AddComponent<DynamicRigidBodyComponent>();
rigidBody2.Geometry = new Sphere(0.5f);

// Create a hinge joint
var joint = new HingeJoint(
    rigidBody1.RigidBody!,
    rigidBody2.RigidBody!,
    Transform.Identity,
    Transform.Identity,
    Vector3.Up
);

joint.LowerLimit = -90.0f;
joint.UpperLimit = 90.0f;
joint.BreakForce = 1000.0f;

// Add joint to physics scene
Engine.PhysicsScene.AddJoint(joint);
```

## Configuration

### Physics Settings
```json
{
  "Physics": {
    "Engine": "PhysX",
    "Gravity": [0, -9.81, 0],
    "SubstepCount": 2,
    "EnableGPUPhysics": true,
    "MaxBodies": 1024,
    "MaxBodyPairs": 1024,
    "MaxContactConstraints": 1024,
    "EnableDebugVisualization": false
  }
}
```

## Related Documentation
- [Component System](../components.md)
- [Scene System](../scene.md)
- [Rendering System](../rendering.md)
- [Animation System](../animation.md)
- [VR Development](../vr-development.md) 