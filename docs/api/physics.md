# Physics Architecture

XREngine wraps multiple physics backends behind a single scene interface so gameplay, tools, and runtime code do not need to care which solver is active. The integration is centered around `AbstractPhysicsScene`, a host-side class that the world owns and ticks every fixed update. Concrete scenes translate high-level requests (actor management, queries, character control) into calls to the selected middleware while keeping compatible data structures such as `Segment`, `LayerMask`, `RaycastHit`, `SweepHit`, and `OverlapHit`.

Most gameplay features ship against the PhysX backend (`XREngine.Rendering.Physics.Physx` namespace). Jolt and Jitter2 scenes exist as experimental alternatives; they share the same surface API but are still catching up on feature coverage.

---

## Scene Lifecycle
- `AbstractPhysicsScene.Initialize()` and `Destroy()` are called by the world when a scene instance is created or torn down. Implementations allocate backend resources here (PhysX creates a foundation/physics instance, Jolt spins up job systems, etc.).
- `StepSimulation()` is always driven from the engine’s fixed timestep. PhysX uses `Engine.Time.Timer.FixedUpdateDelta`, injects pending controller moves, calls `PxScene::simulate`, then fetches results and pushes transforms back to `RigidBodyTransform` owners. The scene raises `OnSimulationStep` after every successful fetch so listeners can post-process collisions or telemetry.
- `AddActor`, `RemoveActor`, and `NotifyShapeChanged` let components attach or detach backend objects without knowing which solver is in use. PhysX scenes map raw pointers back to managed wrappers through global dictionaries so ownership can be tracked across callbacks.

Every query variant in `AbstractPhysicsScene` works with engine concepts:
- `Segment` (start/end) replaces raw ray parameters.
- `LayerMask` is converted to backend filter data (PhysX uses `PxFilterData`, Jolt uses object layers). The helper types live in `XREngine.Scene`.
- Results return owning `XRComponent` references alongside backend-specific hit payloads so higher-level systems can resolve game objects quickly.

---

## PhysX Backend
PhysX 5 is the primary, fully-featured integration. It lives in `XREngine.Rendering.Physics.Physx` and exposes the entire PxScene API surface area the engine relies on.

### Initialization & Lifetime
- `PhysxScene.Init()` is invoked once (via the static ctor) to construct a global `PxFoundation` and `PxPhysics` instance. `Release()` tears them down when the runtime exits.
- Each `PhysxScene` allocates `PxScene` objects with GPU dynamics enabled by default (`PxSceneFlags.EnableGpuDynamics`, `PxSceneFlags.EnableActiveActors`, GPU broadphase). Custom dispatcher, filter shader, and simulation callbacks are wired up during `Initialize()`.
- Instances register themselves in `PhysxScene.Scenes`. This allows static helper code (controllers, debug renderers, wrapper classes) to look up their parent scene by raw pointer.

### Simulation Step
`StepSimulation()` performs the full PhysX pipeline:
1. Consume buffered character-controller moves (`ControllerManager.Controllers`) before stepping.
2. Call `Simulate()` with the engine’s fixed delta, optionally reusing a scratch memory block when GPU features request it.
3. `FetchResults()` followed by optional debug buffer extraction (`PxRenderBuffer`) if visualization is enabled.
4. Iterate over `GetActiveActorsMut` to synchronize every active rigid body back to the owning `RigidBodyTransform`. This invokes `RigidBodyTransform.OnPhysicsStepped()` on both dynamic and static actors so components can pull updated poses.
5. Fire `NotifySimulationStepped()` for listeners that registered via `OnSimulationStep`.

### Actor, Shape, and Material Wrappers
- `PhysxActor`, `PhysxRigidActor`, `PhysxRigidBody`, `PhysxDynamicRigidBody`, and `PhysxStaticRigidBody` wrap the corresponding Px types. They cache themselves inside `AllActors`, `AllRigidActors`, etc., keyed by unmanaged pointers, which lets callback code recover managed objects quickly.
- `PhysxShape` wraps `PxShape` objects, stores simulation/query flags, filter data, and offers convenience helpers (`ExtRaycast`, `ExtSweep`, `ExtGetWorldBounds`). Shapes can be created directly from an `IPhysicsGeometry` and material bundle.
- `PhysxMaterial` inherits `AbstractPhysicsMaterial` and exposes PhysX 5 material flags (disable friction, improved patch friction, compliant contact) and combine modes. All materials live in a static registry for pointer resolution.

### Geometry Interop
`IPhysicsGeometry` (in `Physx/Geometry/IPhysicsGeometry.cs`) provides value-type shapes that work with every backend:
- Primitive types (`Sphere`, `Box`, `Capsule`, `Plane`) directly construct `Px*Geometry` structs and also expose `AsJoltShape()` where possible.
- Mesh types (`ConvexMesh`, `TriangleMesh`, `HeightField`, `TetrahedronMesh`, `ParticleSystem`) wrap PhysX mesh pointers and expose transformation parameters (scale, rotation, flags). Jolt conversion is marked TODO for complex meshes.
- Helper methods provide mass properties, Poisson sampling, and mesh overlap queries by forwarding to PhysX geometry utilities.

### Scene Queries & Filtering
PhysX exposes each query variant (`RaycastAny`, `RaycastMultiple`, `Sweep*`, `Overlap*`) via `PxQueryExt`. XREngine adds a thin layer that converts engine-specific masks and optional `PhysxQueryFilter` structs to the native filter callbacks:
- `GetFiltering` builds `PxFilterData`, attaches optional pre/post delegates, and configures hit flags and sweep inflation.
- Query wrappers normalize results into shared structs (`RaycastHit`, `SweepHit`, `OverlapHit`) and automatically resolve the owning `XRComponent` so higher-level systems can dispatch gameplay logic.
- `PhysxScene.Native.CreateVTable` dynamically constructs vtables for delegates so filters and controller callbacks can hop across managed/unmanaged boundaries.

### Character Controllers
PhysX character controllers are fully supported:
- `ControllerManager` owns the `PxControllerManager`, controller filter callbacks, and obstacle contexts. It also manages debug rendering flags, tessellation settings, and overlap recovery toggles.
- `Controller` subclasses (`CapsuleController`, `BoxController`) wrap `PxController` derivatives. They buffer engine-side move requests (`Move(Vector3 delta, ...)`) into a thread-safe queue that the scene flushes every simulation step.
- Controller hit reports and behavior callbacks are surfaced as .NET events so gameplay can react when controllers bump into shapes or other controllers.

### Joint Library & Debugging
- PhysX joints (`PhysxJoint_*`) are created and tracked by the scene to maintain managed wrappers for Px joint pointers. Utility constructors centralize the PxTransform plumbing needed to connect two actors.
- Debug visualization uses `InstancedDebugVisualizer` to stream data from `PxRenderBuffer` (points, lines, triangles). Runtime toggles link back to `Engine.Rendering.Settings.PhysicsVisualizeSettings`, so enabling debug flags in the engine UI automatically pushes the same configuration into the PxScene.
- `ShiftOrigin`, solver parameters, CCD controls, and GPU copy helpers are also exposed for advanced tooling and streaming scenarios.

---

## Jolt Backend (Experimental)
The Jolt integration lives in `XREngine.Scene.Physics.Jolt`. It already shares the same public surface area but remains feature-incomplete compared to PhysX.

- `JoltScene` spins up a `PhysicsSystem` and `JobSystemThreadPool` during `Initialize()`. The default settings mirror PhysX defaults (gravity, solver iterations, cache sizes) to keep gameplay tuning similar.
- Actors (`JoltActor`, `JoltRigidActor`, `JoltDynamicRigidBody`, `JoltStaticRigidBody`) wrap Jolt `BodyID`s and read poses/velocities through the `PhysicsSystem.BodyInterface`. Components opt-in by storing a reference to their owning body wrapper.
- Queries (ray, sweep, overlap) currently create temporary bodies and run through `NarrowPhaseQuery`. The implementation covers happy paths but lacks per-layer filtering and proper hit metadata; expect to extend this before shipping anything that relies on high query fidelity.
- `StepSimulation()` updates the world with deterministic settings but leaves transform propagation commented out for now. Consumers should treat Jolt as a sandbox backend while parity work continues.

---

## Jitter2 Backend (Prototype)
`JitterScene` (under `Scene/Physics/Jitter2`) demonstrates how another solver can plug into `AbstractPhysicsScene`. At the moment only gravity, stepping (`World.Step`), and scene teardown are wired. All query and actor-management methods throw `NotImplementedException`. Use this scene as a template when adding future engines rather than a production-ready backend.

---

## Engine Components & Transforms
Physics components live in `Scene/Components/Physics` and abstract simulation specifics away from gameplay code.

- `DynamicRigidBodyComponent` and `StaticRigidBodyComponent` both require a `RigidBodyTransform`. When the `RigidBody` property changes, the component automatically removes the old actor from the world, rebinds ownership (`OwningComponent` on the PhysX wrapper), and re-adds the new actor if the component is active. The world then pulls updated poses inside `RigidBodyTransform.OnPhysicsStepped()`.
- `PhysicsActorComponent` defines the shared activation/deactivation behavior for components that manage any physics actor. It uses the world reference exposed by `XRComponent` to locate the current `AbstractPhysicsScene` and registers actors appropriately.
- `LayerMask` (in `Scene/LayerMask.cs`) is a lightweight bitmask structure with helpers to map between engine layer names and backend-specific filters. PhysX converts it to `PxFilterData.word0`; Jolt turns it into an `ObjectLayer`.

---

## Physics Chain Simulation
`PhysicsChainComponent` powers rope/cloth/hair-style simulations without depending on an external solver.

- Particles are organized per root transform (`ParticleTree`). The component supports multiple roots, optional exclusions, and automatically inserts end bones based on `EndLength`/`EndOffset`.
- Integration uses a verlet-style step with damping, elasticity, stiffness, and inertia curves that can vary along the length of the chain via distribution assets. Gravity, external forces, and object motion (to allow parenting to animated rigs) all feed into the solver.
- Collision support is provided through lightweight collider components (`PhysicsChainSphereCollider`, `PhysicsChainBoxCollider`, `PhysicsChainCapsuleCollider`, `PhysicsChainPlaneCollider`). Colliders implement `Prepare()`/`Collide()` so they can be shared across multiple chains each frame.
- A multithreaded worker pool kicks in when `Multithread` is enabled. The component queues pending work during the main update, the pool processes particle updates in parallel, and results are blended back during `LateUpdate()`.
- Distance-based disabling (`DistantDisable`) and blend weights allow gameplay systems to fade simulation in/out depending on camera proximity or animation needs. Debug rendering uses `Engine.Rendering.Debug` helpers to visualize particle positions, radii, and parent links.

---

## Queries & Result Handling
`AbstractPhysicsScene` returns results in sorted dictionaries keyed by distance. Each entry contains the `XRComponent` that owns the hit actor plus backend-specific payloads (`RaycastHit`, `SweepHit`, `OverlapHit`). Consumers often pass the dictionary through helper callbacks to translate hits into gameplay events.

When you implement a new query mode or backend:
1. Convert the incoming `Segment`/geometry pose into your solver’s data structures.
2. Apply layer filtering by translating `LayerMask.Value` into the solver’s collision mask representation.
3. Resolve solver results back to `XRComponent` instances by consulting whatever registries your backend maintains.
4. Store data in the shared structs so tooling (editor pickers, physics picking) continues to work uniformly across backends.

---

## Debugging & Tooling Hooks
- All PhysX visualization toggles mirror properties on `Engine.Rendering.Settings.PhysicsVisualizeSettings`. The scene listens for property-changed events and pushes new flags straight into `PxScene::setVisualizationParameter`.
- `PhysxScene.DebugRender()` feeds the buffered data into `InstancedDebugVisualizer`, which lazily renders points, lines, and triangles. Use this when diagnosing contact manifolds, joint frames, or CCD issues.
- Controller managers expose `SetDebugRenderingFlags`, `ComputeInteractions`, and tessellation controls so level designers can inspect controller capsules in editor builds.

---

## Extending or Swapping Backends
To add features or integrate a new solver:
1. Implement `AbstractPhysicsScene` for the target library. Reuse the PhysX scene as a reference for method semantics and data conversions.
2. Provide wrapper classes for actors, shapes, materials, and joints that keep track of unmanaged handles and owning components.
3. Ensure the world-level components (`DynamicRigidBodyComponent`, etc.) remain oblivious to the backend. If new solver-specific state is required, hide it behind the abstractions.
4. Mirror the query and filtering behavior so tooling (selection rays, physics picking) continues to work regardless of the active backend.

Known gaps to keep in mind:
- Jolt queries need proper layer filtering, hit normals, and distance calculations.
- Jitter2 lacks actor support entirely and currently cannot be selected as a runtime backend.
- PhysX GPU workflows rely on `PhysXDevice`/CUDA availability. When running without a compatible GPU, ensure the project toggles `EnableGpuDynamics` off during scene creation.

---

## Related Documentation
- [Component System](components.md)
- [Scene System](scene.md)
- [Rendering System](rendering.md)
- [Animation System](animation.md)
- [GPU Physics Chain Verification](../gpu-physics-chain-engine-verification.md)