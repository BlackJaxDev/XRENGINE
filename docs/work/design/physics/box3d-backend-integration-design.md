# Box3D Physics Backend Integration Design

Last Updated: 2026-10-06
Status: Proposed. Upstream Box3D is alpha software. The dependency is not approved.
Code todo: [Box3D Backend Integration TODO](../../todo/physics/box3d-backend-integration-todo.md)
Architecture: [Physics Architecture](../../../architecture/physics/overview.md)
Validation: [Physics Validation](../../testing/physics/physics-validation.md#box3d-backend)

## Goal

Add Erin Catto's Box3D as an optional `AbstractPhysicsScene` backend beside PhysX, Jolt, and the experimental Jitter2 backend. The backend uses the backend-neutral rigid-body, collider, query, joint, character, replication, and debug-frame contracts. Gameplay and editor code do not branch on Box3D types.

PhysX stays the default. The user must select Box3D explicitly. When Box3D is unavailable or incomplete, scene creation fails with a visible diagnostic. It never falls back to PhysX or Jolt.

Jolt is the proposed primary cross-platform backend. Re-evaluate Box3D when it leaves alpha. Include its official Emscripten build in that review for browser use.

## Upstream Maturity And Capabilities

The first documented release is `v0.1.0` (announced 2026-06-30). The author calls it alpha software. Pin an exact tag and commit. Do the capability audit again before each upgrade.

| Area | Upstream capability | XREngine consequence |
| --- | --- | --- |
| API and runtime | Portable C17 C API, opaque generational IDs, shared-library exports, MIT license, no core dependency other than the C runtime (`libm` on Unix). | Use a thin source-generated P/Invoke layer. Do not use an unreviewed third-party managed wrapper. |
| Simulation | Fixed-step rigid bodies, substeps, CCD, sleep, SIMD, multithreading, determinism, movement, contact, sensor, and joint events. | Good fit for `AbstractPhysicsScene`. Thread and callback ownership must be explicit. |
| Bodies and shapes | Static, kinematic, and dynamic bodies. Many shapes per body. Sphere, capsule, convex hull, triangle mesh, height field. | Primitive and compound authoring can map. Concave geometry is static only. |
| Queries | Ray casts, shape casts, overlaps, filters, closest and all callbacks. | Can cover ray, sweep, and overlap APIs after a result and face-index audit. |
| Joints | Revolute, prismatic, distance, spherical, weld, wheel, motor, parallel, filter. | Fixed, distance, hinge, prismatic, and spherical map directly. D6 needs a decision. |
| Character movement | Experimental geometric capsule mover outside rigid-body simulation. | Implement only after rigid-body and query parity. Show its limits. Do not claim Jolt or PhysX controller parity. |
| Concave geometry | Triangle meshes and height fields make contacts only on static bodies. Baked compounds are immutable and static only. | Reject dynamic concave and baked-compound authoring. Do not convexify or substitute. |
| Precision | Single precision by default. The double-precision build changes the ABI. | Use single precision to match `System.Numerics`. Double precision is a separate project. |
| Units | Tuned for meters, kilograms, and seconds, with moving sizes near 0.1 m to 10 m. | Keep the engine MKS assumptions. Add scale diagnostics. |

Upstream references:

- [Box3D announcement](https://box2d.org/posts/2026/06/announcing-box3d/)
- [Box3D repository](https://github.com/erincatto/box3d)
- [Box3D v0.1 documentation](https://box2d.org/documentation3d/)
- [Simulation and shapes](https://box2d.org/documentation3d/md_simulation.html)
- [Collision and concave geometry](https://box2d.org/documentation3d/md_collision.html)
- [Character mover](https://box2d.org/documentation3d/md_character.html)
- [Multithreading](https://box2d.org/documentation3d/md_foundation.html)
- [Compound shapes](https://box2d.org/documentation3d/md_compound.html)

## Proposed Decisions For The First Implementation

These decisions apply after dependency approval:

- Pin `v0.1.0` or the exact owner-approved successor. Do not track `main`.
- Use an approved `Build/Submodules/box3d` source dependency and a repository-owned CMake build. Do not use a community C# wrapper.
- Build a `win-x64` shared library with samples, docs, benchmarks, and upstream unit tests off for the shipping artifact. Run upstream tests in a separate dependency validation build.
- Use single precision. Verify `b3IsDoublePrecision() == false` before world creation.
- Start with `workerCount = 1`. Add the Box3D internal scheduler after single-thread parity. Evaluate an engine job-system adapter only when profiles show a reason.
- Use the upstream default-definition functions (`b3DefaultWorldDef`, `b3DefaultBodyDef`, `b3DefaultShapeDef`). Zero-initialized interop structs are not valid.
- Use body movement events after each step to update engine transforms. Do not scan all bodies.
- Keep native IDs and Box3D types inside the Box3D backend project.
- Reject unsupported geometry, material semantics, joint types, query details, and character capabilities explicitly.
- Keep Box3D recording and replay as opt-in diagnostics. It is not a saved-game format.

## Engine Seam

Physics backends are leaf projects behind `PhysicsBackendCatalog`. See [project organization](../../../architecture/runtime/project-organization.md).

- `EPhysicsLibrary` (`XREngine.Data/Core/Enums/EPhysicsLibrary.cs`) selects the backend.
- `IPhysicsBackendModule` supplies the ID, display name, platforms, capabilities, and scene factory. `BuiltInPhysicsBackendModules.RegisterDesktop` installs modules into `PhysicsBackendCatalog`.
- `IPhysicsBackendService` creates neutral static bodies, dynamic bodies, and character controllers.
- `PhysicsRigidBodyCreateInfo` carries collider shapes, material, pose, density, layers, limits, flags, and solver settings.
- `IPhysicsGeometry`, `PhysicsConvexHullGeometry`, `PhysicsTriangleMeshGeometry`, and `PhysicsHeightFieldGeometry` are backend-neutral geometry.
- `AbstractPhysicsScene` defines fixed stepping, queries, actor lifecycle, joint factories, and debug-frame publication.
- `IAbstractStaticRigidBody`, `IAbstractDynamicRigidBody`, and `IAbstractCharacterController` isolate gameplay from native types.

Follow the Jolt ownership shape. Use the Box3D ID and event model. Do not copy JoltPhysicsSharp patterns without review.

## Target Type And File Map

Keep one type per file.

```text
XREngine.Runtime.Physics.Box3D/
  XREngine.Runtime.Physics.Box3D.csproj
  Box3DBackendModule.cs
  runtimes/win-x64/native/
  Box3DNative.cs
  Box3DNativeLibrary.cs
  Box3DInteropTypes.cs
  Box3DScene.cs
  Box3DBackendService.cs
  Box3DActor.cs
  Box3DRigidActor.cs
  Box3DStaticRigidBody.cs
  Box3DDynamicRigidBody.cs
  Box3DShapeFactory.cs
  Box3DShapeOwner.cs
  Box3DMaterialAdapter.cs
  Box3DQueryContext.cs
  Box3DDebugFrameAdapter.cs
  Box3DCharacterController.cs
  Joints/
    Box3DJoint.cs
    Box3DFixedJoint.cs
    Box3DDistanceJoint.cs
    Box3DHingeJoint.cs
    Box3DPrismaticJoint.cs
    Box3DSphericalJoint.cs
```

If the interop surface becomes large, split it by upstream module (`World`, `Body`, `Shape`, `Query`, `Joint`, `Debug`).

## Geometry Mapping Contract

| XREngine geometry | Box3D representation | Policy |
| --- | --- | --- |
| `IPhysicsGeometry.Sphere` | `b3Sphere` shape | Validate a finite positive radius and local center. |
| `IPhysicsGeometry.Box` | `b3MakeBoxHull` hull shape | Validate half extents. Apply the local pose once. |
| `IPhysicsGeometry.Capsule` | `b3Capsule` shape | Convert XREngine half-height semantics to two sphere centers and a radius. |
| `IPhysicsGeometry.Plane` | No infinite plane in the v0.1 shape set. | Unsupported. Add a bounded plane or slab authoring type later if necessary. Never create a large box silently. |
| `PhysicsConvexHullGeometry` | Cooked `b3HullData` hull shape | Apply scale and rotation. Validate degeneracy and hull limits. Own cooked memory until shape destruction. |
| `PhysicsTriangleMeshGeometry` | Cooked `b3MeshData` mesh shape | Static bodies only. Keep scale, winding, materials, source identity, and mesh lifetime. |
| `PhysicsHeightFieldGeometry` | `b3HeightFieldData` height-field shape | Static bodies only. Keep row and column orientation, scale, holes, bounds, and lifetime. |
| Many authored shapes | Many native shapes on one body | Supported for primitives and convex shapes on all valid body types. Apply each local pose. |
| Baked static compound | `b3CompoundData` compound shape | Optional later optimization for large immutable static sets. Version and cache separately. |

Dynamic or kinematic triangle meshes, height fields, and baked compounds return a named `UnsupportedGeometryForBodyType` result. They are never dropped, converted, or attached as invalid shapes.

## Joint Mapping Contract

| XREngine joint | Box3D mapping | First disposition |
| --- | --- | --- |
| Fixed | Weld joint | Required |
| Distance | Distance joint | Required |
| Hinge | Revolute joint | Required |
| Prismatic | Prismatic joint | Required |
| Spherical | Spherical joint | Required |
| D6 | No direct v0.1 equivalent | Unsupported until a tested constraint composition exists |

Motor, wheel, parallel, and filter joints are optional Box3D extensions. They do not replace the neutral D6 contract.

## Capability Policy

Before Box3D is selectable outside developer settings, the backend capability report must cover:

- primitive, convex, mesh, height-field, multi-shape, and baked-compound support by body type;
- static and dynamic friction semantics and material combine modes;
- CCD, kinematic targets, gravity scale, sleep, locks, mass and inertia override, velocity limits, and solver overrides;
- ray, sweep, and overlap result detail, filters, face index, UV, and initial overlap;
- each neutral joint type and its limit, motor, spring, and break behavior;
- character input, arbitrary up, moving ground, dynamic interaction, query visibility, slope, step, and floor behavior, and character-to-character collision;
- collision, contact, sensor, and joint events;
- debug-draw categories;
- worker scheduling, determinism, and recording and replay;
- single or double precision.

The inspector must show an authored field as unsupported for Box3D. Native creation must not ignore a field silently.

## Behavior Rules

- Query result face index and UV: if the public query result cannot give the exact source face, return an explicit unsupported detail state and capability flag. Never return zero as a valid face.
- Friction: Box3D has one Coulomb friction value. Map dynamic friction to it and report the loss of separate static friction, unless the owner approves a tested policy. Audit combine modes against the Box3D global mixing callbacks.
- Layers: map `LayerMask` to Box3D category and mask bits. Define behavior when the engine layer width exceeds the native filter width.
- Events: copy transient native events into bounded backend-neutral buffers before the next step. Resolve owners through generation-safe registries.
- Debug: adapt `b3World_Draw` callbacks to `PhysicsDebugFrameWriter`. Never render from a native callback or keep transient native pointers. See [Physics Debug Frame](../../../architecture/physics/physics-debug-frame.md).
- Threading: no world read or write while `b3World_Step` runs. Use the engine physics mutation queue.
- Character mover: keep `CharacterControllerCapabilities` at `None` until a complete fixed-step mover exists. Do not promote Box3D for player movement until the shared v1 controller capabilities pass.
- Recording: store `.b3rec` captures as disposable evidence under `Build/_AgentValidation/<run>/`. They are not project data.

## Risk Register

| Risk | Mitigation |
| --- | --- |
| Box3D is new alpha software. Its API can change quickly. | Pin an exact release, isolate interop, check the version at load, and upgrade only in a separate audited change. |
| C struct or callback ABI is bound incorrectly. | Add native ABI probes, bool, calling-convention, and layout tests, and returned-struct coverage. |
| Native generational IDs are reused while managed wrappers survive. | Track scene generation plus native ID. Invalidate wrappers before native destruction. Reject stale events. |
| Concave or plane authoring changes silently. | Reject infinite planes and dynamic concave shapes with typed diagnostics. |
| Face index or UV is not recoverable from public query results. | Audit before claiming support. Return an unsupported detail state. |
| Material semantics differ from PhysX and Jolt. | Audit single friction, combine callbacks, damping ownership, and per-triangle material identity. |
| The geometric mover is mistaken for a simulated controller. | Gate each capability. Test moving and dynamic interaction. Keep it experimental. |
| Multithreading races with engine mutations or queries. | Establish single-thread correctness first. Forbid access during step. Use the mutation queue. |
| The internal scheduler oversubscribes hybrid CPUs. | Default to one worker. Select performance cores conservatively. Profile tail latency. |
| Cooked geometry memory is freed early. | Use reference-counted native geometry owners and destruction-order tests. |
| Box3D selection falls through to PhysX. | Register an explicit catalog module and a typed initialization failure. |
| The native DLL is missing from editor or game publish output. | Add runtime asset tests. Inspect build and publish layouts. |

## Non-Goals

- Replace PhysX as the default backend.
- Match PhysX GPU dynamics, CUDA, or vehicle features.
- Track Box3D `main` or upgrade it opportunistically.
- Enable Box3D double precision before XREngine has a double-precision world-coordinate contract.
- Support infinite planes with a large-box approximation.
- Support dynamic triangle meshes, height fields, or baked compounds.
- Claim D6 or character parity through untested approximations.
- Expose native Box3D IDs or structs to gameplay, components, serialization, or editor tools.
- Use Box3D recording as a saved-game format.
- Hide missing native support or unsupported fields behind PhysX or Jolt fallback.
