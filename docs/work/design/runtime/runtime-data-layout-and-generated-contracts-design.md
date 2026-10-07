# Runtime Data Layout And Generated Contracts

Last Updated: 2026-10-02
Status: Accepted design, implementation paused at the owner's 2026-10-02 checkpoint;
remaining code and runtime validation are tracked in the
[Runtime Data Layout And Generated Contracts TODO](../../todo/runtime/runtime-data-layout-and-generated-contracts-todo.md).
The owner approved the gated format, protocol, and dependency changes on
2026-10-02.
Owner: Runtime / Assets / Networking / Rendering
Scope: object population in bulk-updated paths, boundary encodings, generated
type contracts, development/shipping parity, and executable downloadable content.

Related docs:

- [Runtime Regression And NativeAOT Hardening TODO](../../todo/runtime/runtime-regression-and-nativeaot-hardening-todo.md)
- [AOT Final Game Builds](../../../developer-guides/runtime/aot-final-game-builds.md)
- [Cooked Asset Serialization: AOT And I/O](../../../architecture/assets/cooked-asset-aot-and-io.md)
- [Hot-Path Memory Control](../../../developer-guides/runtime/hot-path-memory.md)
- [Generic ECS And Humanoid Avatar Networking Design](generic-ecs-avatar-networking-design.md)
- [Avatar Scene Publication Stalls](../../investigations/asset-import/avatar-scene-publication-stalls-2026-09-24.md)
- [Source-Backed C# Script Components](../scripting/source-backed-csharp-script-components.md)
- [Dynamic Indirect Material Bindings](../rendering/dynamic-indirect-material-bindings.md)
- [Networking Design](../networking/networking.md)

## 1. Summary

This review started from two external inputs. The first was public reporting on
CAPCOM's next-generation RE ENGINE runtime. Code is authored in C#, compiled to
a .NET assembly, translated into a C++ dialect ("RE:C++"), and built natively.
That runtime also uses page-based instance management. The second was an
AI-produced review of what XRENGINE should take from that work.

This document records which ideas apply to XRENGINE. It proposes six changes,
and each is tied to evidence in current `master`.

Conclusions:

- **Do not build a C#-to-native translation pipeline or a custom managed
  allocator.** XRENGINE already uses the language and runtime model that CAPCOM
  is moving toward, and NativeAOT already produces native final-game builds.
- **The lessons that transfer are narrower:**
  1. Do not bulk-update large populations of heavyweight objects.
  2. Keep one source of truth for every type that crosses a boundary.
  3. Keep development and shipping on the same code path.
  4. Translate C# into a constrained target only when that target provides a
     needed property. For XRENGINE, that property is safe downloadable
     behavior, not speed.
- **Current `master` has a concrete instance of each problem.** Five proposals
  are code changes. The sixth is a decision that should be made before world
  packages carry behavior.

## 2. Assessment Of The Prior Review

The prior review is accurate about the large decisions. Most of its concrete
recommendations, however, repeat work the
[NativeAOT hardening TODO](../../todo/runtime/runtime-regression-and-nativeaot-hardening-todo.md)
already tracks. It also spends the most attention on its least useful item:
REDox.

Points this document accepts:

- Reject an RE:C++ equivalent. Code generation, the object/memory model, and the
  integration model are separate decisions.
- Prioritize NativeAOT hardening and generated metadata.
- Treat GC latency modes as supporting controls, not pause guarantees.
- Distinguish downloadable content from downloadable executable code.

Points this document changes or sharpens:

| Prior review | This document |
|---|---|
| Evaluate REDox for manifests and editor metadata. | Defer it. It does not address a measured bottleneck. Authoring assets use YAML, and runtime assets use MemoryPack and the cooked binary format. The prior Json.NET compatibility analysis targets a path the asset system does not use. See §5 for revisit criteria. |
| Memory: measure arenas and record tables under VR load. | The record tables are already compact. The measured object-population problem is the transform hierarchy (§4.5). |
| Keep the specialized pose codecs as the replication baseline. | The live VR IK pose broadcast wraps those codecs in Json.NET. The zero-allocation path exists, but runtime code does not use it (§4.2). |
| Extend the generated registrations. | The generator is a regex-based PowerShell script. Codecs and accessors need semantic type information, so move to a Roslyn generator first (§4.4). |
| Validate shared CPU/GPU layouts. | Each GPU record currently has three hand-maintained copies, and nothing compares the shader copy to the C# copy (§4.6). |
| Examine expensive native boundaries. | There is little evidence of a problem. Physics reads results through active-actor lists, and rendering uses Silk.NET function pointers. Remaining `DllImport` sites are mostly platform, windowing, and editor code. No dedicated program is proposed (§5). |
| Downloadable code should influence the scripting design. | It needs an explicit decision. This is the one place where a CAPCOM-shaped pipeline fits XRENGINE (§4.7). |

**Unverified external claims.** These details come from external material or the
prior review and were not re-verified here:

- the C++23 baseline;
- the RE:Build stages;
- the 64 KB page size and page reference counting;
- REDox source details.

No proposal below depends on them.

## 3. What Transfers

| CAPCOM pain point | XRENGINE status |
|---|---|
| C++ complexity and long build/link times | Not applicable. The editor runs on CoreCLR with hot reload, and only final launchers use NativeAOT publication. |
| Memory corruption and leaks | Mostly not applicable. Native lifetimes already have explicit owners, including VMA, `NativeMemoryPressureTracker`, and frame scratch storage. |
| Awkward reflection | Applies. Strict analysis reports 913 IL2xxx/IL3xxx diagnostics, 424 of them in cooked-binary paths. |
| Duplicated type systems | Applies to C# and GLSL GPU records (§4.6) and to C# and wire payloads (§4.2). |
| Expensive marshaling | Applies to network payload encoding (§4.2) and cooked payload copies (§4.1), not P/Invoke. |
| IL2CPP-only debugging | The equivalent is behavior that works in the CoreCLR editor and fails only after NativeAOT publication (§4.3). |
| GC pressure and fragmentation | Applies to large-object-heap (LOH) churn from asset payload copies (§4.1) and to the number of transform objects (§4.5). |

## 4. Proposals

The proposals are listed in the recommended execution order.

| § | Change | Impact | Cost | Risk | Relationship |
|---|---|---|---|---|---|
| 4.1 | Load cooked assets without transient payload copies | Medium | Small | Low | Supports NativeAOT hardening A1 |
| 4.2 | Use binary state-change payloads and connect pose broadcast to the span codecs | Medium; high at scale | Small to medium | Medium: wire format | Precedes ECS avatar networking |
| 4.3 | Add development-mode AOT parity diagnostics | Medium | Small | Low | Supports NativeAOT hardening A1/A3 |
| 4.4 | Generate runtime contracts from semantic type information | High: enabler | Medium | Medium: needs dependency approval | Prerequisite for A1 expansion |
| 4.5 | Store transform hierarchy state densely | High | Large | High | Avatar stalls; ECS coexistence |
| 4.6 | Define each GPU record layout once | Medium | Medium | Low | Advanced renderer; material layouts |
| 4.7 | Decide how downloaded content may execute | High: security | Decision only | — | Networking and scripting designs |

### 4.1 Load Cooked Assets Without Transient Payload Copies

**Current path.** In a published runtime,
`AssetManager.TryLoadPublishedAssetFromArchive` in
[AssetManager.Published.cs](../../../../XREngine.Runtime.Core/Assets/AssetManager.Published.cs)
loads an asset as follows:

1. `AssetPacker.GetAsset(archivePath, assetPath)` maps the archive. For every
   asset request, it re-reads the header, footer, string dictionary, and TOC.
2. The entry is decompressed into a new `byte[]` (**copy 1**).
3. `CookedAssetReader.LoadAsset(byte[])` deserializes `CookedAssetBlob` through
   MemoryPack. Its `Payload` property is a `byte[]` (**copy 2**).
4. `PublishedCookedAssetRegistry.TryDeserialize(Type, byte[])` calls
   `RuntimeCookedBinarySerializer.Deserialize(Type, ReadOnlySpan<byte>)`, which
   builds the final runtime arrays (**copy 3**, the necessary copy).

Copies 1 and 2 are temporary. For meshes and textures larger than 85 KB, both
allocate on the LOH. The LOH is collected only with generation 2, so frequent
avatar arrivals and departures add gen2 pressure and LOH fragmentation. This is
the .NET form of the fragmentation problem in the CAPCOM material.

`CookedAssetReader.LoadAsset(ReadOnlySpan<byte>)` already exists, but the
published path does not use it. Copy 2 would remain even if it did, because the
envelope materializes `Payload` as an array.

**Design:**

- **Keep archives open.** Add a long-lived published archive handle for each
  archive. It owns the mapping, parsed TOC, and string dictionary, is opened
  once per published content root, supports concurrent read-only lookups, and
  is disposed when content is swapped or the runtime shuts down.
- **Return payload leases.** Add
  `TryReadAsset(path, out CookedPayloadLease lease)`. A stored entry returns a
  span over the mapping. A compressed entry is decompressed into pooled or
  native storage owned by the lease. Disposing the lease releases that storage.
  Leases must not cross asynchronous boundaries unless ownership is explicitly
  transferred.
- **Slice the payload from the envelope.** Replace the MemoryPack `byte[]
  Payload` with a fixed header containing the format, type reference, payload
  offset, and payload length. Read that header from the span and slice the
  payload without copying it. Bump the archive or envelope version, and reject
  older content with an actionable diagnostic. The owner approved this breaking
  storage-format change on 2026-10-02.
- **Use spans in the registry.** Change the deserializer delegate to
  `PublishedCookedAssetDeserializeDelegate(ReadOnlySpan<byte> payload, Type
  assetType)`. The serializer side may write to an `IBufferWriter<byte>`.
- **Separate published reads from reflective reads.** Split the `RuntimeBinaryV1`
  reader from the reflective `BinaryV1` reader. The published path should then
  carry no `RequiresUnreferencedCode` or `RequiresDynamicCode` annotations. This
  removes a cause of cooked-binary warnings instead of suppressing them.
- **Follow up with texture streaming.** Apply the same pattern to payload
  creation and extraction in
  [XRTexture2D.StreamingPayload.cs](../../../../XREngine.Runtime.Rendering/Objects/Textures/2D/XRTexture2D.StreamingPayload.cs).

**Validation:**

- Load MonkeyBall content and one representative imported avatar before and
  after the change. Record total allocated bytes, LOH allocations from
  `GC.GetGCMemoryInfo`, gen2 count, and load time.
- Load and unload the avatar 20 times, then report LOH growth.
- Run the strict AOT smoke with `Tools/Publish-MonkeyBallVR.ps1`.

### 4.2 Use Binary State-Change Payloads And Connect Pose Broadcast To The Span Codecs

**Current path.**
[BaseNetworkingManager.cs](../../../../XREngine.Runtime.Core/Networking/BaseNetworkingManager.cs)
sends state changes as `StateChangeInfo`. Its `Data` field is a string produced
by Json.NET through `StateChangePayloadSerializer`, and MemoryPack then
serializes the wrapper. This path carries humanoid pose frames, replication
snapshots and deltas, authority leases, clock synchronization, and remote-job
messages.

`VRIKSolverComponent` quantizes poses with `HumanoidPoseCodec` and uses the
allocating `HumanoidPosePacketBuilder` to build a `HumanoidPoseFrame`. That frame
is marked `[MemoryPackable]` and contains a `byte[] Payload`. Json.NET still
encodes it on every send, which turns the payload into base64 and increases it
by roughly one third. `ServerNetworkingManager` rebroadcasts accepted frames the
same way.

The zero-allocation path already exists in `HumanoidPoseSpanPacketWriter`,
`HumanoidPosePacketCursor`, `RealtimePacketSendRing`, and
`PersistentReceiveSlabPool`. `RuntimeMemoryControlTests` verifies that this path
allocates zero bytes after warmup, but no runtime code outside tests uses it.

This is a small version of CAPCOM's duplicated-type and marshaling problem: a
typed binary model is converted to reflective text at a boundary and then
converted back.

**Design:**

- **Store binary payloads.** Change `StateChangeInfo` to carry a binary payload
  and its `EStateChangeType`. The payload should be pooled or leased rather than
  a newly allocated string or array per message. Register one binary codec per
  type: MemoryPack-generated codecs for control messages, and dedicated codecs
  for pose and replication data. Remove Json.NET from the runtime networking
  path.
- **Bypass the generic wrapper for high-rate data.** Send poses, replication
  deltas, and clock synchronization through channel-specific codecs that write
  into send-ring slots and decode from receive slabs.
- **Use the existing pose writer.** `VRIKSolverComponent` should publish through
  the span writer and ring path, or through its successor in the
  [generic ECS avatar networking design](generic-ecs-avatar-networking-design.md),
  instead of allocating a builder and frame on each send.
- **Keep JSON at the edges.** Use JSON only for human-facing control-plane and
  HTTP surfaces outside the realtime layer.

**Risk:** this changes the wire format and therefore needs a protocol version
bump. The owner approved the breaking protocol change on 2026-10-02. Admission
already requires matching build and world identity. Coordinate
with the ECS avatar networking design so that pose ownership changes once.

**Validation:**

- After warmup, send and receive allocation scopes should report zero bytes per
  tick, using the existing test pattern.
- Compare bytes per avatar per packet before and after the change.
- Run a local two-client session using the networking guide.

### 4.3 Development-Mode AOT Parity Diagnostics

**Problem.** In editor play mode, CoreCLR can still use reflective paths that
NativeAOT cannot use:

- the assembly scan in `CookedAssetTypeReference.Resolve`;
- reflective `BinaryV1` deserialization;
- reflective factory and property-binding helpers.

A type with missing generated registrations therefore works in the editor and
fails only after a complete NativeAOT publication and smoke run. This is
XRENGINE's equivalent of IL2CPP-only debugging.

**Design:**

- Add a development-only parity mode controlled by
  `XRE_AOT_PARITY=off|warn|error`.
- In this mode, player-path resolution that succeeds only through a reflective
  fallback emits a structured diagnostic. The diagnostic names the type, the
  call-site category, and the missing registration. `warn` logs once per type;
  `error` throws.
- Instrument these fallback points:
  - the type-resolution scan;
  - `BinaryV1` deserialization during play mode;
  - runtime factory helpers that use `Activator.CreateInstance` or
    `Type.GetType`;
  - reflective animation property binding.
- Run the unit-test lane and the MonkeyBall editor play-mode smoke with
  `error`.
- Document the environment variable in
  [AOT Final Game Builds](../../../developer-guides/runtime/aot-final-game-builds.md).

Shipping behavior does not change. The feedback loop shrinks from "publish and
run the smoke" to "enter play mode." This follows the repository rule that
fallbacks must be visible.

### 4.4 Generate Runtime Contracts From Semantic Type Information

**Current generator.**
[Generate-AotFactoryRegistrations.ps1](../../../../Tools/Generate-AotFactoryRegistrations.ps1)
runs from
[FactoryRegistrations.targets](../../../../Build/Registration/FactoryRegistrations.targets)
for Host, Bootstrap, and Browser, and from the Rendering project. It parses C#
with regular expressions, including comment stripping and namespace matching,
and emits factory registrations.

Hardening item A1 asks generated code to provide:

- cooked codecs;
- property, field, method, and event accessors;
- closed-generic collection formatters;
- animation-property binding delegates;
- texture-streaming codecs.

These outputs need semantic information: inheritance, member types, generic
instantiations, and attributes across partial declarations and referenced
assemblies. A regex parser cannot provide that reliably.

**Design:**

- **Add an analyzer-only Roslyn generator.** Create an incremental source
  generator project, tentatively `XREngine.SourceGenerators`, targeting
  `netstandard2.0`. Runtime assemblies reference it as an analyzer.
- **Generate many outputs from one declaration.** For each marked runtime type,
  emit its factory, cooked codec, typed accessors, animation bindings, stable
  type ID, and schema version. Install these outputs into
  `PublishedCookedAssetRegistry` and `AotRuntimeMetadataStore` during module
  installation.
- **Migrate before expanding.** First port the PowerShell factory generation,
  with a parity test that compares the emitted registration sets. Then add
  codecs in the A1 order: texture-streaming payloads first, followed by
  animation bindings.
- **Report unsupported shapes at compile time.** Emit generator diagnostics for
  unsupported member types. Missing metadata then fails during compilation, not
  only during cooking or publication.
- **Use the same generated code everywhere.** The editor and player compile the
  same output, so parity follows from the build. This complements §4.3.

**Dependency:** this requires `Microsoft.CodeAnalysis.CSharp` as an
analyzer-only build dependency. The owner approved it on 2026-10-02. Adding it
still requires running `Tools/Reports/Generate-Dependencies.ps1` and reviewing
the license output, as `AGENTS.md` requires.

**Validation and risks:** measure editor build time before and after. Keep
generator output deterministic, and prevent editor-only types from leaking into
player registrations.

### 4.5 Store Transform Hierarchy State Densely

**Evidence:**

- The
  [avatar scene publication stalls investigation](../../investigations/asset-import/avatar-scene-publication-stalls-2026-09-24.md)
  measured 204.3 ms in `RuntimeWorldHost.ProcessDirtyTransforms` when an avatar
  entered the scene. It also recorded 390,165 render-matrix applications.
- Batching reduced the largest sampled pass to 15.4 ms; ordinary samples were
  3–4 ms. Steady visibility work remained 85–100 ms, including 25–31 ms of
  canonical scene publication.
- Each `TransformBase` contains:
  - ten `Matrix4x4` fields, or at least 640 bytes of matrix state;
  - six separate lock objects;
  - five matrix-changed events, plus the property events inherited from
    `XRBase`;
  - a child `EventList`.
- Twenty-seven classes derive directly from `TransformBase` or `Transform`.
- [RuntimeWorld.Transforms.cs](../../../../XREngine.Runtime.Core/World/RuntimeWorld.Transforms.cs)
  processes dirty transforms as an object graph. It groups dirty transforms in
  `ConcurrentDictionary<int, ConcurrentHashSet<TransformBase>>` buckets,
  removes duplicates with hash sets, walks ancestors, and calls the
  `Task`-returning `RecalculateMatrixHierarchy` for each root.

A full-body avatar has hundreds of transforms, and a populated social instance
can have tens of thousands. This is the kind of large object population the
CAPCOM page and microarray work addresses, and here its cost has been measured.

**Principle.** Follow the coexistence rule from the ECS design. `TransformBase`
remains the authoring and gameplay API, while bulk propagation and publication
move to dense storage. This is not a migration of transforms to ECS.

**Design:**

- **Dense store.** Each world owns a `TransformHierarchyStore`. Its
  structure-of-arrays layout is indexed by a generational `TransformHandle` and
  contains:
  - parent index;
  - hierarchy order;
  - local matrix;
  - world matrix;
  - dirty, world-override, and has-subscribers flags;
  - a render-publication slot mapped to an `AdvancedTransformRecord` row.
- **Parent-first ordering.** World propagation is a linear forward pass over
  dirty ranges: `world[i] = local[i] * world[parent[i]]`. Reparenting and
  insertion mark the order dirty. Reordering happens lazily in batches;
  `TransformHierarchyMutationBatch` is the existing batching seam, so an avatar
  import can insert its hierarchy in one batch.
- **Polymorphism only for local matrices.** Subclasses still compute their own
  local matrices, but only when those matrices are dirty, and then write them
  to the store.
- **World-space overrides.** Transforms that own world space, such as
  physics-driven `RigidBodyTransform` and VR device or late-latched transforms,
  set the world-override flag and write world matrices directly.
- **Subscriber-only events.** Matrix-changed events are raised after the pass
  from a compact changed list, and only for entries that have subscribers.
  Transforms without subscribers do not pay per-transform event cost.
- **Array-level publication.** Replace per-transform locks with the store's
  publication model. The update thread writes the current generation, and render
  consumers read the published generation. This extends the existing
  render-matrix sequence lock and frame-snapshot contracts from individual
  objects to arrays.
- **Direct render publication.** Copy dirty ranges directly into the transform
  record table instead of applying render matrices object by object.
- **Replication state ownership.** Move the per-transform `_lastReplicatedMatrix`
  into the replication component or system that needs it.

**Staging:**

1. **Benchmark.** Measure avatar import and N moving avatars, using the
   RenderBench production scene that already calls `ProcessDirtyTransforms`.
   Record transforms per frame, milliseconds, and allocations.
2. **Prototype.** Put the store behind `TransformBase` for world-matrix
   propagation and render publication only. Leave local computation unchanged,
   then compare against the benchmark.
3. **Migrate object state.** Move events and locks to the store's publication
   model, then remove redundant per-instance matrix fields.
4. **Integrate overrides and tools.** Connect physics and VR world overrides,
   then update editor gizmo paths.

**Risk: high.** This is one of the most widely used engine types. The design
must preserve VR late-latch ordering, physics interpolation, editor gizmo
behavior, and the `ELoopType` parallel modes. Visible behavior needs the
live-editor validation loop, and rendering changes must follow the
[DefaultRenderPipeline invariants](../../../architecture/rendering/default-render-pipeline-notes.md).
Do not begin until the avatar stall investigation closes, so its fixes are not
mixed with this change.

**Acceptance:**

- Peak and steady-state transform costs fall by a target set from the stage-one
  benchmark.
- Steady-state propagation allocates nothing.
- Multi-view captures show no visual regression.
- Physics and VR behavior remain at parity.

### 4.6 Define Each GPU Record Layout Once

**Current state.** Each advanced GPU record exists in three hand-maintained
forms:

1. a C# struct, such as `AdvancedDrawRecord`;
2. a GLSL struct, such as `XRAdvancedDrawRecord` in
   `Build/CommonAssets/Shaders/Advanced/Access/AdvancedDrawAccess.glslinc`;
3. expected sizes and offsets in
   [GPUSceneLayoutContract.cs](../../../../XREngine.Runtime.Rendering/Commands/GPUSceneLayoutContract.cs)
   and `AdvancedGpuSceneRecordContractTests`.

The expected constants pin only the C# struct. Nothing compares the compiled
shader block layout with the C# layout, so a GLSL-only edit can pass every
current check. For example, reordering fields or adding a `vec3` that changes
std430 padding would not be detected.

**Design:**

1. **Validate compiled shader layouts.** During shader cooking or compilation,
   and in a unit test, reflect storage-buffer block members from SPIR-V. The
   Vulkan backend already contains Slang reflection. Compare member names,
   offsets, and strides with `Marshal.OffsetOf` and `Unsafe.SizeOf` for the
   bound C# record. Report every mismatch. The reflected comparison then
   replaces the hand-written constants as the authority.
2. **Generate data-only shader structs.** Mark data-only C# records with an
   attribute. The generator from §4.4 emits their GLSL declarations into a
   generated include used by the existing hand-written access helpers.
   Mapping rules are:
   - `Matrix4x4` maps to `mat4` with explicit row- or column-major layout;
   - handles map to their GLSL structs;
   - `vec3` members are rejected unless padding is explicit.

Align this work with the existing
[dynamic indirect material bindings](../rendering/dynamic-indirect-material-bindings.md)
design. That design already generates material-row shader and packer layouts, so
scene records should use the same mechanism rather than a second one.

### 4.7 Decide How Downloaded Content May Execute

**Current state:**

- World packages currently contain data: one `.asset`, dependent files, and
  bootstrap, build, and schema metadata.
- The client boot flow in the [networking design](../networking/networking.md)
  includes fetching "binaries/assets."
- The [scripting design](../scripting/source-backed-csharp-script-components.md)
  rules out a C# interpreter and states that NativeAOT final builds cannot load
  scripts at runtime.

A social VR platform with user-created worlds and avatars will eventually need
user-authored behavior.

**Constraints:**

- NativeAOT players cannot load new managed assemblies.
- In CoreCLR, `AssemblyLoadContext` provides unloading, not security isolation.
  .NET has no in-process sandbox now that Code Access Security is gone, so
  untrusted IL loaded into the client runs with the client's privileges.
- Therefore, "CoreCLR player plus downloaded DLLs" is not acceptable for
  untrusted content, whether or not the player uses AOT.

**Options:**

| Option | Shape | Notes |
|---|---|---|
| A | No downloadable behavior; worlds configure engine-compiled components with data. | Lowest risk; limits creators. |
| B | Restricted C# subset → IL → allowlist verifier → compact bytecode → metered engine VM. | Uses CAPCOM's C# → assembly → translated-target shape for sandboxing and AOT compatibility rather than speed. The closest precedent is UdonSharp. |
| C | Visual node graphs → the same VM. | Can share B's verifier and VM. |
| D | Embedded WASM runtime with an engine host API. | Language-neutral, but requires a native dependency and license review; C# through WASI is heavyweight. |
| E | Out-of-process script host for each world. | Provides an OS-level sandbox, but adds IPC latency and operational complexity. |

**Recommendation:**

- Use option A for now.
- Record option B as the intended direction, sharing its VM with option C.
- Until a sandboxed runtime exists:
  - package validation should reject managed assemblies and other executable
    payloads;
  - no package or control-plane field should carry them.

This is a decision, not a request to build the VM now. It should be recorded
before the world-package format or control plane accepts anything other than
data.

## 5. Explicitly Not Recommended

- **C#-to-C++ translation or an RE:C++ equivalent.**
  - NativeAOT already produces native code.
  - Owning the semantics of generics, exceptions, GC behavior, and debugging
    would be a permanent cost.
  - CAPCOM's own runtime migration shows that cost.
  - XRENGINE would lose upstream JIT and NativeAOT improvements.
- **A custom page-based managed allocator or reference-counting collector.**
  - .NET does not allow managed object allocation to be replaced.
  - Region-based GC already manages heap memory in regions.
  - Unmanaged frame data already uses `FrameScratchAllocator` and scratch
    rings.
  - Reconsider only if VR-load GC telemetry, collected after §4.1 and §4.5,
    still attributes generation-2 pauses to object population.
- **REDox adoption now.** Revisit only when all three conditions hold:
  - Profiling shows structured-metadata parsing as a measurable editor or
    import cost.
  - REDox supports AOT or source generation.
  - License review is complete. The prior review reports Apache-2.0; that has
    not been re-verified, and NOTICE handling still needs review.
- **An interop overhaul.**
  - Convert remaining `DllImport` calls to `LibraryImport` opportunistically
    during NativeAOT hardening A2.
  - Use `SuppressGCTransition` only for tiny, high-frequency calls that
    measurement identifies.
- **New GC tuning.**
  - Keep the existing `EngineMemoryPolicy` profiles.
  - The outstanding work is the hardware profiler capture already listed in the
    [GC closeout](../../runtime/gc-hot-path-memory-control-2026-07-02.md).

## 6. Sequencing And Ownership

1. **Start now:** §4.3 and §4.1. They are small and independent, and both
   support hardening A1.
2. **Decide before world packages accept executable payloads:** §4.7.
3. **Implement with ECS avatar networking:** §4.2.
4. **Implement before expanding A1 codecs and accessors:** §4.4. This step needs
   dependency approval.
5. **Split the GPU layout work:** §4.6 step 1 can start at any time; step 2
   follows §4.4.
6. **Wait for the avatar stall investigation to close:** §4.5. Begin with the
   benchmark and prototype.

All proposals are tracked in the
[Runtime Data Layout And Generated Contracts TODO](../../todo/runtime/runtime-data-layout-and-generated-contracts-todo.md).
The NativeAOT hardening TODO consumes its generator, codec, and parity
infrastructure, and continues to own strict-publication acceptance.

## 7. Open Questions

- **Archive lifetime (§4.1):** should published archive handles stay mapped for
  the process lifetime, or for each content root's lifetime with eviction?
- **Server pose handling (§4.2):** must the server decode pose payloads for
  validation, or can it forward opaque payload bytes after validating the
  header?
- **Transform-store chunking (§4.5):** what is the target transform population
  per instance? The answer determines whether a world uses fixed-size chunks or
  one growable array, and fixed-size chunks are the part of CAPCOM's page idea
  that applies here.
- **Creator behavior (§4.7):** what creator behavior must v1 support, and does
  it require option B before v1?
