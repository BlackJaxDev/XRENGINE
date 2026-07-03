# Generic ECS Runtime And Humanoid Avatar Networking Design

Last Updated: 2026-07-02
Status: design proposal
Scope: generic runtime ECS backend, humanoid player avatar state, full-body VR pose replication, interpolation, IK, and render handoff for hundreds to thousands of represented players.

Related docs:

- [XRENGINE Networking](../../../developer-guides/networking/networking.md)
- [XR Job Manager](../../../developer-guides/runtime/job-system.md)
- [Networking design](../networking/networking.md)
- [Peer-to-peer host switching](../networking/peer-to-peer-host-switching.md)
- [GPU-driven animation](../rendering/gpu/gpu-driven-animation.md)
- [Skinning](../../../developer-guides/rendering/skinning.md)
- [Avatar optimization and virtualized avatar rendering](../rendering/avatar-optimization-and-virtualized-rendering-design.md)
- [Zero-readback GPU-driven rendering plan](../rendering/zero-readback-gpu-driven-rendering-plan.md)
- [Production GPU-driven rendering roadmap](../../todo/rendering/gpu/production-rendering-pipeline-roadmap.md)

## 1. Summary

XRENGINE should introduce a generic data-oriented runtime ECS backend and use humanoid multiplayer avatars as its first high-pressure feature. The ECS backend should not be avatar-specific. It should be a reusable substrate for any future feature that needs dense state, batched updates, deterministic scheduling, network replication, GPU uploads, or low-allocation hot paths.

ECS is a parallel runtime system, not a replacement for the scene graph or component system. Programmers can opt into it for features that benefit from data-oriented storage and batch execution while continuing to use `SceneNode`, `TransformBase`, and `XRComponent` for normal scene authoring, editor inspection, prefab workflows, gameplay components, and object-oriented feature code.

Humanoid avatars then become a feature module layered on top:

1. Real local players sample headset, controller, tracker, hand, face, and gameplay input into dense ECS state.
2. Servers validate authority leases and stamp accepted avatar state with server tick/time.
3. Clients receive pose streams into interpolation buffers, run the necessary IK/animation solve for visible avatars, and publish render-facing skin palettes or lower-cost representations.
4. Rendering consumes compact ECS-produced buffers through existing GPU skinning, GPU-driven submission, avatar LOD, impostor, and future virtualized-avatar paths.

The key rule is:

> Scene graph and ECS coexist. Authoring and object-oriented gameplay stay component based; high-volume runtime state may opt into dense, versioned, system-owned ECS storage.

`SceneNode` and `XRComponent` remain first-class engine systems. They should not be forced to store every piece of high-frequency data for hundreds of networked avatars, but they also should not be removed, downgraded, or treated as legacy.

## 2. Problem Statement

The current engine already has useful realtime contracts:

- `NetworkEntityId`, `NetworkAuthorityLease`, `NetworkReplicationChannel`, `NetworkSnapshotEnvelope`, and `NetworkDeltaEnvelope`.
- `HumanoidPoseFrame`, `HumanoidPosePacketKind`, and the six-tracker `HumanoidPoseCodec`.
- `VRIKSolverComponent` can capture local humanoid poses and apply received poses.
- GPU skinning and GPU-driven rendering already have the right direction for high-volume render submission.

The missing piece is a shared runtime data model. Today, pose networking is component-centric: each solver can capture, send, receive, parse, and apply pose traffic independently. That is workable for tests and small sessions, but it scales poorly:

- per-avatar object traversal,
- per-component packet parsing,
- dictionary lookups in hot paths,
- repeated allocation-prone DTO construction,
- difficult per-connection interest management,
- weak separation between local-authoring components and replicated runtime state,
- no generic dense data plane for future non-avatar features.

The target scale needs different behavior:

- Several hundred visible avatars should be possible in one instance with mixed update/render LOD.
- Thousands of represented players should be possible when most are low-frequency, low-detail, or non-rendered.
- Full-fidelity tracked VR should remain excellent for the local player and nearby interacting players.
- Server and client logic should reuse the same state model where possible, with headless/server builds able to omit render-only modules.

## 3. Goals

- Add a generic runtime ECS backend that can be reused by avatars, projectiles, interactables, NPCs, crowds, streaming feedback, gameplay state, and renderer-facing instance data.
- Keep high-volume state in dense arrays with stable integer handles.
- Keep external identities such as `Guid`, `NetworkEntityId`, asset ids, and scene-node ids at the edges.
- Support explicit system scheduling over tick phases, jobs, and dependencies.
- Respect the user's configured CPU worker budget and allow high-volume systems to scale across the cores they are willing to allocate.
- Support dirty-range tracking for networking and GPU uploads.
- Support deterministic snapshot/delta production from ECS-owned state.
- Preserve `SceneNode`/`XRComponent` as first-class editor, prefab, inspection, and authoring surfaces.
- Let programmers choose ECS per feature or subsystem instead of requiring a whole-engine scene-graph migration.
- Let feature modules register component types, systems, serializers, and bridge adapters without changing the ECS core.
- Provide avatar-specific replication LOD, AOI, interpolation, IK, animation, and render-output systems on top of the generic backend.

## 4. Non-Goals

- Do not replace the scene graph or component system.
- Do not require every existing component to become an ECS component immediately.
- Do not treat ECS as the only correct way to build engine features.
- Do not make ECS depend on rendering, networking, humanoids, OpenXR, or OpenVR.
- Do not build a Unity DOTS clone or bring in a large ECS dependency unless a later decision proves it is better than a small engine-owned backend.
- Do not replicate final bone matrices for normal humanoid avatar networking. Replicate tracker intent and compact state, then solve locally.
- Do not silently hide missing accelerated paths behind CPU fallbacks for high-scale avatar modes. Unsupported scale/LOD paths should emit diagnostics.

## 5. Design Principles

1. ECS is a parallel runtime store, not the editor object model.
2. Dense integer ids are the hot-path identity.
3. Stable external ids are lookup edges.
4. Struct-of-arrays and chunked arrays are preferred for hot components.
5. Systems own mutation. Components are data.
6. Every component store can publish dirty ranges and generation counters.
7. Feature modules may compose through common ECS primitives instead of bespoke managers when ECS is a good fit.
8. Networking, rendering, physics, animation, and editor bridges are modules, not ECS-core dependencies.
9. Local player fidelity beats crowd fidelity.
10. Network and render LOD are first-class data, not incidental code branches.
11. Per-frame parallel work should be partitioned into coarse ranges, not one scheduled job per entity.

## 6. Current Engine Shape

Relevant existing runtime contracts:

- `XREngine.Runtime.Core/Networking/NetworkingMessages.cs`
  - `HumanoidPoseFrame`
  - `PlayerAssignment`
  - `PlayerTransformUpdate`
- `XREngine.Runtime.Core/Networking/HumanoidPoseSync.cs`
  - `HumanoidPoseSample`
  - `HumanoidPoseCodec`
  - `HumanoidPosePacketBuilder`
- `XREngine.Runtime.Core/Networking/ReplicationContracts.cs`
  - `NetworkEntityId`
  - `NetworkAuthorityLease`
  - `RealtimeReplicationCoordinator`
- `XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/IK/VRIKSolverComponent.cs`
  - local pose capture
  - baseline/delta sending
  - received baseline storage
  - target application
- `XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/HumanoidComponent.cs`
  - humanoid bone mapping, IK target data, muscle values, and avatar pose logic.

The ECS design should preserve these contracts where they are already useful, but move packet fan-in/fan-out, interpolation, interest management, and batched pose application out of individual components.

## 7. Generic ECS Backend

### 7.1 Core Types

The backend should expose a small set of generic concepts:

```csharp
public readonly record struct RuntimeEntity(int Index, int Generation);

public interface IRuntimeComponent
{
}

public interface IRuntimeSystem
{
    RuntimeSystemPhase Phase { get; }
    void Execute(RuntimeSystemContext context);
}

public sealed class RuntimeEntityWorld
{
    public RuntimeEntity CreateEntity();
    public void DestroyEntity(RuntimeEntity entity);
    public ComponentStore<T> Store<T>() where T : unmanaged, IRuntimeComponent;
    public EntityLookup<TKey> Lookup<TKey>() where TKey : notnull;
}
```

The exact C# API can evolve, but the data model should stay clear:

- `RuntimeEntity` is a transient dense handle with generation safety.
- `ComponentStore<T>` owns dense storage for one component type.
- `EntityLookup<TKey>` maps stable external ids to runtime entities.
- `RuntimeEntityWorld` owns creation, destruction, structural changes, and system execution.

### 7.2 Storage Model

The first implementation can use sparse-set component stores:

```text
denseEntities[]      RuntimeEntity
denseComponents[]    T
sparseIndex[]        entity index -> dense slot
dirtyWords[]         bitset or generation range
version[]            per dense slot version
```

This is simpler than full archetype chunks and still gives fast iteration over each component type. If future features require true multi-component archetype chunks, the public system/query surface should allow the storage backend to change later.

Hot stores should prefer unmanaged components. Managed references can exist, but should be isolated in bridge components that are not iterated by high-volume systems.

### 7.3 Reusable Infrastructure

The ECS core should provide generic services that feature modules can reuse:

- entity creation/destruction queues,
- stable-id lookup tables,
- dense component stores,
- query builders,
- per-store generation counters,
- dirty bitsets and changed ranges,
- fixed-step and variable-step scheduling,
- event streams for structural and state changes,
- pooled temporary buffers,
- snapshot/delta cursors,
- GPU upload cursors,
- diagnostics counters,
- deterministic test harnesses.

Feature modules should not each invent their own dense-id allocator, dirty tracking, packet cursor, or GPU-upload staging path.

### 7.4 System Phases

System scheduling should map cleanly onto existing engine timing without exposing ECS internals to all components.

Suggested phases:

```text
InputSample
NetworkReceive
Prediction
AuthoritativeSimulation
Animation
IK
PhysicsBridge
ReplicationBuild
Interpolation
RenderPrepare
GpuUpload
Diagnostics
```

Each system declares:

- read component sets,
- write component sets,
- phase,
- fixed or variable tick,
- server/client/local role mask,
- optional job parallelism policy.

This lets the runtime run avatar pose decode, local input sampling, animation, IK, AOI, and render upload in a predictable order without making every feature a special case.

### 7.5 Relationship To The Scene Graph

The scene graph and ECS should run side by side. The scene graph remains the authoritative structure for scene ownership, transforms where object graph semantics matter, prefab composition, editor selection, component authoring, and general gameplay scripting. ECS is available as an additional runtime backend for systems that need dense iteration, batched jobs, network snapshots, or GPU upload streams.

Programmers should be able to choose from three valid patterns:

| Pattern | Intended use |
| --- | --- |
| Scene graph only | Normal editor-authored objects, low-count gameplay components, tools, UI, bespoke behavior. |
| ECS only | Headless/runtime-only data, crowds, network state, transient simulation records, dense render or streaming feedback. |
| Hybrid bridge | Authored scene objects that publish or consume dense runtime data, such as avatars, interactables, animation instances, and render proxies. |

`SceneNode` and `XRComponent` can bridge into ECS through thin adapters when a feature wants both authoring ergonomics and dense runtime execution:

```text
SceneNode prefab/component authoring
    -> RuntimeEntityBinding
    -> ECS entity and component stores
    -> systems update dense data
    -> optional bridge writes back to SceneNode transforms for editor-visible objects
```

For high-volume avatars, the bridge should be selective:

- Local player: bidirectional bridge for controls, editor/debug inspection, and gameplay.
- Nearby remote player: ECS drives IK targets and render state, with optional scene-node writeback for debug/editor selection.
- Far player: ECS drives render instance or impostor only; no full scene hierarchy update.
- Server: no render bridge and no editor writeback unless explicitly enabled.

### 7.6 Networking Bridge

Networking should interact with ECS through channel-specific systems:

- `NetworkEntityBindingSystem` maps `NetworkEntityId` to `RuntimeEntity`.
- `AuthorityLeaseSystem` mirrors `NetworkAuthorityLease` into ECS authority components.
- `ReplicationReceiveSystem` decodes incoming payloads into ECS state.
- `ReplicationBuildSystem` produces snapshot/delta payloads from dirty ECS state.
- `ConnectionInterestSystem` builds per-connection relevance lists and priorities.

The generic replication layer should not know humanoid semantics. It should know:

- entity id,
- channel id,
- component serializers,
- baseline tick,
- dirty generation,
- connection interest,
- byte budget.

Humanoid pose replication is then one channel implementation.

### 7.7 GPU Upload Bridge

The ECS backend should expose generic GPU upload staging:

- component store dirty ranges -> upload ranges,
- stable GPU instance ids,
- double or triple buffered previous/current state where needed,
- no readback requirement for normal visible paths,
- role-specific omission for server/headless builds.

Avatar render output, transform buffers, animation parameters, skin-palette ranges, and impostor instance data can all use the same upload cursor model.

### 7.8 Job-System Integration And CPU Budgets

The ECS backend should use the existing XR job system for CPU-parallel work. It should not create a separate avatar thread pool. The engine already exposes worker count controls through job settings and environment variables such as `XR_JOB_WORKERS`, `XR_JOB_WORKER_CAP`, `XR_JOB_QUEUE_LIMIT`, and `XR_JOB_QUEUE_WARN`; the ECS scheduler should honor those limits.

The design goal is:

> Users can spend more CPU cores on avatars and other ECS systems when they want scale, while conservative defaults preserve editor responsiveness, render-thread headroom, audio, networking, and input latency.

The ECS scheduler should provide a low-overhead parallel range API over `Engine.Jobs`:

```csharp
public readonly record struct RuntimeWorkerBudget(
    int MaxWorkers,
    int MinItemsPerBatch,
    float TimeBudgetMs,
    RuntimeBudgetOverflowPolicy OverflowPolicy);

public interface IRuntimeParallelScheduler
{
    RuntimeParallelFence ScheduleRange(
        string label,
        int itemCount,
        RuntimeWorkerBudget budget,
        Action<RuntimeRange> executeRange);
}
```

This API is intentionally range-based. A system should schedule a small number of chunk/range jobs, usually no more than the effective worker count, rather than scheduling one job per avatar. For example, 1,000 avatars with a 16-worker budget should become roughly 16 to 64 range jobs depending on batch size and cache behavior, not 1,000 jobs.

Suggested budget controls:

| Setting | Meaning |
| --- | --- |
| `RuntimeEcsWorkerBudgetMode` | `Auto`, `Manual`, or `Disabled` for ECS worker use. |
| `RuntimeEcsMaxWorkers` | Optional cap for all ECS systems, clamped by `JobManager.WorkerCount`. |
| `RuntimeEcsReserveWorkers` | Workers to leave for non-ECS background work even when many cores are available. |
| `AvatarUpdateMaxWorkers` | Optional avatar-module cap inside the generic ECS cap. |
| `AvatarUpdateMinBatchSize` | Minimum avatars per scheduled range. |
| `AvatarUpdateTimeBudgetMs` | Soft per-frame CPU time target before degrading optional avatar work. |

These can start as design-level settings and later map to project/user overrides if implementation proves them useful. Defaults should be conservative: use enough workers to avoid single-thread bottlenecks, but leave room for rendering, IO, audio, networking, and editor work.

### 7.9 Parallel System Rules

Systems that opt into parallel execution must obey stricter rules:

- Read-only component stores can be shared across all ranges.
- Writable component stores must be partitioned so each range writes disjoint dense slots.
- Structural changes are forbidden inside parallel range execution; systems enqueue create/destroy/add/remove requests for the structural phase.
- Managed object access, scene-node mutation, GPU API calls, and editor/UI calls are forbidden inside worker ranges.
- Per-range temporary storage must come from pooled or stack/local buffers.
- Systems publish one fence per phase; dependent systems wait at explicit phase boundaries.
- If work misses its soft time budget, the system degrades optional work using LOD policy rather than spilling unbounded work into the next frame.

This keeps parallel avatar updates deterministic enough to debug and prevents worker threads from mutating the object graph.

### 7.10 Avatar Work Partitioning

Avatar update work should be split by solve level and data dependency:

```text
Input/local prediction:
    small local-player set, usually main/update thread or one worker range

Network decode/interpolation:
    parallel by received packet ranges or avatar sample ranges

IK and animation:
    parallel by avatar solve tier
    full solve ranges first
    reduced/proxy ranges second
    optional far crowd work last

Render prepare:
    parallel by dense avatar render ranges
    writes disjoint render instance and palette ranges
```

The scheduler should prioritize nearby/high-fidelity avatars before optional crowd work. If the user allocates more workers, the same phase can process more avatars at high fidelity. If the budget is tight, low-priority tiers degrade or skip while local and nearby interaction avatars remain stable.

Important: the local player's prediction and VR input sampling should not wait behind large remote-avatar batches. Local player work should have its own high-priority path and should complete before remote avatar visual refinement.

## 8. Humanoid Avatar Feature Module

### 8.1 Avatar Entity

Each networked avatar gets one primary ECS entity. Additional entities may represent attachments, held objects, voice emitters, or debug gizmos, but the core replicated player body should be one dense avatar entity.

Suggested hot components:

```csharp
public struct AvatarIdentity : IRuntimeComponent
{
    public int DenseAvatarId;
    public NetworkEntityId NetworkEntityId;
    public int ServerPlayerIndex;
    public Guid SessionId;
}

public struct AvatarAuthority : IRuntimeComponent
{
    public NetworkAuthorityMode Mode;
    public int OwnerConnectionIndex;
    public double LeaseExpiryUtc;
}

public struct AvatarTrackerPose : IRuntimeComponent
{
    public Vector3 RootPosition;
    public Quaternion RootRotation;
    public AvatarTrackerSet Trackers;
    public uint ValidMask;
    public uint DirtyMask;
    public long SampleTick;
}

public struct AvatarRigBinding : IRuntimeComponent
{
    public int HumanoidProfileId;
    public int SkeletonId;
    public int BonePaletteBase;
    public int BoneCount;
}

public struct AvatarRenderInstance : IRuntimeComponent
{
    public int MeshVariantId;
    public int MaterialSetId;
    public int Lod;
    public AvatarRepresentation Representation;
}
```

The `AvatarTrackerSet` should start with the current six tracker slots:

- hips,
- head,
- left hand,
- right hand,
- left foot,
- right foot.

It should be designed to grow without breaking the base channel:

- chest,
- elbows,
- knees,
- finger curls,
- facial expression weights,
- eye gaze,
- voice/lip sync weights,
- per-tracker confidence,
- linear/angular velocity.

### 8.2 Local VR Sampling

Local VR players should sample OpenXR/OpenVR input into ECS in one system:

```text
VrDeviceSamplingSystem
    reads runtime VR device state
    writes AvatarTrackerPose
    writes AvatarInputIntent
    writes AvatarTrackingQuality
```

This replaces each solver independently pulling device state or preparing its own network packet. Component-facing VR objects can still exist for authoring and inspection, but the local avatar's authoritative runtime pose lives in ECS.

### 8.3 Calibration And Body Model

Calibration should be data, not solver-only state:

```csharp
public struct AvatarCalibration : IRuntimeComponent
{
    public float UserHeightMeters;
    public float ArmSpanMeters;
    public float ShoulderWidthMeters;
    public Matrix4x4 HeadOffset;
    public Matrix4x4 LeftHandOffset;
    public Matrix4x4 RightHandOffset;
    public Matrix4x4 HipOffset;
}
```

The same calibration can feed:

- local IK,
- remote reconstruction,
- body-scale estimation,
- network quantization ranges,
- avatar profile selection,
- anti-cheat plausibility checks.

### 8.4 Avatar Pose Replication

The first implementation should preserve the existing `HumanoidPoseFrame` envelope and `HumanoidPoseCodec` concept, but move usage into batched systems:

```text
AvatarPoseReplicationBuildSystem
    reads AvatarTrackerPose, AvatarAuthority, AvatarReplicationState
    writes HumanoidPoseFrame payloads

AvatarPoseReplicationReceiveSystem
    reads HumanoidPoseFrame payloads
    writes AvatarNetworkSampleBuffer
```

The current packet format can carry multiple avatars in one frame. That should become the normal path, not an incidental builder capability.

The avatar pose channel should evolve from "six tracker positions plus root yaw" to a versioned payload family:

```text
AvatarPoseV1:
    root position
    root yaw
    six local tracker positions

AvatarPoseV2:
    V1
    tracker rotations
    tracker velocities
    per-tracker confidence mask

AvatarPoseV3:
    V2
    finger curls or hand skeleton summary
    face/eye/lip weights as optional side payloads
```

Older clients can still consume lower versions if the session protocol allows it.

### 8.5 Server Authority

Server-side ECS systems should:

1. Decode incoming avatar pose frames.
2. Resolve `NetworkEntityId` to `RuntimeEntity`.
3. Validate active `NetworkAuthorityLease`.
4. Reject stale, cross-session, or unauthorized samples.
5. Clamp impossible movement/tracker deltas with diagnostics.
6. Stamp accepted state with server tick/time.
7. Write authoritative `AvatarTrackerPose`.
8. Build per-connection outgoing pose frames according to AOI and budget.

The server does not need to run full visual IK for every avatar. It needs enough pose state for:

- authority validation,
- gameplay interactions,
- proximity and AOI,
- hit/interaction lag compensation,
- authoritative rebroadcast,
- optional server-side recording/moderation.

### 8.6 Client Interpolation

Remote avatar clients should not apply network samples directly to scene transforms. They should buffer and reconstruct:

```csharp
public struct AvatarRemoteInterpolation : IRuntimeComponent
{
    public int SampleRingBase;
    public int SampleCount;
    public double RenderDelaySeconds;
    public double LastServerTimeUtc;
}
```

The interpolation system should:

- render at estimated server time minus adaptive delay,
- interpolate root and tracker positions,
- slerp tracker rotations when available,
- extrapolate only for a short bounded window,
- mark stale avatars for lower LOD or fade-out,
- keep previous output for motion vectors and temporal effects.

### 8.7 IK And Animation

The avatar module should support multiple solve levels:

| Level | Use case | Work performed |
| --- | --- | --- |
| Full local | local player | full VR input, calibration, IK, hands/face, prediction |
| Full remote | nearby interacting avatars | interpolated trackers, full IK, facial/hand channels |
| Reduced remote | visible mid-range avatars | upper-body or six-point IK, reduced finger/face |
| Animation proxy | distant avatars | root motion plus animation state/pose seed |
| Impostor/crowd | very far or dense crowd | no skeletal solve; render representation only |

The ECS system should choose solve level from avatar relevance and render LOD data. It should not ask every avatar component to decide independently.

High-volume solve levels should be job-parallel. Full local and nearby remote avatars get priority; reduced, proxy, and crowd solves fill whatever worker budget remains. Avatar systems should expose enough counters for users to see whether they are CPU-bound, budget-limited, or LOD-limited.

### 8.8 Render Handoff

Avatar ECS output should feed rendering through dense render-facing buffers:

- root transform buffer,
- previous root transform buffer,
- skin-palette source ranges,
- avatar render instance table,
- LOD/representation table,
- animation/impostor frame data,
- material customization ids.

For full and reduced skeletal avatars, ECS/animation/IK writes final local/world pose data and the renderer consumes the normal skinning path. For far avatars, ECS should select lower-cost representations that align with the avatar optimizer:

- optimized LOD meshes,
- reduced bone palettes,
- rigid or near-rigid skinning fallback,
- octahedral impostors,
- animated Gaussian clips when that pipeline exists,
- crowd animation seeds.

Thousands of represented players require these lower-cost representations. Thousands of full IK and full skinning avatars every frame is not a realistic v1 target.

## 9. Network LOD And Interest Management

Avatar replication LOD must be per recipient. A player may be full fidelity for one nearby client and low fidelity or silent for another.

Suggested replication tiers:

| Tier | Frequency | Payload | Intended use |
| --- | --- | --- | --- |
| LOD0 | 30-90 Hz | root, tracker positions/rotations, hands/face where enabled | nearby interaction, local mirrors, combat/social focus |
| LOD1 | 15-30 Hz | root, head, hands, feet, hips, coarse rotations | nearby visible avatars |
| LOD2 | 5-10 Hz | root, velocity, head yaw, animation state | mid/far visible avatars |
| LOD3 | 1-5 Hz | root sector, movement mode, animation seed | crowd/background avatars |
| Silent | 0 Hz | none until relevant again | outside AOI or over budget |

The server should compute per-connection relevance from:

- distance to recipient,
- camera/frustum or approximate facing,
- party/group membership,
- voice/chat proximity,
- active interaction,
- gameplay importance,
- recent damage/grab/contact,
- moderation/spectator policy,
- available byte budget.

The replication builder should pack highest-priority avatars first, then degrade or skip lower-priority avatars when the token bucket is exhausted.

## 10. Snapshot And Delta Strategy

Generic ECS replication should treat every channel as baseline plus delta:

```text
Baseline:
    full state needed to decode future deltas

Delta:
    changed fields against known baseline

Keyframe:
    periodic baseline refresh or recovery packet
```

For humanoid pose:

- baseline includes root sector/local pose and all active tracker values,
- delta includes changed root axes, changed yaw, changed tracker axes, and optional side channels,
- sequence numbers are per source or per connection depending on final loss/reorder design,
- server-stamped tick/time is authoritative after rebroadcast.

The generic ECS backend should provide baseline cursors and dirty-generation checks, but avatar code owns quantization and payload semantics.

## 11. Data Flow

### 11.1 Local Client

```text
VR devices/controllers
    -> VrDeviceSamplingSystem
    -> AvatarTrackerPose
    -> AvatarPredictionSystem
    -> AvatarIKSystem
    -> AvatarRenderPrepareSystem
    -> GPU upload/render
    -> AvatarPoseReplicationBuildSystem
    -> BaseNetworkingManager
```

### 11.2 Server

```text
BaseNetworkingManager
    -> AvatarPoseReplicationReceiveSystem
    -> AuthorityLeaseSystem
    -> AvatarServerValidationSystem
    -> Authoritative AvatarTrackerPose
    -> ConnectionInterestSystem
    -> AvatarPoseReplicationBuildSystem
    -> per-connection outbound queues
```

### 11.3 Remote Client

```text
BaseNetworkingManager
    -> AvatarPoseReplicationReceiveSystem
    -> AvatarRemoteInterpolation
    -> AvatarInterpolationSystem
    -> AvatarIK/Animation LOD systems
    -> AvatarRenderPrepareSystem
    -> GPU upload/render
```

## 12. Reusable Feature Modules Beyond Avatars

The ECS backend should be designed so these future features can reuse it:

- networked interactables and ownership leases,
- projectiles and hit validation,
- NPC crowds,
- physics broadphase proxies,
- audio emitters and voice proximity,
- particle/gameplay emitters,
- editor selection/transform tool runtime caches,
- texture and geometry streaming feedback entities,
- GI probes and dynamic light relevance,
- server-side moderation recording,
- replay/ghost playback,
- render instance staging for static and dynamic meshes.

The ECS core should not contain avatar names, humanoid enums, network DTOs, render backend types, or VR device references. Those belong to modules.

## 13. Implementation Phases

### Phase 0 - Contracts And Tests

- Define `RuntimeEntity`, `RuntimeEntityWorld`, `ComponentStore<T>`, and system phase contracts.
- Define the ECS parallel range scheduler surface and worker-budget policy.
- Add deterministic unit tests for create/destroy, generation safety, sparse-set iteration, dirty ranges, and external id lookup.
- Add deterministic unit tests for range partitioning, fence ordering, worker-budget clamping, and no-overlap writes.
- Keep all code independent from rendering, networking, animation, and editor assemblies.

Acceptance:

- A headless test can create 100,000 entities with simple components, mutate dirty ranges, destroy/reuse entities, and verify no stale handle access succeeds.
- A headless test can process a component store through parallel ranges using the configured worker cap and produce the same result as the single-thread path.

### Phase 1 - Avatar ECS Store

- Add avatar ECS components for identity, authority, tracker pose, replication state, interpolation, rig binding, and render instance selection.
- Add bridge components that register existing player/humanoid scene nodes into ECS.
- Keep existing `VRIKSolverComponent` behavior working, but allow ECS to own pose receive/send when enabled.

Acceptance:

- One local avatar and one remote avatar can be represented in ECS without changing existing networking behavior.

### Phase 2 - Batched Pose Networking

- Move `HumanoidPoseFrame` parse/build out of individual `VRIKSolverComponent` instances and into ECS systems.
- Batch multiple avatars into one frame where appropriate.
- Validate `NetworkAuthorityLease` before accepting incoming pose state.
- Register AOT/serialization coverage for any new realtime DTOs.

Acceptance:

- Existing networking pose smoke tasks still pass.
- A synthetic test with hundreds of avatars builds and parses pose frames without per-avatar allocations in the hot loop.

### Phase 3 - Interpolation, AOI, And Replication LOD

- Add per-connection interest lists and avatar replication tier selection.
- Add remote interpolation buffers.
- Add adaptive update rates and budget-based shedding.
- Add diagnostics for skipped, degraded, stale, and over-budget avatars.

Acceptance:

- Two clients at different positions receive different avatar LOD sets from the same server tick.
- Budget exhaustion degrades lower-priority avatars before dropping nearby/interacting avatars.

### Phase 4 - IK And Render Handoff

- Add ECS-driven avatar IK/animation solve levels.
- Batch IK/interpolation/render-prepare work across the available ECS worker budget.
- Publish render-facing buffers for skinning, LOD selection, and avatar representations.
- Integrate with existing GPU skinning and avatar optimization outputs.

Acceptance:

- A scene with hundreds of mixed-LOD avatars renders from ECS state without updating a full scene-node hierarchy for every remote avatar each frame.
- Increasing the configured ECS/avatar worker budget increases completed avatar update throughput until another bottleneck dominates.

### Phase 5 - Stress And Production Hardening

- Add Release stress tests for 100, 500, and 1000 represented avatars.
- Measure CPU update, network encode/decode, interpolation, IK, GPU upload, and render submission separately.
- Add profiler counters for ECS entity count, dirty ranges, avatar LOD counts, pose bytes, and dropped/degraded updates.
- Add profiler counters for ECS worker budget, scheduled ranges, completed ranges, queued/wait time, avatar CPU time by solve tier, and budget-driven degradation.

Acceptance:

- A 1000-avatar stress scene can run with explicit LOD distribution and no silent CPU fallback from intended accelerated paths.
- Stress runs demonstrate conservative, half-core, and high-core worker budgets with clear CPU scaling and no local-player latency regression.

## 14. Diagnostics And Tooling

The ECS backend should expose:

- entity count by component type,
- system timings,
- worker budget, range count, and worker wait time by ECS phase,
- per-system allocation counters in debug/profiling builds,
- dirty range counts,
- structural change counts,
- external lookup hit/miss counters,
- packet encode/decode bytes and timings,
- avatar LOD distribution,
- avatar update CPU time by solve tier,
- budget-driven avatar degradation/skips,
- authority rejection reasons,
- interpolation stale/extrapolated counts,
- GPU upload bytes by component stream.

Editor tooling can show this through ImGui first. Native UI can follow later.

## 15. Risks

### ECS Becomes A Second Engine

Risk: ECS grows into a parallel scene graph with duplicate concepts.

Mitigation: keep the core tiny. Feature modules own semantics. Scene bridges are explicit.

### Over-Generic Design Blocks Avatar Progress

Risk: too much abstraction delays the avatar use case.

Mitigation: implement the generic backend only to the level needed by avatar networking first, but keep names, dependencies, and storage contracts reusable.

### Component Bridge Costs Hide The Win

Risk: ECS updates dense arrays but then writes every result back into `SceneNode` hierarchies.

Mitigation: only write back for local/near/debug entities. Render and network should consume ECS data directly.

### Parallel Jobs Starve The Rest Of The Engine

Risk: avatar ECS work consumes every worker and causes IO, streaming, editor, or gameplay jobs to wait too long.

Mitigation: clamp ECS to the effective job worker count, reserve workers by default, expose user/project caps, keep local-player work high priority, and degrade optional avatar tiers when wait time or frame time exceeds budget.

### Too Many Tiny Jobs Create Scheduler Overhead

Risk: scheduling per-avatar jobs costs more than the work itself.

Mitigation: schedule coarse dense ranges, enforce minimum batch sizes, and cap range count per system phase.

### Networking LOD Causes Social Artifacts

Risk: low update tiers make users look robotic or stale.

Mitigation: preserve high fidelity for nearby/interacting/voice-relevant avatars, add hysteresis, and use animation proxy states instead of frozen poses for far avatars.

### Server Validation Is Too Weak

Risk: client-driven full-body trackers become a cheating or griefing vector.

Mitigation: validate authority, plausible speed, tracker reach, body proportions, interaction proximity, and lease ownership server-side. Log explicit rejection reasons.

## 16. Open Questions

- Should the first ECS storage backend be sparse-set only, or should we start with archetype chunks for multi-component query locality?
- Should avatar pose packet sequence state be per source avatar, per connection, or both?
- What is the first versioned extension beyond six tracker positions: rotations, velocities, or tracker confidence?
- Should server-side lag compensation store tracker poses only, solved bones for selected gameplay channels, or both?
- How much scene-node writeback is needed for editor debugging of remote avatars?
- Should GPU-driven animation consume ECS components directly or through a renderer-owned animation bridge?
- Should the first implementation add a dedicated low-overhead `ScheduleRange` path to `JobManager`, or layer range batches over existing `ActionJob`/`EnumeratorJob` scheduling first?
- What default ECS worker reservation keeps editor/VR responsiveness healthy on 4, 8, 16, and 32 core machines?

## 17. Extension Checklist

When adding a new ECS-backed feature:

- Keep component structs small and data-only.
- Use stable external ids only at registration and lookup boundaries.
- Add system phase and dependency metadata.
- Add worker-budget metadata for high-volume systems.
- Add dirty-range reporting if the component can replicate or upload to GPU.
- Add deterministic tests for storage and serialization.
- Avoid managed allocations in per-frame systems.
- Avoid LINQ, captured closures, and object graph traversal in hot systems.
- Partition writable stores into disjoint ranges before running parallel workers.
- Expose diagnostics before adding complex fallback behavior.
- Keep feature semantics out of the ECS core.

When extending humanoid avatar networking:

- Preserve session and `NetworkEntityId` scoping.
- Validate authority leases on the server.
- Version payloads instead of overloading fields.
- Add serialization/AOT tests for new DTOs.
- Add bandwidth and LOD diagnostics.
- Keep full-fidelity local/nearby avatars prioritized.
- Do not replicate final bone matrices unless a specific feature truly needs them.
