# Editor Memory Retention Across Play Round Trips

**Status:** Retention chains fixed and measured on desktop; OpenXR fixed costs
and steady-state churn remain open (see [Remaining work](#remaining-work)).

## Problem

With OpenXR active the editor reached 28.8 GB private bytes and a 20.3 GB
working set after a few Play round trips; the user reported 27–30 GB. The target
is at most 8 GB, ideally 1–2 GB. Desktop-only sessions grew by about 1.5 GB of
private memory per round trip without levelling off.

## How it was measured

- Fixture: default unit-test world, Release, Vulkan Advanced pipeline,
  `XRE_FORCE_MESH_SUBMISSION_STRATEGY=CpuDirect`, isolated MCP sessions.
- Round trips through `enter_play_mode`/`exit_play_mode`, sampling private
  bytes, working set and GC counters 10 s after each transition.
- Full dumps analysed with `dumpheap -stat -live` (reachable objects only),
  `gcroot` on objects from destroyed Play copies, and all-minus-live type
  differences for garbage. `dotnet-gcdump` was not used for counts because it
  caps at 10 million objects.
- Process address space summarized with `VirtualQueryEx` by protection, to
  separate the GC heap, other native heaps and write-combined mapped memory.

`GC.CollectionCount` read through `invoke_method` is the reliable collection
count; the `dotnet.gc.collections` counter reported zero while collections ran.

## Retention chains found and fixed

Each destroyed Play copy stayed reachable through one of these paths. Fixes are
listed in the order the dumps exposed them.

| Path | Fix |
| --- | --- |
| BVH snapshots, masked and reference result buffers kept item references beyond their counts | Clear stale slots on reset, clone and frame start; superseded unread snapshots release their items |
| `GenericRenderObject` destroy never reached the Vulkan renderer's object cache (wrapper owner is the backend context) | Destroy removes the object from every window renderer's cache |
| `RenderableMesh` subscriptions on source LODs, never removed | Recorded and unsubscribed on dispose |
| `RenderableComponent` and `Model` owned event lists that were never destroyed | Destroyed with their owners; models created by a restore are recorded in `SnapshotRestoredContent` |
| `VkRenderProgram` cross-frame binding artifacts keyed by strong `(XRMaterial, XRMeshRenderer)` references; programs outlive worlds | Each `VkMeshRenderer` records the programs it published into and removes its slots on unlink/delete |
| `EditorFlyingCameraPawnComponent` kept the last Advanced pick result (render info of a destroyed copy) in a write-only field | Field removed |
| `SkyboxComponent` destroyed its mesh but not its renderer or material; material rebuilds replaced materials without destroying them | Renderer and material destroyed on component destruction; replaced material destroyed after the renderer switches |
| Every `EventList` registered in the global `XRObjectBase` cache, which holds strong references until destroy, so any owner that did not destroy its lists leaked them and everything their handlers reach | `XRObjectBase.ParticipatesInObjectCache`; event lists opt out (they are never resolved by ID) |
| Embedded sub-assets kept `SourceAsset` pointing at a destroyed root, or at a root that replaced them (a material's default `RenderingParameters`), while staying in the global cache | Destroying an asset detaches its embedded assets; an asset graph refresh detaches assets it no longer reaches |

After these fixes the Play copy, its GPU scene database (8 → 3 live: the 3D and
2D scenes plus one transient), its skyboxes (7 → 2: the live world and the
original copy kept by design) and its model components are released.

## Fixed costs reduced

- **Descriptor publication scratch.** Every `VkMeshRenderer` owned nine
  1024-entry scratch columns (about 380 KB) and every `VkMaterial` kept at least
  one similar workspace in a per-material pool, about 460 MB for ~1,200
  renderers. Both now rent from a process-wide pool bounded by concurrent
  publications.
- **Collection after Play exit.** Background collections neither compact nor
  release a discarded world copy, so committed memory climbed even when live
  data did not. Exiting play mode now queues one LOH-compacting maintenance
  collection as an app-thread job between render frames.

## Measurements

Desktop, three round trips (private / working set / GC committed, GB):

| Build | Startup | After 3 trips | Live heap after 3 trips |
| --- | --- | --- | --- |
| Before retention fixes | 6.80 / 5.13 / 2.99 | 11.40 / 9.20 / 6.39 | 3.44 (after 5 trips) |
| All fixes | 5.98 / 4.34 / 2.26 | 8.79 / 6.93 / 3.84 | 2.22 |

With all fixes the fourth trip adds nothing (9.17 → 9.18 GB private in the
four-trip run with the exit collection). Live heap at startup is 1.98 GB.

OpenXR (2688×2688 swapchain per eye):

| Build | Private | Working set |
| --- | --- | --- |
| Before, after earlier trips | 28.8 | 20.3 |
| All fixes, startup | 17.5 | 7.7 |
| All fixes, 3 trips | 19.2–24.0 (varies with sample timing) | 10.7–13.9 |

## Remaining work

Ordered by measured size.

1. **OpenXR device-local memory (7.4 GB, mirrored into the process).** On this
   NVIDIA driver, device-local Vulkan allocations also appear in the process as
   write-combined private memory, so they count toward Task Manager's figure.
   VMA device-local usage with user settings was 12.43 GB, because the
   resource planner held the stereo pipeline's 2.3 GB target set three times.
   After the planner fixes it is 7.41 GB: one stereo set (2.3 GB), the desktop
   set (0.6 GB), 4K avatar textures (about 1.3 GB) and the rest. See the
   [stereo flicker and duplication record](2026-10-04-openxr-stereo-flicker-and-target-duplication.md).
   Transient aliasing is disabled for every planner target, so 38 stereo
   targets at 2688² each own memory.
2. **CPU-side mesh vertices (about 2.5 GB managed).** A live-object census of
   the user configuration (5.9 GB live of an 8.5 GB heap, 25.8 M objects) is
   led by `XRMesh.Vertices`. It holds 1.79 M `Vertex` objects, each with its own
   bone-weight `Dictionary<TransformBase, …>`, texture-coordinate and colour
   `List`s, and per-vertex blendshape `VertexData` (6.15 M objects). That is
   about 1.4 KB per vertex. Roots run through live meshes (for example Advanced
   managed deformation source rows), so this is retained by design rather than
   leaked. About 34 runtime sites read `Vertices`. Replace the per-vertex object
   model with packed arrays, or release it after GPU upload where no CPU
   consumer remains.
3. **Fixed-capacity pipeline arrays (about 1.2 GB managed).** These are stable-bin
   records and headers (314 MB over 21 streams), Advanced visibility operation
   payloads (233 MB), deformed vertices (244 MB), blendshape sparse records
   (223 MB), meshlet vertices (124 MB) and skin influences (99 MB). See item 7.
4. **CPU-resident texture data (about 1.2 GB).** Texture streaming reports
   1.17 GB of managed texture bytes for 65 textures after upload.
5. **Generated uber shader variants are duplicated by the Play snapshot.**
   Generated variants have no file path, so the snapshot inlines each material's
   ~452K-character source. After three trips 814 such strings (~0.5 GB) were live
   with only 112 distinct contents; every trip also discards ~0.6 GB of them on
   the large object heap. Restored materials also reload `UberShader.frag` from
   disk to recover their canonical shader. Planned fix: on restore, replace a
   deserialized generated variant with the live cached instance when its variant
   hash, source path and text match, so materials share one shader and program
   as before the trip.
6. **Per-frame binding snapshots in XR.** Idle OpenXR allocates ~54 MB/s; the
   garbage is dominated by `ComputeDispatchSnapshot` captures (each with about
   ten dictionaries) and uber material property lookups that allocate closures.
   Background gen2 runs about every 4 s. These are hot-path allocations to remove.
7. **Stable-bin and visibility capacity.** Each frame plan and mesh ingress
   preallocates a stable-bin stream for `VulkanMeshOperationRequestQueue.Capacity`
   (about 4,096) operations: ~330 MB of record/header arrays plus plan objects
   across 21 streams, and 163 MB of visibility operation payloads. Grow these to
   demand instead of the maximum.
8. **Upload arena high-water.** The Advanced upload arena grows to a power of two
   per stream and never shrinks; full-scene uploads during Play transitions set
   the high-water mark (~190 MB of `byte[]`).
9. **Smaller per-trip leaks.** About 900 empty `XRMesh` objects per trip (no
   name, no vertices, non-persistent ID) stay in the object cache with nothing
   else referencing them; the creator has not been identified. `XRObjectBase`
   has a finalizer, so every dead engine object survives one extra collection
   and promotes its graph.
10. **Original world copy.** By design the world as loaded before the first entry
   is never destroyed (it can share objects with import caches), so one extra
   copy of the scene stays alive after the first round trip.

## Follow-up implementation (2026-10-05)

The remaining work above is implemented as the
[editor memory reduction TODO](../../todo/rendering/optimization/editor-memory-reduction-todo.md),
which records each change with its measurement. Same OpenXR user
configuration, steady state:

| Measure | 2026-10-04 after fixes | 2026-10-05 |
| --- | --- | --- |
| Device-local (VMA) | 7.41 GB | 5.52 GB |
| Private bytes (commit), natural | 24.7 GB | 13.5 GB |
| Private bytes after a full GC | — | 13.2 GB |
| Private minus device-local | about 17 GB | 7.7–8.0 GB |
| Live managed estimate | 5.9 GB | 1.6 GB |
| GC committed | 8.4 GB | 3.3 GB |
| Allocation rate at idle | 54 MB/s | 48 MB/s |
| OpenXR submit / missed per second | 30 / 4.6 | 32.3 / 4.7 |

Desktop Play round trips with the avatar (commit / device-local / live managed,
GB): 10.3 / 3.1 / 1.9 before the first entry, 16.1 / 5.2 / 2.2 after it, and
flat after the second and third (16.0–16.9 / 5.2 / 2.2–2.5). The first-trip
step is the original world copy (item 10); before the deformation-generation
fix below, the second trip reached 20.4 / 6.4 / 4.3.

Findings made along the way:

- **Texture decode loop.** Two avatar textures never became resident and were
  decoded about twice a second for the whole session (100-370 MB/s of large
  object heap allocation, a gen2 GC every 3 s). A texture without its preview
  alternated between a 64-pixel preview load and a 1-pixel load created by the
  budget fit of a "hold" decision, each canceling the other under flickering
  visibility. Fixed in `ImportedTextureStreamingManager`.
- **Allocator-budget capability missing.** No renderer implemented
  `IVulkanAllocatorStreamingBackendCapability`, so the streaming
  allocator-pressure check never ran. `VulkanRenderer` implements it now.
- **`RuntimeEngine.UserSettings` is never filled.** It is a separate instance
  from the host's user settings; the pipelines' GI mode reads it. Not fixed;
  texture quality uses the host settings service instead.
- **Play entry is slow.** Capturing the snapshot takes about six minutes with
  the avatar on the previous commit as well: every cooked mesh stream of
  256 KiB or more is LZMA-compressed, including in-memory snapshots.
- **Play with the avatar failed before deserializing the scene.** On the
  previous commit restore stopped at "Meshlet payload does not belong to the
  supplied source mesh" and skipped the scene. Two more defects followed:
  - Import placeholders for textures whose source file is missing on this
    machine were captured as references that cannot resolve. The snapshot now
    writes them by value.
  - The cooked mesh reader rejected meshes with more than eight color channels.
    One avatar mesh carries 23 FBX color layers. The bound is now 64 on both
    sides, and the writer verifies its calculated sizes.
- **Deformation generations outlived their scene.** Each Play transition
  recreates the world's GPU scene. The four pooled static deformation
  generations kept transition-peak capacity, their GPU copies and the old
  scene's meshes until reassigned: about 4.5 GB of commit after two round trips.
  Generations of destroyed scenes are now freed.
- **Unused CPU deformation arena.** `AdvancedDeformedVertexArena` kept
  3 × 128 MB of pinned CPU vertex storage that no runtime path reads; it is now
  allocated on first CPU access.
- **Copy-on-write spill views are charged to commit.** A 512 MB copy-on-write
  file view raised `PrivateUsage` by 513 MB at map time (a read-only view: about
  1 MB). Spilled mesh buffers therefore leave the private working set but not
  commit.
- **`restart_renderer` leaves the 3D scene undrawn.** Viewport captures after a
  restart are black on this build and on the previous commit; the composited
  window shows the editor UI but only 14 draw calls. Texture rehydration
  reported 53 reloads from source and no failures.
- **Remaining large items.**
  - The original world copy after the first Play entry: +2.1 GB device-local
    and +2 GB native buffers.
  - Canonical geometry: after three round trips, three 109 MB `StaticVertices`
    arena arrays are live. Each holds the avatar's full packed vertices and is
    referenced by publication snapshots (32 live
    `AdvancedGeometryPublicationSnapshot` objects); the pre-skinned arenas are
    empty. Whether the three are separate scene databases or superseded arena
    generations kept by publication rings is not yet established.
  - `XRMesh.Triangles`: 2.56 M `IndexTriangle` objects, each with a
    `List<int>`, about 300 MB for two world copies.

## Ruled out

- The editor runs `EditorInteractive` (Interactive latency), not
  `SustainedLowLatency`; the VR latency profile is not the cause of gen2 growth.
- The noisy pink ground in one desktop capture is the randomly selected
  `satara_night_4k` environment map under auto-exposure, not corruption.

## Related observation

With the `klippad_sunrise_2_4k` environment map, the sky above the horizon
renders black on desktop. This predates the changes above (a capture from
before them shows it) and is not investigated here.

## Validation

- Release builds: zero warnings and errors.
- Desktop round trips keep the engine timer running with no terminal fault;
  edit, Play and post-exit captures render the scene and skybox.
- Unit tests: object cache, event list, asset serialization, prefab, scene node
  and mesh renderer suites pass (129/129). A wider filter (460 tests, adding
  descriptor, skybox, play-mode and uber suites) fails 102, almost all source
  contracts. The failures touching edited files were checked: their asserted
  strings are absent at `HEAD` as well (play-mode exit, skybox ambient), the
  workspace path is ambiguous because of a nested worktree, or they fail
  identically with the data-layer patch reversed (four uber-variant runtime
  tests). No tests were added or modified.
