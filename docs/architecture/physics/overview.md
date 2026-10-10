# Physics Architecture

XREngine wraps multiple physics backends behind a single scene interface so gameplay, tools, and runtime code do not need to care which solver is active. The integration is centered around `AbstractPhysicsScene`, a host-side class that the world owns and ticks every fixed update. Concrete scenes translate high-level requests (actor management, queries, character control) into calls to the selected middleware while keeping compatible data structures such as `Segment`, `LayerMask`, `RaycastHit`, `SweepHit`, and `OverlapHit`.

Most gameplay features currently use the PhysX backend. Backend-neutral contracts and authored components live in `XREngine.Runtime.Core`. `XREngine.Runtime.Physics.PhysX`, `XREngine.Runtime.Physics.Jolt`, and `XREngine.Runtime.Physics.Jitter` own the implementations while keeping their existing type namespaces. Jolt remains a parity candidate; Jitter is an opt-in experimental reference implementation.

Applications register installed backends in `PhysicsBackendCatalog` during composition. Each module supplies a stable `EPhysicsLibrary` ID, display name, supported platforms, capabilities, and scene factory. Selecting a known backend whose module was not installed throws a named diagnostic. The serialized enum values remain unchanged. Desktop bootstrap installs PhysX and Jolt; Jitter is not in the default composition.

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


## Backend-neutral gameplay API contract

Shared gameplay and XRComponent code should depend on the contracts in `XREngine.Scene` instead of PhysX concrete types:

- rigid bodies: `IAbstractPhysicsActor`, `IAbstractRigidPhysicsActor`, `IAbstractDynamicRigidBody`, and `IAbstractStaticRigidBody`;
- character controllers: `IAbstractCharacterController`;
- scene queries: `AbstractPhysicsScene.IAbstractQueryFilter` or the concrete backend-neutral `PhysicsQueryFilter`;
- joints: `IAbstractJoint` and the typed joint interfaces under `XREngine.Scene.Physics.Joints`.

Backend-specific properties are allowed only as explicit extension surfaces. PhysX extension members must be grouped as `Physics / PhysX Extensions` in component metadata and documented as non-portable. They should not be required by ordinary gameplay systems, Jolt scenes, or shared editor workflows.

Live body properties used by shared components pass through `IPhysicsRuntimeBodyProperties` and `IPhysicsDynamicBodySettings`. Native shape cooking for generated static colliders passes through the optional `IPhysicsConvexHullInstaller` supplied by PhysX. The desktop editor installs `IPhysicsColliderAuthoringService` from `XREngine.Runtime.Physics.Authoring`; CoACD execution and its disk-cache I/O live there. Applications without that service must load cooked collider data instead of generating it at runtime, and authoring requests receive a named missing-service diagnostic. Image-backed height-field creation uses `IPhysicsHeightFieldImageSource` so PhysX does not depend directly on the imaging implementation.

`PhysicsQueryFilter` is the portable query representation. PhysX maps it to `PxQueryFlags`/`PxHitFlags`; Jolt maps the same actor-type selection to static/dynamic motion filtering. Use `PhysxScene.PhysxQueryFilter` only when a caller needs native PhysX pre/post query callbacks.

## Jolt parity and unsupported-field policy

The current Jolt rigid-body mapping supports gravity toggles, simulation/debug/sleep flags, damping, max velocity limits, mass and inertia settings, center-of-mass pose storage, kinematic/CCD-style flags, lock axes, and layer/object-filter updates. Fields without a native Jolt equivalent must remain visible in inspectors as unsupported or PhysX-extension fields rather than being silently ignored.

Collision filtering parity is defined around shared `LayerMask` behavior plus `PhysicsQueryFilter.ActorTypes`: static-only, dynamic-only, and all-body queries must include the same categories in PhysX and Jolt. Backend-specific callback semantics remain PhysX extensions until an abstract callback contract is introduced.


### Controller and collider authoring

`CharacterControllerComponent` is the reusable controller owner for gameplay code that wants controller lifecycle separated from movement behavior. `CharacterMovement3DComponent` can bind to a sibling controller component, or continue to create its legacy private controller when no reusable controller owner is present. Controller lifecycle and contact-state changes are exposed through backend-neutral events.

### Character controller contract

- Motion commands carry a `CharacterMotionInputModel`: `Velocity` (units per second, the default) or `Displacement` (distance for the command duration). Each queued command keeps its model and duration, so a runtime model change never reinterprets queued work. Model changes apply on a physics-step boundary.
- `TickInputWithPhysics = false` produces movement commands on the Update thread with `Engine.Delta`. `true` produces them on the fixed PrePhysics thread with `Engine.FixedDelta`. Update-thread commands cross to physics as a duration-tagged stream and are resampled by duration. Update cadence does not change distance, acceleration, gravity, or jump count. Movement never reads a render or present delta.
- Each backend converts the model once at its native boundary. PhysX passes displacement through and converts velocity with the fixed delta. Jolt passes velocity through and converts displacement with the fixed delta. A zero or invalid fixed delta causes no native move and keeps queued remainders.
- Jolt calls `CharacterVirtual.ExtendedUpdate` on every fixed step, also with zero input. `MinMoveDistance` does not suppress contact refresh, floor sticking, stair logic, or moving ground. Ground velocity is added once.
- `TotalHeight` is the full capsule height. Backends convert it to a native cylinder height of `max(0, totalHeight - 2 * radius)`.
- Jolt `CharacterPadding`, `PredictiveContactDistance`, `CollisionTolerance`, floor-stick distance, step-up distance, and extra step-down distance are independent settings.
- `CharacterSupportState` (`Unknown`, `InAir`, `Supported`, `TooSteep`, `NotSupported`) comes from the backend support model. `CollidingUp`, `CollidingDown`, and `CollidingSides` report contact location relative to `UpDirection` only. Movement exposes `GroundNormal` and `GroundVelocity` through neutral types.
- Locomotion math projects along and across the normalized `UpDirection`. It does not assume world Y up.
- `PhysicsCharacterControllerCapabilities` reports per-backend features such as displacement and velocity input, arbitrary up, moving ground, predictive contacts, independent step-down, and PhysX-only materials, invisible walls, and constrained climbing. Unsupported fields stay visible as capability differences.

Collider/material authoring should prefer `PhysicsMaterialDefinition` and `PhysicsColliderShape`. Rigid-body components still accept the legacy single `Geometry`/`Material` fields, but `ColliderShapes` is the compound-authoring surface. PhysX and Jolt iterate all enabled collider shapes, retaining each shape's local pose and material settings. Runtime edits should call `RebuildCollisionShapes()` so ownership, registration, and cached velocities are handled coherently.


### Runtime ownership and diagnostics

Rigid-body components expose `ReplicationAuthority` and `OwnerClient` metadata so networking code can make explicit ownership decisions for rigid bodies, controllers, and joints. Isolated Windows editor runs have exercised both PhysX and Jolt, and targeted tests cover selected lifecycle and query behavior. Full backend parity, reload/leak behavior, and browser qualification remain validation gates before changing the default. Jolt exposes `GetDiagnostics()` plus debug-render collection hooks so tests can assert actor/controller/joint counts without reaching into backend dictionaries.

### Backend service, ownership, and boundary rules

- Components create actors, controllers, and colliders through `IPhysicsBackendService` (`XREngine.Runtime.Core/Scene/Physics/PhysicsBackendService.cs`). Shared code does not switch on concrete scene types.
- Joint components (Fixed, Distance, Hinge, Prismatic, Spherical, D6) own native joint creation, destruction, and rebinding. Settings are serialized component properties and are pushed to the active native joint. A joint rebinds when its connected bodies activate before or after the joint component.
- Runtime-only constraints, such as grab constraints, use `RuntimeDistanceConstraintOwner`. Creation and removal are idempotent and lifecycle-owned. Authored constraints use the joint component family.
- Reusable collider data uses `PhysicsColliderAsset` (`PhysicsMeshGeometry.cs`). Convex-decomposition output converts to this asset. Shape-authoring data stays independent from backend cooking artifacts.
- Live shape replacement keeps native identity: Jolt keeps the body identifier, and PhysX keeps the actor pointer, releases old shape and material references, and recomputes mass and inertia.
- Unsupported backend-native geometry is rejected explicitly. A backend never substitutes a different collision shape.
- Jolt queries return PhysX-compatible triangle barycentric UVs for authored triangle meshes and zero UV for other shapes. Authored source face IDs survive compound and decorated shapes.
- Replication handoff uses `PhysicsReplicationAuthority`, network identity, lease-owner metadata, and server fallback (`PhysicsReplicationPolicy`).
- Jolt native debug extraction for shapes, constraints, and contacts is in `JoltEngineDebugRenderer`.

Boundary tests enforce these rules: `PhysicsBackendBoundaryTests`, `PhysicsGameplayApiBoundaryTests`, `PhysicsGeometryAdapterBoundaryTests`, and `PhysicsP0ApiContractTests` reject `Physx*`, `Jolt*`, and `MagicPhysX.Px*` types in shared gameplay APIs outside named backend extensions. Parity fixtures include `JoltQueryParityTests`, `JoltGeometryParityTests`, `JoltControllerParityTests`, `JoltProductionHardeningTests`, `PhysicsSceneSerializationTests`, `PhysicsReplicationPolicyTests`, `RuntimeDistanceConstraintOwnerTests`, and `PhysxShapeMutationTests`.

## PhysX Backend
PhysX 5 is the current primary, fully-featured integration. Its scene, actors, controllers, joints, geometry adapter, and backend service live in `XREngine.Runtime.Physics.PhysX` under the stable `XREngine.Scene.Physics.Physx` namespace.

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
`IPhysicsGeometry` is a backend-neutral marker contract in `Scene/Physics/IPhysicsGeometry.cs`:
- Primitive authoring types (`Sphere`, `Box`, `Capsule`, `Plane`) contain only portable values.
- `PhysicsConvexHullGeometry`, `PhysicsTriangleMeshGeometry`, and `PhysicsHeightFieldGeometry` contain serializable CPU-authored data consumed natively by both backends.
- Jolt conversion is owned by `JoltShapeFactory`; PhysX conversion, cooking, mass-property helpers, and geometry queries are owned by `PhysxGeometryAdapter`.
- Already-cooked PhysX pointers use explicitly named `Physx*GeometryExtension` types. They are backend extensions and are not portable collider authoring contracts.

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
- Debug visualization copies `PxRenderBuffer` points, lines, and triangles into a backend-neutral frame once per step. See [Physics Debug Frame](physics-debug-frame.md). Runtime toggles link back to `Engine.Rendering.Settings.PhysicsVisualizeSettings`, so enabling debug flags in the engine UI automatically pushes the same configuration into the PxScene.
- `ShiftOrigin`, solver parameters, CCD controls, and GPU copy helpers are also exposed for advanced tooling and streaming scenarios.

---

## Jolt Backend
The Jolt integration lives in `XREngine.Runtime.Physics.Jolt` under the stable `XREngine.Scene.Physics.Jolt` namespace. Its module reports supported capabilities explicitly. Desktop parity and browser qualification determine whether it can become the default backend; it does not silently implement PhysX-only settings.

- `JoltScene` spins up a `PhysicsSystem` and `JobSystemThreadPool` during `Initialize()`. The default settings mirror PhysX defaults (gravity, solver iterations, cache sizes) to keep gameplay tuning similar.
- Actors (`JoltActor`, `JoltRigidActor`, `JoltDynamicRigidBody`, `JoltStaticRigidBody`) wrap Jolt `BodyID`s and read poses/velocities through the `PhysicsSystem.BodyInterface`. Components opt-in by storing a reference to their owning body wrapper.
- Native body allocation ownership is separate from active scene attachment. Deactivation removes a body from simulation without giving up ownership, so reactivation in the same live scene preserves its identifier. Scene destruction releases attached and detached bodies and their shape metadata, invalidates their identifiers, and clears component body links. Destroyed wrappers and wrappers allocated by another physics system cannot be reattached; `AllocatedActorCount` includes detached allocations while `GetDiagnostics()` reports active registrations.
- Ray, sweep, and overlap queries use `NarrowPhaseQuery` with shared layer masks and static/dynamic actor filters. Results include the owning component, hit position, normal, distance, and backend payload where applicable.
- `StepSimulation()` consumes buffered controller movement, updates `PhysicsSystem`, publishes its debug frame, propagates dynamic body poses through `IPhysicsStepListener.OnPhysicsStepped()`, and raises `NotifySimulationStepped()`. Cross-platform deterministic replay is not claimed with the current desktop native supply.

---

## Jitter2 Backend (Prototype)
`JitterScene` in `XREngine.Runtime.Physics.Jitter` demonstrates how another solver can plug into `AbstractPhysicsScene`. The module is experimental and must be installed explicitly. Its unimplemented operations are not a production-ready backend.

---

## Engine Components & Transforms
Physics components live in `Scene/Components/Physics` and abstract simulation specifics away from gameplay code.

Shared rendered and headless world hosts initialize the native physics scene before attaching authored scene nodes. Assigning a world context can activate components before gameplay begins, so actor creation must already be available at that boundary. Hosts apply authored physics settings after native initialization and reuse the initialized scene when beginning play; gameplay and component-activation callback ordering stays unchanged.

- `DynamicRigidBodyComponent` and `StaticRigidBodyComponent` both require a `RigidBodyTransform`. When the `RigidBody` property changes, the component automatically removes the old actor from the world, rebinds its neutral actor ownership, and re-adds the new actor if the component is active. The world then pulls updated poses inside `RigidBodyTransform.OnPhysicsStepped()`.
- `PhysicsActorComponent` defines the shared activation/deactivation behavior for components that manage any physics actor. It uses the world reference exposed by `XRComponent` to locate the current `AbstractPhysicsScene` and registers actors appropriately.
- `LayerMask` (in `Scene/LayerMask.cs`) is a lightweight bitmask structure with helpers to map between engine layer names and backend-specific filters. PhysX converts it to `PxFilterData.word0`; Jolt turns it into an `ObjectLayer`.

---

## Physics Chain Simulation
`PhysicsChainComponent` powers rope/cloth/hair-style simulations without depending on an external solver.

- The component is an authoring facade. `PhysicsChainWorld` owns registration, scheduling, shared templates and collider sets, arenas, quality tiers, and outputs. See [Physics Chain World Runtime](physics-chain-world-runtime.md).
- Particles are organized per root (`PhysicsChainTemplateTree`). The component supports multiple roots, optional exclusions, and end bones from `EndLength`/`EndOffset`.
- Integration uses a verlet-style step with damping, elasticity, stiffness, and inertia curves that can vary along the chain. Gravity, external forces, and root motion feed the solver.
- Collision uses shared collider components (`PhysicsChainSphereCollider`, `PhysicsChainBoxCollider`, `PhysicsChainCapsuleCollider`, `PhysicsChainPlaneCollider`) that become versioned collider sets.
- CPU (`PhysicsChainCpuBackend`) and GPU (`GPUPhysicsChainDispatcher`) backends write current and previous palettes and conservative bounds directly. See [Physics-chain compute backends](physics-chain-compute-backends.md) and [Physics-chain output and readback](physics-chain-output-and-readback.md).

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
- Jolt's default promotion still requires the full contract parity and browser runtime gates, including compound mutation, joints, reload, authority, and callbacks.
- Jitter2 remains experimental with unimplemented operations; selecting it requires explicit module installation.
- PhysX GPU workflows rely on `PhysXDevice`/CUDA availability. When running without a compatible GPU, ensure the project toggles `EnableGpuDynamics` off during scene creation.

---

## Related Documentation
- [Physics Debug Frame](physics-debug-frame.md)
- [Physics Chain World Runtime](physics-chain-world-runtime.md)
- [Component API](../../developer-guides/components/component-api.md)
- [Scene Architecture](../scene/overview.md)
- [Rendering Runtime Overview](../rendering/runtime-overview.md)
- [Animation API](../../developer-guides/animation/animation-api.md)
- [Physics API](../../developer-guides/physics/physics-api.md)
- [Physics Chain Performance](../../developer-guides/rendering/physics-chain-performance.md)
- [Physics Validation](../../work/testing/physics/physics-validation.md)
- [Physics-chain output and readback](physics-chain-output-and-readback.md)
- [Skinning](../../developer-guides/rendering/skinning.md)
