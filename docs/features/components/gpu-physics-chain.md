# GPU Physics Chain Component

## Overview

The `GPUPhysicsChainComponent` is a high-performance, GPU-accelerated alternative to the CPU-based `PhysicsChainComponent`. It provides identical physics behavior while leveraging compute shaders for massive parallelization, making it ideal for scenes with many physics chains (hair, cloth, tails, chains, etc.).

## Features

- **100% Feature Parity** with CPU `PhysicsChainComponent`
- **Batched Dispatching** - all physics chains processed in a single GPU dispatch
- **Async Readback** - non-blocking GPU-to-CPU data transfer
- **All Update Modes** - Default, FixedUpdate, and Undilated
- **Full Collider Support** - Sphere, Capsule, Box, and Plane colliders
- **Distribution Curves** - Per-particle parameter variation along bone chains
- **Root Bone Tracking** - Character locomotion-relative physics for smooth movement
- **Velocity Smoothing** - Reduces jitter at high velocities

---

## Architecture

### Component Hierarchy

```
GPUPhysicsChainComponent (per-object)
         │
         ▼
GPUPhysicsChainDispatcher (singleton)
         │
         ▼
    Compute Shader (PhysicsChain.comp)
```

### Data Flow

```
┌─────────────────────────────────────────────────────────────────────┐
│                         Frame N                                      │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  1. PREPARE PHASE (LateUpdate)                                       │
│     ┌─────────────┐  ┌─────────────┐  ┌─────────────┐               │
│     │ Component A │  │ Component B │  │ Component C │               │
│     │  Prepare()  │  │  Prepare()  │  │  Prepare()  │               │
│     └──────┬──────┘  └──────┬──────┘  └──────┬──────┘               │
│            │                │                │                       │
│            └────────────────┼────────────────┘                       │
│                             ▼                                        │
│  2. SUBMIT PHASE                                                     │
│     ┌───────────────────────────────────────────────────────────┐   │
│     │              GPUPhysicsChainDispatcher                     │   │
│     │  - Collect particle data from all components               │   │
│     │  - Merge into combined GPU buffers                         │   │
│     │  - Adjust parent indices to global space                   │   │
│     └───────────────────────────────────────────────────────────┘   │
│                             │                                        │
│                             ▼                                        │
│  3. DISPATCH PHASE (GlobalPreRender)                                │
│     ┌───────────────────────────────────────────────────────────┐   │
│     │              Single Compute Dispatch                       │   │
│     │  - All particles processed in parallel                     │   │
│     │  - Memory barriers between iterations                      │   │
│     │  - Up to 3 iterations per frame (UpdateRate)               │   │
│     └───────────────────────────────────────────────────────────┘   │
│                             │                                        │
│                             ▼                                        │
│  4. FENCE PHASE                                                      │
│     ┌───────────────────────────────────────────────────────────┐   │
│     │              GPU Fence Sync Created                        │   │
│     │  - Non-blocking fence for async readback                   │   │
│     └───────────────────────────────────────────────────────────┘   │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────┐
│                         Frame N+1                                    │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  5. READBACK PHASE (GlobalPreRender, before new dispatch)           │
│     ┌───────────────────────────────────────────────────────────┐   │
│     │              Check Fence (non-blocking)                    │   │
│     │  - If signaled: read particle positions from GPU           │   │
│     │  - Distribute to components via ApplyReadbackData()        │   │
│     └───────────────────────────────────────────────────────────┘   │
│                             │                                        │
│            ┌────────────────┼────────────────┐                       │
│            ▼                ▼                ▼                       │
│     ┌─────────────┐  ┌─────────────┐  ┌─────────────┐               │
│     │ Component A │  │ Component B │  │ Component C │               │
│     │   Apply     │  │   Apply     │  │   Apply     │               │
│     │  Transforms │  │  Transforms │  │  Transforms │               │
│     └─────────────┘  └─────────────┘  └─────────────┘               │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

---

## GPU Data Structures

### Particle Data (96 bytes, padded for GPU alignment)

```glsl
struct Particle {
    vec3 Position;           // Current world position
    float _pad0;
    vec3 PrevPosition;       // Previous frame position (Verlet)
    float _pad1;
    vec3 TransformPosition;  // Bone transform world position
    float _pad2;
    vec3 TransformLocalPosition;  // Local position relative to parent
    float _pad3;
    int ParentIndex;         // Index of parent particle (-1 for root)
    float Damping;           // Velocity damping [0-1]
    float Elasticity;        // Pull toward rest position [0-1]
    float Stiffness;         // Shape preservation [0-1]
    float Inert;             // Response to parent movement [0-1]
    float Friction;          // Collision friction [0-1]
    float Radius;            // Collision radius
    float BoneLength;        // Cumulative bone length
    int IsColliding;         // Collision flag
    int _pad4, _pad5, _pad6;
};
```

### Particle Tree Data (112 bytes)

```glsl
struct ParticleTreeData {
    vec3 LocalGravity;       // Gravity in local space
    float _pad0;
    vec3 RestGravity;        // Rest pose gravity direction
    float _pad1;
    int ParticleStart;       // Start index in global particle array
    int ParticleCount;       // Number of particles in this tree
    float _pad2, _pad3;
    mat4 RootWorldToLocal;   // Root bone inverse world matrix
    float BoneTotalLength;   // Total length of bone chain
    int _pad4, _pad5, _pad6;
};
```

### Collider Data (48 bytes)

```glsl
struct ColliderData {
    vec4 Center;   // xyz: position, w: radius (sphere/capsule)
    vec4 Params;   // Type-specific parameters
    int Type;      // 0: Sphere, 1: Capsule, 2: Box, 3: Plane
    int _pad0, _pad1, _pad2;
};
```

| Type | Center | Params |
|------|--------|--------|
| Sphere | xyz: position, w: radius | unused |
| Capsule | xyz: start, w: radius | xyz: end |
| Box | xyz: center | xyz: half-extents |
| Plane | xyz: point on plane | xyz: normal, w: bound (0=outside, 1=inside) |

---

## TODO (Data Layout Optimizations)

- **Pack flags/indices**: Combine `ParentIndex` + `IsColliding` (and other flags) into a single `uint`.
- **Half precision scalars**: Store per-particle scalars (damping/elasticity/stiffness/inert/friction/radius/bone length) as packed `half` values.
- **Remove redundant positions**: Drop `TransformPosition` if it can be derived from `TransformLocalPosition` and matrices.
- **SoA layout**: Split particle data into multiple SSBOs (positions, prev, params) for better cache/coalescing.
- **Tree data trimming**: Consider removing `RootWorldToLocal` and deriving it on GPU if possible.
- **Collider compaction**: Use per-type collider buffers to avoid unused padding/params.

## Physics Algorithm

The GPU compute shader implements the same physics algorithm as the CPU version:

### 1. Verlet Integration

```glsl
vec3 velocity = position - prevPosition;
vec3 inertiaMove = objectMove * inert;
prevPosition = position + inertiaMove;

float damping = particle.Damping;
if (isColliding) {
    damping += particle.Friction;
    damping = min(damping, 1.0);
}

position += velocity * (1.0 - damping) + force + inertiaMove;
```

### 2. Force Calculation

```glsl
vec3 force = Gravity;
vec3 gravityDir = normalize(Gravity);
vec3 projectedGravity = gravityDir * max(dot(restGravity, gravityDir), 0.0);
force -= projectedGravity;
force = (force + additionalForce) * (objectScale * deltaTime);
```

### 3. Constraint Solving

#### Elasticity Constraint
```glsl
vec3 restPos = (parentMatrix * vec4(localPosition, 1.0)).xyz;
position += (restPos - position) * elasticity * deltaTime;
```

#### Stiffness Constraint
```glsl
float stiffness = mix(1.0, particle.Stiffness, weight);
vec3 d = restPos - position;
float len = length(d);
float maxLen = restLength * (1.0 - stiffness) * 2.0;
if (len > maxLen) {
    position += d * ((len - maxLen) / len);
}
```

#### Length Constraint
```glsl
vec3 diff = parentPosition - position;
float L = length(diff);
if (L > epsilon) {
    position += diff * ((L - restLength) / L);
}
```

#### Freeze Axis Constraint
```glsl
vec3 normal = parentMatrix[axisIndex].xyz;
float d = dot(position - parentPosition, normal);
position -= normal * d;
```

### 4. Collision Detection

```glsl
// Sphere
vec3 d = pos - center;
float dist = length(d);
if (dist < radius + particleRadius) {
    pos = center + d * ((radius + particleRadius) / dist);
}

// Capsule - project to line segment, then sphere collision

// Box - clamp to AABB, then sphere collision with closest point

// Plane
float d = dot(pos - planePoint, planeNormal);
if ((bound == Outside && d < 0) || (bound == Inside && d > 0)) {
    pos -= planeNormal * d;
}
```

---

## Update Modes

| Mode | Delta Time | Behavior |
|------|------------|----------|
| **Default** | `Time.deltaTime` | Single update per frame, optionally scaled by UpdateRate |
| **FixedUpdate** | `Time.fixedDeltaTime` | Multiple iterations to match physics tick rate |
| **Undilated** | `Time.unscaledDeltaTime` | Ignores time scale, supports UpdateRate iterations |

### Iteration Capping

To prevent "spiral of death" when frame rate drops:
- Maximum 3 physics iterations per frame
- Excess accumulated time is discarded

---

## Batched Dispatching

### GPUPhysicsChainDispatcher

The singleton `GPUPhysicsChainDispatcher` manages all GPU physics chains:

```csharp
public sealed class GPUPhysicsChainDispatcher
{
    public static GPUPhysicsChainDispatcher Instance { get; }
    
    // Registration
    public void Register(GPUPhysicsChainComponent component);
    public void Unregister(GPUPhysicsChainComponent component);
    
    // Main processing (called from VisualScene3D.GlobalPreRender)
    public void ProcessDispatches();
    
    // Statistics
    public int RegisteredComponentCount { get; }
    public int TotalParticleCount { get; }
    public int TotalColliderCount { get; }
}
```

### Buffer Merging

When multiple components are registered, their data is merged:

```
Component A: particles[0..99],   trees[0..2],   colliders[0..5]
Component B: particles[0..49],   trees[0..1],   colliders[0..3]
Component C: particles[0..199],  trees[0..4],   colliders[0..2]
                    │                  │              │
                    ▼                  ▼              ▼
Combined:    particles[0..349], trees[0..7],  colliders[0..10]
             (indices adjusted)
```

Parent indices are adjusted to global space:
- Component A: parentIndex unchanged
- Component B: parentIndex += 100
- Component C: parentIndex += 150

---

## Usage

### Basic Setup

```csharp
// Add component to a GameObject with a bone hierarchy
var gpuPhysics = gameObject.AddComponent<GPUPhysicsChainComponent>();

// Set the root bone(s)
gpuPhysics.Root = rootBoneTransform;
// Or multiple roots:
gpuPhysics.Roots = new List<Transform> { root1, root2, root3 };

// Configure physics parameters
gpuPhysics.Damping = 0.1f;
gpuPhysics.Elasticity = 0.1f;
gpuPhysics.Stiffness = 0.1f;
gpuPhysics.Inert = 0.0f;
gpuPhysics.Friction = 0.5f;
gpuPhysics.Radius = 0.02f;
gpuPhysics.Gravity = new Vector3(0, -9.8f, 0);
```

### Colliders

```csharp
// Sphere collider
var sphere = headBone.AddComponent<PhysicsChainSphereCollider>();
sphere.Radius = 0.1f;

// Capsule collider
var capsule = bodyBone.AddComponent<PhysicsChainCapsuleCollider>();
capsule.Radius = 0.05f;
capsule.Height = 0.3f;

// Box collider
var box = chestBone.AddComponent<PhysicsChainBoxCollider>();
box.Size = new Vector3(0.2f, 0.3f, 0.1f);

// Plane collider (ground)
var plane = groundObject.AddComponent<PhysicsChainPlaneCollider>();
plane._direction = Direction.Y;
plane._bound = EBound.Outside;

// Assign colliders to component
gpuPhysics.Colliders = new List<PhysicsChainColliderBase> 
{ 
    sphere, capsule, box, plane 
};
```

### Distribution Curves

```csharp
// Vary damping along the bone chain (0 = root, 1 = tip)
gpuPhysics.DampingDistrib = new AnimationCurve();
gpuPhysics.DampingDistrib.AddKeyframe(0.0f, 0.5f);  // Root: 50% damping
gpuPhysics.DampingDistrib.AddKeyframe(1.0f, 0.1f);  // Tip: 10% damping

// Similar for other parameters:
gpuPhysics.ElasticityDistrib = ...;
gpuPhysics.StiffnessDistrib = ...;
gpuPhysics.InertDistrib = ...;
gpuPhysics.FrictionDistrib = ...;
gpuPhysics.RadiusDistrib = ...;
```

### Batched vs Standalone Mode

```csharp
// Batched mode (default) - best performance with multiple chains
gpuPhysics.UseBatchedDispatcher = true;

// Standalone mode - component manages its own GPU resources
gpuPhysics.UseBatchedDispatcher = false;
```

### Character Locomotion (Root Bone Tracking)

When physics chains are attached to a character controlled by a character controller (e.g., player locomotion), the chains can lag behind during rapid movement because they perceive the character's movement as external force. The `RootBone` and `RootInertia` properties solve this:

```csharp
// Set the character's locomotion root (typically pelvis or hips bone)
gpuPhysics.RootBone = characterPelvisBone;

// Control how much root movement affects physics
// 0 = World space (default) - chains react to all movement
// 1 = Fully relative - chains move "with" the root bone
gpuPhysics.RootInertia = 0.8f;  // 80% relative to root bone
```

**Use Cases:**
- **Teleportation** - Prevent chains from stretching across the teleport distance
- **Dashing/Rolling** - Keep hair/clothing from extreme lag during fast movement
- **Vehicle Entry/Exit** - Smooth transition when character position changes abruptly
- **Animation Root Motion** - Chains follow the animated root properly

### Velocity Smoothing (Anti-Jitter)

At very high velocities (fast character movement, rapid camera motion), physics chains can experience violent jittering. The `VelocitySmoothing` property applies an exponential moving average to the perceived velocity:

```csharp
// Smooth velocity to reduce jitter
// 0 = No smoothing (raw velocity)
// 1 = Maximum smoothing (very dampened response)
gpuPhysics.VelocitySmoothing = 0.3f;  // Moderate smoothing
```

**How It Works:**
```
smoothedVelocity = lerp(smoothedVelocity, rawVelocity, 1 - smoothing * 0.9)
```

**Recommended Values:**
| Scenario | VelocitySmoothing |
|----------|-------------------|
| Slow/static objects | 0.0 |
| Normal gameplay | 0.1 - 0.3 |
| Fast-paced action | 0.3 - 0.5 |
| VR (reduce motion sickness) | 0.4 - 0.6 |

---

## Performance Characteristics

### Dispatch Efficiency

| Scenario | Standalone Mode | Batched Mode |
|----------|-----------------|--------------|
| 1 physics chain | 1 dispatch | 1 dispatch |
| 10 physics chains | 10 dispatches | **1 dispatch** |
| 100 physics chains | 100 dispatches | **1 dispatch** |
| 1000 physics chains | 1000 dispatches | **1 dispatch** |

### Memory Usage

| Buffer | Size per Element | Typical Usage |
|--------|------------------|---------------|
| Particles | 96 bytes | ~10KB per 100 particles |
| Trees | 112 bytes | ~1KB per 10 trees |
| Transforms | 64 bytes | ~6KB per 100 particles |
| Colliders | 48 bytes | ~500B per 10 colliders |

### Latency

- **One frame latency** due to async readback
- GPU fence sync is non-blocking
- Results from frame N are applied in frame N+1

---

## Comparison: CPU vs GPU

| Feature | CPU (PhysicsChainComponent) | GPU (GPUPhysicsChainComponent) |
|---------|----------------------------|--------------------------------|
| Physics Algorithm | ✅ Identical | ✅ Identical |
| Collider Support | ✅ All types | ✅ All types |
| Distribution Curves | ✅ Full support | ✅ Full support |
| Update Modes | ✅ All modes | ✅ All modes |
| Root Bone Tracking | ✅ Full support | ✅ Full support |
| Velocity Smoothing | ✅ Full support | ✅ Full support |
| Multithreading | ✅ Job system | ✅ GPU parallelism |
| Batching | ❌ Per-component | ✅ Single dispatch |
| Latency | None | 1 frame |
| Best For | Few chains, low latency | Many chains, high throughput |

---

## Files

| File | Description |
|------|-------------|
| `XRENGINE/Scene/Components/Physics/GPUPhysicsChainComponent.cs` | Main component |
| `XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.cs` | Batched dispatcher |
| `Build/CommonAssets/Shaders/Compute/PhysicsChain.comp` | Main physics shader |
| `Build/CommonAssets/Shaders/Compute/PhysicsChain/SkipUpdateParticles.comp` | Skip-update shader |

---

## See Also

- [PhysicsChainComponent](../../../XRENGINE/Scene/Components/Physics/PhysicsChainComponent.cs) - CPU version
- [BvhRaycastDispatcher](../../../XRENGINE/Rendering/Compute/BvhRaycastDispatcher.cs) - Similar batched dispatch pattern
