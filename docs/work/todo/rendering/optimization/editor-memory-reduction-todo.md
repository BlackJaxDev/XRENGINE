# Editor Memory Reduction TODO

Status: Active. Last reconciled: 2026-10-05.

Goal: the editor with OpenXR, user settings and avatars loaded uses at most
8 GB, ideally 1–2 GB of process memory outside GPU memory. Work the sections in
order. Each item lists the evidence, the change, the expected saving and how to
validate it. Record measured results next to the item when it is checked.

Related records:

- [Editor memory retention across Play round trips](../../../investigations/rendering/2026-10-04-editor-memory-retention.md)
- [OpenXR stereo flicker and render-target duplication](../../../investigations/rendering/2026-10-04-openxr-stereo-flicker-and-target-duplication.md)
- [Vulkan stall remediation TODO](../vulkan-stall-remediation-todo.md) (OpenXR 8 GB ceiling item)
- [Texture runtime, streaming and virtual texturing design](../../../design/texturing/texture-runtime-streaming-virtual-texturing-design.md)
- [Model import binary cache TODO](../../assets/model-import-binary-cache-todo.md)
- [Blendshape compression and GPU efficiency TODO](../gpu/blendshape-compression-and-gpu-efficiency-todo.md)

## Baseline (2026-10-04, user settings, OpenXR stereo 2688² per eye)

| Measure | Value |
| --- | --- |
| Process private bytes (settled) | 24.7 GB |
| Process working set (Task Manager "Memory") | 17.4 GB |
| VMA device-local usage | 7.41 GB |
| .NET GC heap / committed / fragmented | 8.55 / 9.28 / 1.05 GB |
| Live managed objects | 5.9 GB in 25.8 M objects |
| CPU texture pixel copies (native) | ~1.17 GB |

On this NVIDIA driver, device-local Vulkan allocations are also mapped into the
process as write-combined private memory. They count toward private bytes, the
working set and full dump size. Report GPU memory (VMA device-local) separately
from process memory excluding it.

Largest live managed types:

| Type | Size | Objects | Owner |
| --- | --- | --- | --- |
| Per-vertex mesh model (`Vertex`, `VertexData`, weight dictionaries, lists) | ~2.5 GB | ~20 M | `XRMesh.Vertices` |
| `byte[]` | 629 MB | 15 K | mixed |
| `AdvancedDeformedVertex[]` | 244 MB | 107 | deformation payloads and generation mirrors |
| `VulkanAdvancedVisibilityOperationPayload[]` | 233 MB | 10 | frame-plan streams |
| `AdvancedBlendshapeSparseRecord[]` | 223 MB | 106 | deformation payloads |
| Stable-bin records and headers | 314 MB | 42 | stable-bin streams |
| `MeshletVertex[]` | 124 MB | 111 | meshlet payloads |
| `AdvancedSkinInfluence[]` | 99 MB | 3 | deformation generations |

## Current state (2026-10-05, same configuration)

| Measure | Baseline | Now |
| --- | --- | --- |
| Process private bytes (commit), settled | 24.7 GB | 13.5 GB (13.2 GB after a full GC) |
| Private bytes minus device-local | about 17 GB | 7.7–8.0 GB |
| VMA device-local usage | 7.41 GB | 5.52 GB |
| .NET GC heap / committed / fragmented (after a full GC) | 8.55 / 9.28 / 1.05 GB | 2.94 / 3.26 / 1.37 GB |
| Live managed objects | 5.9 GB | 1.6 GB |
| Native `DataSource` memory | not measured | 1.18 GB |
| Allocation rate at idle | 54 MB/s | 48 MB/s |
| OpenXR frames submitted / missed per second | 30 / 4.6 | 32.3 / 4.7 |

## Open decisions

Each open item below waits on one of these decisions. Sparse residency is not
listed: the texture runtime design already defers it.

### D1. Which BCn encoder to add

Unblocks **BCn payloads** (section 5).

- **Need.** A BC7 (colour and masks), BC5 (normals) and BC4 encoder for the
  texture binary cache. BC7 stores 1 byte per pixel against 4 for RGBA8.
  Streamed textures took 1.17 GB of GPU memory under the old residency policy,
  which would become about 0.3 GB.
- **To decide:**
  - which library to use;
  - whether to use a native or a managed encoder;
  - whether textures are encoded at import or by a background cache job.
- **Constraints.** The licence must allow both the Community Source License and
  commercial distribution (see `LEGAL/README.md`). After adding the dependency,
  run `Tools/Reports/Generate-Dependencies.ps1`.

### D2. Published cook format for large mesh streams

Unblocks **Mapped cooked data** and **Upload without processing** (section 3).

Today every mesh stream of 256 KiB or more is cooked as LZMA. Nothing large can
be mapped from the archive, and every load decodes on the CPU into private
memory. The options:

| Option | Archive size | Load path | Cost |
| --- | --- | --- | --- |
| LZMA (current) | smallest | CPU decode into private memory | none |
| Raw | grows by the compression ratio | map archive ranges directly, no private copy | larger downloads and installs |
| GDeflate | between the two | GPU decompression on upload (`VK_NV_memory_decompression`) | needs a CPU decode path for OpenGL, other GPUs and CPU readers |

To decide: the format, and whether OpenGL and non-NVIDIA GPUs may take a CPU
decode for GDeflate. Both choices are cook-format changes and need a recook.

### D3. Read-only spill views, to lower commit as well

Follow-up to **Client-copy policy** (section 3).

- **Today.** Spilled mesh buffers live in copy-on-write file views. Windows
  charges those views to commit in full, so about 274 MB at steady state (548 MB
  with the original world copy after a Play entry) still counts in private
  bytes, although the pages are no longer in the private working set.
- **Fix.** Read-only views carry no commit charge, but every writer must
  convert the copy back to private memory before writing.
- **Risk.** About 240 call sites reach client bytes through `ClientSideSource`
  or `Address` without separating reads from writes. A writer that is missed
  faults with an access violation.
- **To decide:** route every raw writer through the `XRDataBuffer` write model
  and then switch to read-only views, or keep copy-on-write views and accept the
  commit charge.

### D4. GC conserve-memory default

Follow-up to **Fragmentation** (section 7).

- **Measured.** `DOTNET_GCConserveMemory=5` saved about 0.5 GB of GC committed
  memory, with no measurable frame-rate cost in one sample of each setting.
- **Risk.** Conserve mode chooses blocking compactions when fragmentation is
  high. The worst single gen2 pause in VR was not measured.
- **To decide:** one of
  - enable `System.GC.ConserveMemory` in the editor project;
  - keep it as an opt-in environment variable (current);
  - measure the maximum gen2 pause in OpenXR first.

### D5. Separate projects for transient aliasing and the planner commit fix

Covers **Transient aliasing** and **Planner follow-ups** (section 6).

- **Transient aliasing.** Only scaffolding exists, so this is multi-week,
  high-risk work: lifetime-aware grouping, alias handoff barriers, discard on
  first use and an audit of out-of-graph lookups. The stereo target set stays
  at its full size until it is done.
- **Planner commit.** A commit inside the OpenXR planner scope still forces one
  stereo set reallocation and about 2.5 s of black at startup. The earlier
  attempt broke plan sealing.
- **To decide:** whether each gets its own TODO, and its priority against other
  rendering work.

### D6. Play-mode memory and time outside this TODO

Found while validating Play round trips. Neither cost is covered by an item
here.

- **Original world copy.** After the first Play entry, the world as loaded
  stays alive for the rest of the session: about 2.1 GB of device-local memory
  and 2 GB of native buffers with the avatar. This is by design, because that
  world can share objects with import caches and engine defaults
  ([play-mode architecture](../../../../architecture/editor/play-mode-architecture.md)).
  Releasing it needs
  ownership tracking for those shared objects.
- **Snapshot capture time.** Entering Play takes about 6.5 minutes with the
  avatar, on the previous commit as well. The capture LZMA-compresses every mesh
  stream of 256 KiB or more. An uncompressed in-memory snapshot would be faster
  but larger while Play runs.
- **To decide:** whether to address either cost, and in which TODO.

## Measurement method

Use these for every item. Keep raw evidence under `Build/_AgentValidation/`; put
results in this document.

- **Session.** An isolated MCP session with user settings and OpenXR
  (`Tools/Manage-McpEditorSession.ps1`, session environment with
  `XRE_UNIT_TEST_WORLD_SETTINGS_PATH` and `XRE_UNIT_TEST_VR_MODE=OpenXR`).
  Measure once private bytes have been stable for 20 s.
- **GPU memory.** MCP `get_vulkan_memory_statistics` (VMA heaps) and
  `get_vulkan_resource_planner_states` (render-target sets per planner state).
  Use `list_vulkan_image_allocation_diagnostics` for individual images.
- **Managed heap.** `invoke_method System.GC.GetGCMemoryInfo` for totals.
  - For a per-type census, use a full dump with `dumpheap -stat -live`.
  - A full dump is as large as private bytes (about 26 GB at baseline), so write
    it to a drive with space and delete it after the census.
  - `dotnet-gcdump` truncates at 10 M objects and is unusable at baseline.
- **Textures.** MCP `get_texture_streaming_summary`. Its `current_managed_bytes`
  is the estimated GPU size of streamed textures, not CPU memory.
- **Regression gates.** No black frames in a 15 s SteamVR VR View capture. No
  per-frame allocation increase (allocated bytes per second at idle). Frame time
  unchanged within noise.

## Rules for this work

- No per-frame heap allocations. Storage may only grow at cold provisioning
  points (frame-slot provisioning, visibility-family provisioning, mesh
  preparation within its budget). Sealed frames never grow storage.
- Do not silently fall back to a CPU path. A missing CPU copy must be reported,
  or reloaded asynchronously, not replaced by a different code path.
- Ask before adding dependencies (BC encoder) and run the dependency report
  afterwards.
- Every release of CPU data needs a defined reload source: cooked archive, cache
  file or original source.

## 1. Capacity-sized renderer arrays (~1 GB managed, low risk)

Most of these arrays are sized for worst-case capacity, created per frame-plan
slot, and never shrink. One `FramePlanBuilder` per renderer holds the active
slots plus four retired spares, and each slot builds three operation streams and
a frame plan. Paths are under
`XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/` unless they
say otherwise.

**Result so far (2026-10-04).** Measured with a forced full GC on the same
OpenXR user configuration, before and after the changes below:

| Measure | Before | After |
| --- | --- | --- |
| Live managed estimate | 5.44 GB | 4.84 GB |
| GC committed | 8.44 GB | 5.83 GB |
| Heap after full GC | 7.43 GB | 5.79 GB |

Private bytes vary by a couple of GB between runs and are not a reliable
per-item measure. VR View showed no dark frames in 15 s, no end-frame failures,
and the render-failure log had no capacity errors (only startup readiness
failures, the same kinds as before).

- [x] **Advanced visibility operation payload capacity.** `FrameOperationPayloads`
  allocated `VulkanAdvancedVisibilityOperationPayload` (about 11 KB each) and its
  late and native-compute closure storage at the general capacity (2048) per
  main-scene store. Scene lanes now start at 64 rows
  (`AdvancedVisibilityInitialCapacity`) and grow on a new high-water mark, up to
  the general capacity (`EnsureAdvancedVisibilityCapacity`).
  - Growth happens while the plan is built, before sealing.
  - A slot is rebuilt only after its previous frame retired.
  - Closure objects already created are carried over.
- [x] **Per-opcode payload capacities.** Every opcode column now starts at 64
  rows (1,024 for mesh draws) and grows on a new high-water mark. The declared
  general, mesh and texture capacities remain the admission maximum (`Ensure`).
  The per-frame loops over the payload arrays, such as
  `ReleaseReadOnlyStorageBindings`, now walk the small arrays.
- [x] **Advanced visibility inputs.** Fixed slot storage
  (`VulkanAdvancedVisibilityInputStorage`) starts at 4,096 rows per column
  (`InitialFixedRowCapacity`) and grows up to the declared maximum (65,536 draws
  and ranges); exceeding it still rejects the frame. Lease storage was already
  demand-sized.
- [x] **Stable-bin streams.** `VulkanPreparedStableBinStream` creates no
  `VulkanSealedBinSubmissionPlan` objects (86,016 at baseline, 53 MB), atlas
  manifests or manifest views up front; they are created per row on first use
  (`RentSealScratchPlan`, `VisibilityAtlasManifest`, `VisibilityManifestView`).
  Records, headers and the other row columns start at 256 rows
  (`InitialRowCapacity`) and grow together on a new high-water mark
  (`EnsureRowCapacity`, `EnsurePayloadCapacity`, `EnsureLateResourceUseCapacity`)
  while a plan is prepared, never after it is frozen, up to the previous
  capacity (`VulkanMeshOperationRequestQueue.Capacity`). Growth rebinds the
  stream-owned manifests to the new resource slabs
  (`VulkanBinResourceManifest.RebindStreamSlabs`). The exception stream, sealed
  exception snapshots and the CPU indirect parity artifact are demand-sized.
- [x] **Deformation imported payloads.** After a commit adopts an imported
  payload, `ReleaseAdoptedImportedPayload` removes it from `ImportedMeshPayloads`;
  the generation holds its own copy. A payload from a newer import is a
  different instance and stays.
- [x] **Deformation generation mirrors.** A successor generation adopts its
  pinned predecessor's managed arrays instead of copying them
  (`AdvancedGpuDeformationStaticGeneration.AdoptCpuMirror`); the predecessor is
  never written again. If an older generation is selected again
  (`TrySelectStaticGeneration`), it rebuilds its arrays from its own GPU buffers'
  client-side copies (`TryRestoreCpuMirror`); if those do not cover its uploaded
  rows, it is reassigned and its meshes are prepared again. A superseded
  generation holds no managed arrays; a generation that selection switched away
  from (not superseded) keeps them while its scene lives.
- [x] **Deformation generations of destroyed scenes.** The pool holds one
  generation per frame slot plus one. Each generation kept the largest capacity
  any scene revision needed (capacity only grows) and its scene's `XRMesh` keys
  until it happened to be reassigned. Play transitions destroy and recreate the
  world's GPU scene, so after two round trips all four generations held
  transition-peak buffers: 2^22 source rows for 1.76 M deformed vertices and
  256 MB of blendshape deltas each, plus their GPU copies. Selection now first
  frees every unpinned, non-current generation whose scene was destroyed
  (`GPUScene.IsDestroyed`, `AdvancedGpuDeformationStaticGeneration.ReleaseStorage`,
  `ReleasedStaticGenerationCount`).

  Measured on desktop after the second Play round trip:

  | Measure | Before | After |
  | --- | --- | --- |
  | Commit | 20.44 GB | 15.97 GB |
  | Device-local | 6.40 GB | 5.24 GB |
  | Live managed | 4.33 GB | 2.21 GB |
  | DataSource memory | 5.33 GB | 3.15 GB |

  All four stay flat over later trips.
- [x] **Deformed vertex arena CPU storage.** `AdvancedDeformedVertexArena` kept
  one pinned array per frame slot at full capacity (3 × 128 MB for 2^21
  vertices), but GPU deformation writes its own output buffers and uses only the
  arena's offsets; nothing in the runtime reads the CPU vertices. The arrays are
  now allocated on the first CPU access (`GetCurrentVertices`,
  `GetPreviousVertices`), and growth keeps its retirement bookkeeping without
  them. Measured in OpenXR after a forced GC: live managed 1.96 to 1.57 GB.
- [x] **Meshlet vertices.** `MeshletPayload` no longer has a `Vertices` copy.
  Payload format version 4 omits the vertex stream; version 3 payloads are read
  by skipping it and are rewritten as version 4. The diagnostic
  `MeshletCollection` builds its vertices from the mesh when asked.
  `MeshletVertex[]` (124 MB at baseline) is gone from the heap.
- [x] **OpenXR eye frame-op clones.** `FramePlanBuilder.BuildAndSeal` takes
  `borrowedLogicalOperations`: a borrowed cohort is lowered with its native
  framebuffer fields stripped (`WithoutNativeOutputFrameBuffer`) and its
  authoring snapshots and input leases left to their owner, so the eye path no
  longer clones every op. The paired-eye plan concatenates both eyes into an
  `ArrayPool` array returned after sealing.
- [x] **Per-frame binding snapshots.** Sealed `ComputeDispatchSnapshot`s are
  row-owned and reused (`FrameOperationPayloads.SealBindingSnapshot`,
  `SealDraw`, `ComputeDispatchSnapshot.CopySealedFrom`) for mesh, indirect,
  mesh-task and compute dispatches; a `SealedContentVersion` joins the
  command-buffer reuse signature so reuse still detects changed bindings. Uber
  material property and feature lookups loop instead of allocating closures.
  Measured at idle in OpenXR: allocations excluding texture decoding fell from
  77.9 to about 44.6 MB/s, and `CreateSealedCopy` no longer appears in
  allocation stacks.
- [x] **Retired frame slots.** Measured with `VulkanFramePlanSlotTelemetry`
  (MCP `invoke_method` `XREngine.Rendering.Vulkan.VulkanFramePlanSlotTelemetry`
  `Describe`): over 14,529 plan builds in an OpenXR session one slot was
  replaced while pinned. `FramePlanBuilder` now provisions one spare
  (`InitialSpareSlotCount`) and creates further spares on demand, up to four.

**Validation.** Census before and after each item. Check allocated bytes per
second at idle, and that no capacity exception is thrown in desktop, OpenXR and
Play round trips.

## 2. Mesh CPU data: packed buffers as the only CPU copy (~2.5 GB managed)

### Background

Each vertex is stored as its own object: `Vertex` (derived from `VertexData`)
with `List<Vector2>` texture coordinates, `List<Vector4>` colours, a
`Dictionary<TransformBase, (float weight, Matrix4x4 bind)>` of bone weights and
per-vertex blendshape `VertexData`. That is about 1.4 KB and roughly 14 objects
per vertex; the baseline holds 1.79 M vertices.

The same data already exists packed in the mesh's `XRDataBuffer`s:

- one buffer per attribute, or an interleaved buffer;
- N UV sets and N colour sets;
- Core4 skinning with 8- or 16-bit indices plus a spill table (offsets, counts,
  indices, values) for vertices with more than four influences;
- sparse blendshape deltas.

Those buffers' client-side copies are native (`DataSource`, `Marshal.AllocHGlobal`)
and are kept after upload. Cooked loads rebuild `Vertex[]` from them
(`TryRebuildVerticesFromBuffers`) without weights or blendshapes. The weight
dictionaries come from in-session imports.

**Packed size.** A packed vertex is about 70–80 bytes:

| Attribute | Size |
| --- | --- |
| Position | 12 |
| Normal | 12 |
| Tangent + sign | 16 |
| UV0 | 8 |
| Colour (unorm8) | 4 |
| Four bone indices + weights | 16–24 |

1.79 M vertices then take about 140 MB, and the GC traces about 25 M fewer
objects.

**Modularity.** Packed storage stays modular, and the cooked format already is:

- attributes are present only when the source has them;
- each attribute carries its own component type and count;
- skinning uses a fixed core plus a spill table for any influence count;
- blendshapes are stored per shape and only for the vertices they move.

Dynamic packing per mesh is the existing buffer model: a per-mesh layout
descriptor lists the attributes, formats and strides. Two constraints apply:

- Each distinct layout is a vertex-input/pipeline variant. Keep the set of
  allowed formats small (for example half-float UVs, unorm8 colours, octahedral
  normals) so pipeline permutations stay bounded.
- The Advanced GPU-scene path needs one uniform record format for indirect
  drawing. `AdvancedPackedVertexCodec` packs position, normal, tangent, two UV
  sets and two colours into `AdvancedDeformedVertex`. It should pack from the
  buffers rather than from `Vertex` objects, and either accept the canonical
  limit or gain a declared extension.

### Hot consumers to move first

- [x] **Advanced geometry validation.** `TryValidateCanonicalGeometry` checks
  `VertexCount` and the attribute buffers' element counts
  (`AdvancedPackedVertexCodec.HasReadableAttributes`).
- [x] **Advanced geometry registration.** `TryRegisterCanonicalGeometry` packs
  with `AdvancedPackedVertexCodec.Pack(XRMesh, …)` straight from the attribute
  buffers.
- [x] **Deformation preparation.** `TryPrepareMesh` counts and packs from the
  attribute buffers, the Core4 + spill skinning buffers
  (`XRMeshSkinningInfluenceReader`) and the blendshape active list
  (`XRMeshBlendshapeActiveListReader`), in the existing budgeted Count/Pack
  stages; the per-vertex helpers are gone.
- [x] **CPU skinned-bounds fallback.** The renderable keeps one packed copy of
  positions and influences per mesh skinning generation
  (`SkinnedBoundsCpuSource`), rebuilt only when the mesh, its geometry revision
  or its skinning state changes, and evaluates a bone palette against it.
- [x] **Bone culling volumes.** `BuildSkinnedBoneCullingVolumes` reads positions
  and Core4 + spill influences from the buffers only.

### One-off consumers (import, editor actions)

- [x] **Scoped vertex view.** `XRMeshVertexView.Open(mesh, content, first, count)`
  materializes positions, normals, tangents, UVs, colours, influences and
  blendshape deltas for a vertex range and attribute subset
  (`EXRMeshVertexViewContent`) and releases them on dispose. It warns when opened
  on the render thread.
- [x] **Move these readers to the scoped view or direct buffer reads:**
  - island split (`XRMesh.MeshIslands`);
  - LOD generation and weight locks (`MeshOptimizerIntegration`);
  - `HlodGroupComponent`;
  - texture-streaming UV bounds (`RenderableMesh.TextureStreaming`);
  - blendshape bounds;
  - skinning diagnostics;
  - `HeightScaleBaseComponent` (eye bones);
  - convex hull input;
  - modelling document converters;
  - `XRMesh.Clone` `HardCopy`.

  BVH, meshlet build and `XRMeshRenderer` already use buffers.
- [x] **Bone remaps on packed data.** Rebinds rewrite only the bone table
  (`UtilizedBones`); the packed indices refer to table slots, so they stay
  valid. `SerializedSceneImporter.Components`, `RebindSerializedTransformReferences`
  and `RebaseSkinningBindPose` work on the table.
- [x] **Importers write buffers directly.** The native FBX importer, the native
  glTF importer and the Assimp triangle path append vertices, influences and
  blendshape deltas to an `XRMeshPackedSource`, and
  `XRMesh(XRMeshPackedSource, indices)` writes the attribute, Core4 + spill and
  sparse blendshape buffers from it. No per-vertex objects are created. Point
  and line meshes keep the primitive constructor, whose vertices are discarded
  after packing.
- [x] **Remove stored vertex state.** `XRMesh._vertices`, `Vertices` and
  `TryRebuildVerticesFromBuffers` are removed. Callers that build from
  `Vertex` objects pass them explicitly
  (`RebuildSkinningBuffersFromVertices(IReadOnlyList<Vertex>)`,
  `XRMesh.Create(primitives, out Vertex[] sourceVertices)`). The `XRMesh`
  runtime contract fingerprint was updated with its schema version kept: the
  removed members were never serialized.

**Validation.**

- Census shows no `Vertex`/`VertexData` objects after load.
- Avatar skinning, blendshapes and bounds are unchanged (screenshots in desktop
  and OpenXR, with GPU and CPU skinning).
- Import, LOD, island split and prefab rebind still work.
- Focused unit tests for the codecs.

**Result (2026-10-05).** OpenXR user configuration, forced full GC: live
managed estimate 4.03 GB after section 1 and 2.15 GB after section 2; GC
committed 5.85 → 4.86 GB. In a desktop A/B against the previous commit with the
default pipeline and a procedural sky, the imported avatar (native FBX path,
GPU skinning and blendshapes) renders the same; only its idle animation pose
differs between captures. Focused unit tests (250): 245 pass; the 5 failures
fail identically on the previous commit. `XRMeshPackedVertexReaderTests` cover
the Core4 + spill and blendshape readers and the packed source. A live-object
census of a full dump after three desktop Play round trips has no `Vertex` or
`VertexData` objects (two empty `Vertex[]`); the memory record counted 1.79 M
and 6.15 M.

## 3. Mesh native buffer copies: release after upload, reload from cooked data

- [x] **Client-copy policy on `XRDataBuffer`.** `XRDataBuffer.ClientCopyPolicy`
  (`EXRBufferClientCopyPolicy`: `Default`, `Retain`, `ReleaseAfterUpload`).
  `Default` resolves to the engine setting `MeshBufferClientCopyPolicy` for
  mesh-owned buffers (default `ReleaseAfterUpload`) and to `Retain` otherwise.
  When every backend reports the current revision uploaded
  (`ReportBackendUploadState`), a `ReleaseAfterUpload` buffer of 256 KiB or more
  is queued; a background timer (`XRBufferClientSpill`) writes its bytes to a
  delete-on-close session file and swaps in a copy-on-write mapping of it
  (`XRBufferSpilledDataSource`). The buffer keeps its metadata and a valid
  pointer: CPU readers page the bytes in from the file cache, and writes copy
  only the touched pages. The bytes leave the private working set: the pages are
  file-backed, so the OS can drop and reread them without writing the pagefile.
  A copy-on-write view is still charged to commit for its full size (measured:
  +513 MB for a 512 MB view; a read-only view adds about 1 MB), so commit
  (`PrivateUsage`) does not fall. Read-only views would release the commit too,
  but about 240 call sites reach client bytes through `ClientSideSource` or
  `Address` without separating reads from writes, and a missed writer would fault;
  that conversion is a follow-up ([D3](#d3-read-only-spill-views-to-lower-commit-as-well)). Only static-usage buffers spill. The swap is
  abandoned if any explicit write ran during it (writers, commits, pushes and
  setters; see the write-model guide), and the private copy is leased while the
  file is written, so disposing or resizing the buffer meanwhile cannot free it
  under the spill. A clone of a spilled buffer gets its own private copy. The
  replaced private copy is dropped after a
  30 s grace period (clones may share it, workers may hold its pointer) and freed
  by its finalizer. GPU-only buffers (Advanced deformation output slots, PPLL
  nodes, Forward+ visible indices) now keep no client copy at all
  (`allocateClientSideSource: false`, `GpuProduced`). Measured in OpenXR: 274 MB
  of mesh buffers spilled; mesh client bytes outside spill mappings 335 → 61 MB;
  non-mesh client bytes 1,382 → 734 MB.
- [ ] **Mapped cooked data.** Partly covered: every uploaded mesh buffer of
  256 KiB or more ends up file-backed through the spill above, whatever its
  source. Still open for published builds: back stored entries directly with the
  archive mapping instead of copying them at load. Published archives are memory-mapped
  (`PublishedArchiveHandle`, `FileMap.FromFile`), but
  `XRMesh.CookedBinary.CopyReaderToBuffer` copies each buffer into new native
  memory and the lease is disposed after load.
  - For stored (uncompressed) entries, keep the lease and back the buffer with
    the mapped range.
  - File-backed pages are not private memory. The OS evicts them under pressure
    and reads them back on access, which provides ranged on-demand reads.
  - Blocked on the cook encoding. `XRMesh.ResolveEncoding` LZMA-compresses every
    stream of 256 KiB or more unless a resolver overrides it (only the
    third-party import path does, choosing Raw inside its Zstd wrapper). Stored
    entries are therefore all smaller than the spill threshold, and mapping them
    individually saves nothing. Mapping pays off only if published cooks store
    large streams raw (larger archives) or as GDeflate (next item). Decision:
    [D2](#d2-published-cook-format-for-large-mesh-streams).
- [ ] **Upload without processing.** Make cooked buffer bytes identical to the
  GPU upload layout so upload is a single copy from mapped pages to staging.
  This is already true for attribute, Core4, spill and sparse blendshape
  buffers. GDeflate entries already support GPU decompression
  (`SetGpuCompressedPayload`); use that path for compressed entries instead of
  CPU decode. Not started; it needs a decision on the published cook format.
  The pieces exist but do not form a complete path:
  - The encoder is the DirectStorage codec that desktop composition registers
    (`Compression.GDeflateBackend`).
  - The cooked reader keeps a GDeflate stream only as the buffer's GPU-compressed
    payload, with no CPU copy.
  - Vulkan decompresses it on upload only with `VK_NV_memory_decompression` and
    buffer device addresses. Otherwise the buffer stays empty
    (`DeviceLocalCompressedFallbackMissingCpuData`).

  Missing pieces:
  - a CPU decode on read for OpenGL, other GPUs and the CPU readers (Advanced
    registration, BVH, skinned bounds);
  - a cook policy that chooses GDeflate over LZMA;
  - validation on both backends.

  Decision: [D2](#d2-published-cook-format-for-large-mesh-streams).
- [x] **Cook the Advanced canonical records.** The Advanced path now packs its
  canonical records from the attribute buffers (section 2), so no runtime repack
  from vertex objects remains; cooking the records as well is not needed.
- [x] **Version every cooked payload layout.** The only cooked layout this work
  changed is the meshlet payload: version 4 omits the vertex stream, and
  version 3 is still read (its vertex stream skipped) and rewritten as 4.
  Skinning (v3) and blendshape (v2) payloads are unchanged and keep rejecting
  other versions with a recook message. The attribute stream layout is
  unchanged; its reader still distinguishes its two historical shapes by
  plausibility rather than by a version number, and adding one would be a format
  change for every cooked mesh.
- [x] **In-session imports.** Their buffers' reload source is the session
  spill file written before the private copy is released (above); no cooked
  import cache is needed for that. Reusing the cooked model across sessions
  still depends on the model/prefab warm path in the
  [model import binary cache TODO](../../assets/model-import-binary-cache-todo.md).
- [x] **On-demand CPU reads.** Spilled bytes are read on demand by paging;
  pages stay in the standby list after the spill writes them, so first reads hit
  memory. The CPU readers that touch spilled buffers after load (Advanced
  registration and deformation preparation, skinned bounds sources, BVH builds)
  run in budgeted preparation stages or on workers, not in the render
  submission loop.
- [x] **GPU readback (optional).** Not built: no consumer needs it now. Spilled
  mesh buffers stay CPU-readable through their mapping, and the GPU-only buffers
  (deformation outputs, PPLL nodes, Forward+ indices) have no CPU reader. Only a
  synchronous diagnostic readback (`IBufferDiagnosticReadbackBackendCapability`)
  and the physics-chain readback service exist. When picking or physics needs
  deformed positions, the design must be async: staging plus fence, with results
  one to three frames later. A synchronous wait drops a frame.

**Validation.**

- Private bytes fall by the released buffer size. Measured: the private working
  set falls; commit does not, because copy-on-write views are charged in full.
- The working set may stay similar while mapped pages are hot; they are
  file-backed and evictable.
- No frame-time spikes when CPU consumers run.
- Reload after eviction is correct.

## 4. CPU texture copies (~1.2 GB native)

Each texture keeps its full RGBA8 mip chain in native memory after upload.
`ApplyResidentDataForVulkanPublication` assigns `Mipmaps`, and each
`Mipmap2D.Data` is a `DataSource`.

Reasons it is kept:

- Renderer-restart rehydration (`ImportedTextureStreamingManager`, called from
  `VulkanTextureUploadGenerationLedger`) re-uploads from `Mipmaps`. Without it,
  the frame is marked `RendererTerminal` with "no retained resident mip payload".
- `XRTexture2D.Width`/`Height`, the Vulkan image description and dense promotion
  (`lockMipLevel`) are read from `Mipmaps`.

Streaming promotion and demotion already reload from the binary cache or source
file, not from these copies.

- [x] **Metadata separate from pixels.** A released mip keeps its width,
  height and formats; only `Mipmap2D.Data` is cleared. Width, height, the Vulkan
  image description and `lockMipLevel` already read only those fields, so no
  separate metadata store is needed.
- [x] **Release pixels after upload.** When a Vulkan dense publication completes
  for a texture with a streaming source, `ImportedTextureStreamingManager` frees
  the pixels of the chain it applied (`XRTexture2D.ReleaseStreamingResidentPixels`)
  by clearing each mip's `Data`; `Mipmaps` is not reassigned, so the image is not
  recreated. Only the chain applied from streaming resident data is released
  (`TrackStreamingResidentMipmaps`). OpenGL keeps its pixels: it uploads
  progressively from them. Consumers that need pixels afterwards: the cooked
  writer and the browser exporter reload them synchronously from the streaming
  source (`TryRestoreReleasedResidentPixels`); DDGI no longer treats a released
  texture as GPU-authored; Vulkan `PushData` and the ImGui preview upload skip a
  released texture. Measured in OpenXR: 1,206 MB released over 115 chains with
  the previous residency policy; DataSource native memory 3.29 to 2.16 GB.
- [x] **Rehydration from the cache.** `TrySchedulePublishedResidentDataForVulkanRehydration`
  claims the transition and, for a released chain, reloads it on a worker from
  `ITextureStreamingSource.LoadResidentData` at the published size and mip count
  before handing it to the renderer's upload scheduler; a mismatch fails the
  rehydration with a named reason.
- [x] **Dispose replaced mips.** `DataSource` now reports owned allocations of
  64 KiB or more to the GC as memory pressure and counts the bytes its finalizer
  frees (`DataSourceMemoryStatistics`), so dropped sources are collected promptly
  and the cost of relying on finalizers is visible. On Vulkan the published chain
  is freed explicitly at publication; a chain replaced by a canceled upload is
  left to the finalizer because its staging may still read it.
- [x] **Clear importer caches.** The path-keyed uber sampler caches hold their
  textures weakly (`WeakTextureCache`): textures no material uses are collected,
  and live ones stay shared. Clearing outright would duplicate textures for
  `SerializedMaterialImporter` calls made outside import scopes.
- [x] **Reuse cache expiry.** `TextureStreamingResidentDataReuseCache.ExpireStale`
  runs every 600 collect frames from the streaming evaluation; entries own their
  copies and are disposed on eviction and expiry, under the same lock that clones
  them out.
- [x] **Play snapshot.** The snapshot writes a texture by reference when the
  asset manager holds it or its file exists, and inline otherwise; the inline
  writer reloads released pixels from the streaming source first
  (`EnsureResidentPixelsForSerialization`) or fails with the reason. Measured on
  a desktop Play round trip with 82 released chains: no reloads were needed;
  every inlined texture is generated or an import placeholder whose source file
  is missing. Two pre-existing defects blocked Play with the avatar and are fixed:
  placeholders for missing files were written as references that cannot resolve
  (now inlined), and the cooked mesh reader rejected meshes with more than eight
  color layers (FBX files carry 23 here; the bound is now 64 on both sides and
  the writer checks that it writes exactly its calculated size).

**Validation.**

- Native private bytes fall by about the streamed texture size.
- `restart_renderer` and renderer hot reload re-upload correctly.
- Play round trips are unchanged.

## 5. Texture VRAM

- [ ] **BCn payloads.** Everything uploads as uncompressed RGBA8
  (`R8G8B8A8Unorm`); the binary cache variant is named
  `..._rgba8_uncompressed_binary`. Add BC7 colour, BC5 normal and BC4/BC7 mask
  variants to the texture cache, with Vulkan format mappings and
  `textureCompressionBC` checks. BC7 is 1 byte per pixel against 4: the
  1.17 GB of streamed textures would become about 0.3 GB. This needs an encoder
  dependency; ask first and check the licence. It is already planned in the
  texture runtime design. Decision: [D1](#d1-which-bcn-encoder-to-add).
- [x] **Real VRAM budget.** `VulkanRenderer` now implements
  `IVulkanAllocatorStreamingBackendCapability` (nothing did, so the streaming
  allocator-pressure check never ran). Streaming caps its budget to what the
  device-local heap budget from VMA (`VK_EXT_memory_budget`) times the new
  `VramBudgetHeapFraction` engine setting (default 0.84, minus a 768 MB reserve)
  leaves after every other Vulkan allocation, and still honours `VramBudgetMB`.
  Measured: the streaming budget fell from 19.4 GB to about 10.5-11.8 GB on a
  24 GB GPU, so dense residency demotes before the driver pages.
- [x] **Wire `TextureQuality`.** `IRuntimeRenderSettingsServices.TextureQuality`
  carries the user setting to streaming; `TextureResidencyPolicy.ApplyTextureQuality`
  caps the resident size at 4096, 2048, 1024 and 512 texels for High, Medium, Low
  and Lowest (never below the preview size). The cap is the mip bias: mips above
  it are never resident. (`RuntimeEngine.UserSettings` is a separate instance the
  host never fills; the GI mode read from it is a separate defect.)
- [x] **Coverage rule for VR.** Removed the rule. A covering mesh's projected
  span already reaches the viewport size, and the role and UV-density terms
  still apply; for a 2688-pixel eye a covering albedo still quantizes to 4096,
  but normal, mask and ORM maps of a self-avatar the HMD sits inside no longer
  all go to full size. Measured: streamed texture GPU memory 1.17 to 0.30 GB for
  the avatar scene; device-local 7.49 to 6.66 GB.
- [ ] **Sparse residency (later).** Vulkan uses dense residency only
  (`SupportsSparseResidency => false`). Sparse residency is later work in the
  design.

## 6. Render-target VRAM

The stereo pipeline set is 38 targets, about 2.3 GB at 2688² with two layers;
the desktop set is 0.6 GB.

- [x] **Audit active passes.** Every declared target is materialized and
  allocated eagerly, so declarations must follow the active passes. Changes:
  - The OpenXR eye pipeline (`RvcRenderPipeline`) declares its nine two-layer RVC
    targets, buffers and FBOs only when the resolved RVC plan schedules GPU stages
    (`RvcResourcesEnabled` feature bit; `Off` and the Forward+ oracle schedule
    none, and no RVC kernel is implemented yet). `VPRC_RvcPass` describes no
    resources without them.
  - AO scratch targets are declared per AO mode: SSAO/MVAO/MSVO raw, HBAO+ raw and
    blur, GTAO raw and blur (`UsesRawAmbientOcclusionTexture`, `UsesHBAOPlusMode`,
    `UsesGTAOMode`), matching the per-mode FBOs.

  Measured in OpenXR together with the bloom change below: device-local 6.50 to
  5.27 GB. Not changed: the Advanced pipeline's AA, effect and exact-transparency
  sets are declared without feature gating (desktop sizes; follow-up), and WBOIT
  targets follow `EnableWeightedBlendedOitPasses`.
- [x] **Formats.** The bloom chain is R11G11B10F (`BloomInternalFormat`): no
  bloom consumer reads alpha (the bloom copy passes source alpha through, which
  the format drops), every consumer samples `.rgb`, and values are
  clamped non-negative (about 77 MB at 2688² by two layers). Depth (D24S8 /
  D32S8) and normals (RG16F) are already compact. Not changed: HDR scene colour
  (`SceneCopy` and `PassthroughHDR` copy its alpha), emission (its alpha is a
  flag read by the light combine), post-process outputs (shared format
  resolver), and starting bloom at half resolution (the bloom pass and the
  composite would have to agree on a new base mip).
- [ ] **Transient aliasing.** Aliasing between targets with disjoint lifetimes
  is disabled for every target (`VulkanResourceAllocator.UpdatePlan` forces
  `SupportsAliasing=false`, and `VulkanTransientAttachmentPlan.IsActive` is
  false), pending native handoff, initialization and lifetime proof. It is high
  risk; do it last. Not started. What exists is scaffolding:
  - `VulkanAliasGroupKey` puts every transient request with an equal alias key
    (size policy, format, usage, samples, mips, layers) into one group backed by
    one `VkImage`, without checking lifetimes. Enabling the flag as it stands
    would merge targets that are live at the same time.
  - Per-pass lifetime intervals exist (`VulkanCompiledRenderGraphPlan`,
    `VulkanTransientAttachmentPlan.DeclaredIntervalsDoNotOverlap`), but they only
    feed diagnostics.
  - Missing:
    - lifetime-aware grouping (interval colouring per key);
    - placed memory aliasing for targets of different formats or sizes;
    - handoff barriers between aliases (`VulkanBarrierPlanner` emits none when
      both aliases use the same layout);
    - an UNDEFINED or discard transition on first use;
    - an audit of name-based lookups outside the graph (readback, ImGui viewers,
      OpenXR prewarm).

  Buffers were not forced dedicated: `BufferResourceDescriptor.SupportsAliasing`
  defaults to true, so a transient buffer would have aliased without any lifetime
  check. `UpdatePlan` now keeps buffers dedicated as well.

  Decision: [D5](#d5-separate-projects-for-transient-aliasing-and-the-planner-commit-fix).
- [ ] **Planner follow-ups.** From the stereo flicker record:
  - [ ] a commit made inside the OpenXR planner scope retires the live
    eye-planner allocator, causing a reallocation and the startup black. Still
    open: the record notes that changing the retirement candidate caused
    plan-seal failures; deferring the commit to scope exit is the safer route.
    Decision: [D5](#d5-separate-projects-for-transient-aliasing-and-the-planner-commit-fix).
  - [x] the published table's LRU can evict a shared stereo state: eye and mirror
    scopes now mark the published entry that owns their allocator as used
    (`VulkanResourcePlannerSessionService.TouchPublishedOwner`).

## 7. .NET heap fragmentation and churn

- [x] **Fragmentation.** The heap is 8.55 GB with 5.9 GB live and 1.05 GB
  fragmented. Re-measure after sections 1–2. Try a GC conserve-memory setting
  and record heap size, pause times and frame time.

  Re-measured in OpenXR with user settings, 300 s after the avatar loaded, on
  the same build, one run each:

  | Measure | Default GC | `DOTNET_GCConserveMemory=5` |
  | --- | --- | --- |
  | Heap, natural | 2.91 GB | 2.89 GB |
  | Fragmented, natural | 1.21 GB | 1.19 GB |
  | GC committed, natural | 3.60 GB | 3.09 GB |
  | Heap, after a full GC | 2.94 GB | 2.59 GB |
  | Fragmented, after a full GC | 1.37 GB | 1.02 GB |
  | Commit (`PrivateUsage`), natural | 13.52 GB | 13.01 GB |
  | GC pause time | 1.14 % | 1.06 % |
  | OpenXR frames submitted / missed per second | 32.3 / 4.7 | 31.8 / 5.1 |
  | Allocation rate | 48 MB/s | 49 MB/s |

  Live managed memory is now about 1.6 GB, against 5.9 GB. Conserve mode saves
  about 0.5 GB of committed memory at no measurable frame-rate cost in these
  samples. It is not enabled by default: it makes the GC choose blocking
  compactions when fragmentation is high, and an average pause percentage does
  not show whether a single compaction of a 3 GB heap drops VR frames. To try it,
  set the environment variable before launch, or add the
  `System.GC.ConserveMemory` runtime option to the editor project. Decision:
  [D4](#d4-gc-conserve-memory-default).
- [x] **Uber variants.** Generated uber shader variants are duplicated per
  material by the Play snapshot (see the memory record). A generated variant
  has no file path, so the capture still inlines it. On restore,
  `SnapshotBinarySerializer.RestoreSnapshotValue` matches the copy to the live
  cached variant (`UberShaderVariantBuilder.TryGetCachedVariant`: shader type,
  hash, source path and text), destroys the copy and returns the live instance.
  Restored materials recover their canonical shader from the variant
  (`TryGetCanonicalShader`) instead of reading `UberShader.frag` again. Measured
  with a full dump after three desktop Play round trips: 34 live strings of
  800 KB or more (52 MB); the memory record counted 814 such strings (about
  0.5 GB) after three trips.
- [x] **Upload arena.** Its high-water mark never shrinks (see the memory
  record). Two changes:
  - The instance, view, light and material streams have no production writer,
    so `AdvancedFrameSlotUploadArenaOptions.Default` starts them at 4 KB (main
    and overflow) instead of sizing them for full-scene uploads. The deformation
    job stream is unchanged.
  - The arena shrinks: after 600 frames (`ShrinkWindowFrames`) in which a grown
    stream used a quarter of its capacity or less, the next frame boundary
    resizes it to twice the window's peak, never below the initial size. Telemetry reports
    `CapacityShrinkCount` and `CapacityShrinkBytes`.

  Measured on desktop after three Play round trips: 2.1 MB per slot and 6.3 MB
  mapped, with no growth (`CapacityGrowthCount` 0). The memory record measured
  about 190 MB of `byte[]` before.

## Expected savings (estimates)

| Section | Process memory | GPU memory |
| --- | --- | --- |
| 1. Renderer arrays | ~1 GB | — |
| 2. Per-vertex mesh model | ~2.5 GB | — |
| 3. Mesh native copies | the native buffer size (to be measured) | — |
| 4. Texture CPU copies | ~1.2 GB | — |
| 5. Texture compression and budget | — | ~0.9 GB or more |
| 6. Render targets | — | hundreds of MB to over 1 GB |
| 7. Heap fragmentation | part of 2.6 GB dead or fragmented | — |
