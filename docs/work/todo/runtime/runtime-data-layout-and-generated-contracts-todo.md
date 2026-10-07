# Runtime Data Layout And Generated Contracts TODO

Created: 2026-10-02

Owner: Runtime / Assets / Networking / Rendering / AOT

Status: Paused at the owner's 2026-10-02 wrap-up request; implementation is
substantially advanced but not complete. The owner requested code changes before
the measured validation pass. See the
[progress ledger](../../progress/runtime/runtime-data-layout-and-generated-contracts-progress.md)
for recovered implementation state and outstanding work. Unchecked acceptance
gates remain open; code presence alone does not close them.

## Current Checkpoint — 2026-10-02

Checked implementation boxes mean the code is present at an integration
checkpoint. They do not certify performance, runtime behavior, NativeAOT
acceptance, or headset parity. Validation boxes remain unchecked unless the
corresponding operation was actually performed.

Implemented: binary client/server state changes and high-rate packet paths;
versioned cooked envelopes, shared archive handles, span readers and payload
leases; downloaded-content policy guards; scoped development parity diagnostics;
semantic factory generation and generated-contract registrations; generated GPU
records with compiled layout reflection; active dense transform storage; and
repeatable asset/network/transform measurement tooling.

Remaining **code work**, before the validation pass:

- **G/P:** safely activate generated component constructors while preserving
  SceneNode attachment before derived constructors. The previous construction
  order is retained, with a parity diagnostic on reflection. Finish generated
  player formatter/accessor coverage, metadata tables and scan removal,
  missing-path/open-generic diagnostics, and payload-body generation. Typed
  registration around an existing serializer is not full body generation.
- **C:** implement direct-span native GDeflate/hardware LZ4 overrides; their
  compatibility defaults still allocate full arrays. Finish texture source/YAML
  lease extraction. The archive lease lifetime and pool LOH-rounding fixes are
  already present.
- **L:** complete the all-shader-bound-record inventory and any remaining
  unconverted families recorded in L; a passing reflected subset does not
  establish full engine coverage.

Remaining **validation/evidence**: asset/network/transform baselines and numeric
noise/target budgets; real two-client traffic and pose application; parity
play-mode smoke; asset churn and allocation measurements; existing test lanes
and planned tests after live validation; refreshed AOT warnings and build-cost
measurements; OpenGL/Vulkan visual, physics, and editor behavior; VR on hardware.
No performance or visual acceptance is claimed.

Checkpoint evidence: Editor and the generator-related narrow builds compile;
the 50 covered GPU records pass actual Shaderc/SPIRVCross reflection, and a
scratch member reorder is rejected with the expected offset diagnostic.
Detailed commands and final compile results are in the progress ledger.

Resume from the explicit section checkpoint notes and unchecked boxes below,
using the commands in
[Runtime Data Layout Measurements](../../../developer-guides/diagnostics/runtime-data-layout-measurements.md).
Do not restart the recovered implementation or treat the tracker as complete.

Design: [Runtime Data Layout And Generated Contracts](../../design/runtime/runtime-data-layout-and-generated-contracts-design.md)

Related work:

- [Runtime Regression And NativeAOT Hardening TODO](../runtime-regression-and-nativeaot-hardening-todo.md).
  Its A1 and A2 items consume the generator, codec, and parity infrastructure
  built here. Its strict-publication acceptance stays there.
- [Generic ECS And Humanoid Avatar Networking Design](../../design/runtime/generic-ecs-avatar-networking-design.md)
- [Avatar Scene Publication Stalls](../../investigations/asset-import/avatar-scene-publication-stalls-2026-09-24.md)
- [Dynamic Indirect Material Bindings](../../design/rendering/dynamic-indirect-material-bindings.md)
- [Texture Runtime Streaming And Virtual Texturing TODO](../texturing/texture-runtime-streaming-virtual-texturing-todo.md)

## Goal

Remove the specific runtime costs and correctness risks identified in the
design:

1. Load cooked assets without transient full-payload copies, and stop
   reopening archives for every asset.
2. Send realtime state changes as typed binary payloads, and route
   high-rate pose traffic through the existing zero-allocation codecs.
3. Make development play mode report any player-path behavior that will fail
   under NativeAOT.
4. Generate runtime type contracts from semantic type information instead of
   parsing C# with regular expressions.
5. Define each GPU record layout once, and validate shader layouts against
   their C# counterparts.
6. Store transform hierarchy state densely for bulk propagation and render
   publication.
7. Record and enforce the policy for executable content in downloaded worlds
   and avatars.

This tracker is complete only when every completion gate below passes with
measured evidence. A workstream is not finished because code exists.

## Approval Record

The owner approved the following gated changes on 2026-10-02 in the session
that produced this tracker:

| Change | Approval scope | Required process |
|---|---|---|
| Cooked archive and asset-envelope format | Approved as a breaking change. Older cooked content is rejected and must be cooked again. | Version bump, actionable rejection diagnostic, and updated architecture document. |
| Realtime wire format for state changes and high-rate channels | Approved as a breaking protocol change. | Protocol-version bump, version-checked admission, and updated networking guide. |
| `Microsoft.CodeAnalysis.CSharp` and `Microsoft.CodeAnalysis.Analyzers` as analyzer-only build dependencies | Approved for the source-generator project. | Run `pwsh Tools/Reports/Generate-Dependencies.ps1`, then review and commit `docs/DEPENDENCIES.md` and `docs/licenses/`. |

This approval does **not** cover:

- a script VM, a WASM runtime, or any other native dependency;
- submodule bumps;
- upgrades or replacements of existing packages, including MemoryPack,
  SPIRVCross.NET, and Silk.NET;
- licensing-document changes;
- any later gated change not listed above.

Request a separate approval for each of those.

## Workstream Map

| ID | Workstream | Depends on | Design | Status |
|---|---|---|---|---|
| B | Baseline and evidence | — | — | RenderBench and local-session tooling implemented; measurements pending |
| D | Executable downloaded-content policy | — | §4.7 | Implementation present; validation pending |
| P | Development-mode AOT parity diagnostics | B | §4.3 | Diagnostics and classification present; contract gaps and smoke pending |
| C | Cooked asset loads without transient copies | B | §4.1 | Implementation present; validation pending |
| N | Binary realtime payloads and zero-allocation pose path | B; coordinate with ECS design | §4.2 | Client/server integration resumed; runtime measurements pending |
| G | Semantic source generator for runtime contracts | P to measure coverage | §4.4 | Semantic factories default; generated registrations advanced; player coverage and body generation incomplete |
| L | Single-source GPU record layouts | B; L3 also depends on G | §4.6 | Generated records, cook/debug hooks and reflected checks implemented; full inventory and live validation pending |
| T | Dense transform hierarchy storage | B; avatar stall investigation closed | §4.5 | Runtime integration implemented; prerequisites, measurements and validation pending |

## Scope Boundaries

### In Scope

- cooked archive reading, the asset envelope, published-asset registry
  delegates, and the split between published and reflective readers;
- realtime state-change encoding, high-rate pose and replication channels,
  server relay, and protocol versioning;
- development-mode parity diagnostics at reflective fallback sites;
- a Roslyn incremental generator, its migration from the PowerShell generator,
  and generated codecs, accessors, bindings, and factories;
- shader-block layout reflection, validation, and generated GLSL record
  declarations;
- the dense transform store, transform propagation, render publication, and
  transform event and lock migration;
- world-package validation against executable payloads, plus the policy
  document.

### Explicitly Out Of Scope

- a C#-to-native translation pipeline;
- a custom managed allocator or collector;
- adopting REDox;
- a broad interop rewrite;
- new GC tuning profiles;
- implementing a script VM or another sandboxed runtime, which needs its own
  design, TODO, and dependency approval;
- migrating transforms to ECS. The transform store remains scene-graph storage;
- Vulkan backend work beyond shader-layout validation and transform-record
  publication;
- strict NativeAOT publication acceptance, which remains in the hardening TODO;
- claiming physical-headset or hardware results that were not observed on the
  required device.

## Engineering Principles

- Measure before and after. Each workstream records its baseline in B and
  reports results against that baseline.
- Do not put workstream IDs, TODO references, or plan-phase names in code,
  comments, XML summaries, identifiers, diagnostics, or change summaries.
- Hot paths must not allocate after warmup. That includes render submission,
  visible collection, fixed and frame update, network send and receive, and
  transform propagation.
- Do not use LINQ, captured closures, boxing, or non-struct enumerators in new
  hot-path code.
- Fallbacks must remain visible. Do not replace a failing generated or
  registered path with a silent reflective path.
- Make breaking format and protocol changes explicitly. Version every format,
  reject incompatible inputs with an actionable diagnostic, and do not keep
  compatibility shims before v1.
- In `XRBase` descendants, use `SetField(...)` for every touched setter or
  mutation path.
- Introduce no compiler warnings. Do not suppress IL2xxx or IL3xxx diagnostics
  broadly.
- Do not modify or revert concurrent work outside this tracker, including
  in-flight Vulkan frame-loop changes.
- Put scratch evidence under `Build/_AgentValidation/<run>/`. Copy durable
  findings into the progress ledger.
- Add tests for new contracts during validation, after the implementation
  works through its live path. Follow the validation sequencing in `AGENTS.md`.

## Baseline Evidence At Creation

The design's findings are reproduced here. Revalidate them in B before relying
on them.

| Area | Finding | Source |
|---|---|---|
| Asset load | Each published load maps the archive and re-parses its header, footer, string dictionary, and TOC. It then decompresses into a new `byte[]`, and MemoryPack materializes `CookedAssetBlob.Payload` as a second `byte[]`. | `XREngine.Runtime.Core/Assets/AssetManager.Published.cs`, `XREngine.Data/Core/Files/AssetPacker/AssetPacker.cs`, `XREngine.Data/Core/Files/CookedAssetBlob.cs` |
| Asset load | A second archive reader duplicates the format constants and parsing. | `XREngine.Data/Core/Files/AssetArchiveReader.cs` |
| Asset load | Registry delegates accept and return `byte[]`, although the runtime serializer accepts `ReadOnlySpan<byte>`. | `XREngine.Data/Core/Files/PublishedCookedAssetRegistry.cs`, `RuntimeCookedBinarySerializer.cs` |
| Networking | `StateChangeInfo.Data` is a string produced by Json.NET and then wrapped by MemoryPack. Of 17 payload types, 16 are already `[MemoryPackable]`; `RemoteJobRequest` and `RemoteJobResponse` are not. | `XREngine.Runtime.Core/Networking/StateChangeInfo.cs`, `StateChangePayloadSerializer.cs` |
| Networking | `VRIKSolverComponent` builds a `HumanoidPoseFrame` and sends it through Json.NET, encoding its byte payload as base64. `HumanoidPoseSpanPacketWriter`, `HumanoidPosePacketCursor`, `RealtimePacketSendRing`, and `PersistentReceiveSlabPool` have no runtime consumer outside tests. | `XREngine.Runtime.AnimationIntegration/.../IK/VRIKSolverComponent.cs`, `XREngine.Runtime.Core/Networking/HumanoidPoseSync.cs`, `RealtimeReplicationBuffers.cs` |
| AOT | Strict analysis reports 913 IL2xxx/IL3xxx rows, including 424 cooked-binary rows. The 2026-08-28 snapshot has not been refreshed. | [Hardening TODO](../runtime-regression-and-nativeaot-hardening-todo.md#baseline-evidence) |
| Generator | `Tools/Generate-AotFactoryRegistrations.ps1` parses C# with regular expressions. `Build/Registration/FactoryRegistrations.targets` runs it for Host, Bootstrap, and Browser; `XREngine.Runtime.Rendering.csproj` runs it with `-CommandsOnly`. | as named |
| Parity | Reflective fallbacks include assembly scans in `CookedAssetTypeReference.cs`, `AotRuntimeMetadataStore.cs`, `RuntimeCookedBinarySerializer.cs`, `CookedBinarySerializer.cs`, and `PolymorphicYamlNodeDeserializer.cs`. `AnimationMember.cs` uses reflective member binding, and 16 `Activator.CreateInstance` sites exist in Runtime.Core, Data, and Animation. | as named |
| GPU layouts | Advanced records exist as C# structs, as GLSL structs in `Build/CommonAssets/Shaders/Advanced/Access/*.glslinc`, and as expected constants in `GPUSceneLayoutContract.cs` and `AdvancedGpuSceneRecordContractTests.cs`. No check compares the compiled shader layout with C#. `SPIRVCross.NET` and `Silk.NET.Shaderc` are already referenced. | as named |
| Transforms | Avatar entry spent 204.3 ms in `ProcessDirtyTransforms` and recorded 390,165 render-matrix applications. After batching, peaks were 15.4 ms and ordinary samples 3–4 ms. Each `TransformBase` has 10 `Matrix4x4` fields, 6 lock objects, and 5 matrix events. There are 27 direct subclasses. | [Avatar stall investigation](../../investigations/asset-import/avatar-scene-publication-stalls-2026-09-24.md), `XREngine.Runtime.Core/Scene/Transforms/`, `XREngine.Runtime.Core/World/RuntimeWorld.Transforms.cs` |
| Downloads | World packages contain data and metadata (`WorldPackageManifest`). Verification does not reject executable payloads. | `XREngine.ControlPlane/WorldPackageManifest.cs`, `WorldPackageManifestBuilder.cs` |

## B - Baseline And Evidence

- [x] Run `pwsh Tools/Limit-AgentValidation.ps1 -ReserveTaskRun`, then create
      one run root: `Build/_AgentValidation/<yyyyMMdd-HHmmss>-runtime-data-layout/`.
- [x] Create the progress ledger at
      `docs/work/progress/runtime/runtime-data-layout-and-generated-contracts-progress.md`
      using the
      [memory investigation template](../../testing/memory-control-investigation-template.md)
      where it applies.
- [x] Record the commit, working-tree state, and concurrent out-of-scope files.
- [x] Add one repeatable headless entry point for measurements, reusing the AOT
      smoke host or RenderBench host. Record its exact command line.
- [ ] Measure asset loads:
  - [ ] Load MonkeyBall content and one representative imported avatar,
        identified by asset identity rather than a local path.
  - [ ] For each load, record total allocated bytes, LOH allocation bytes and
        count from `GC.GetGCMemoryInfo`, gen0/1/2 counts, wall time, and
        archive-open count.
  - [ ] Load and unload the avatar 20 times. Record LOH size and gen2 count
        after each cycle.
- [ ] Measure networking in a local two-client session plus a synthetic
      harness with 1, 8, and 32 avatars:
  - [ ] bytes per pose packet;
  - [ ] managed allocations per send tick and receive tick;
  - [ ] server relay cost.
- [ ] Measure transforms using the avatar-import scenario from the stall
      investigation and the RenderBench production scene with 1, 8, and 32
      animated avatars:
  - [ ] transforms per frame and dirty count;
  - [ ] `ProcessDirtyTransforms` p50, p95, and maximum;
  - [ ] render-matrix applications;
  - [ ] canonical publication time;
  - [ ] allocation-scope bytes.
- [ ] Refresh the AOT warning inventory with
      `powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Publish-MonkeyBallVR.ps1 -AllowAotWarnings`.
      Use `-AllowAotWarnings` for diagnosis only, then record counts by
      cluster.
- [ ] Inventory every shader-bound GPU layout. For each, record its C# struct,
      shader struct and block, include file, binding, layout rule, and
      expected-constant location.

Acceptance criteria:

- [ ] Every baseline has an exact command, environment, and archived output.
- [ ] Two consecutive runs of each measurement differ by no more than the noise
      band recorded in the ledger.

## D - Executable Downloaded-Content Policy

Implementation checkpoint: policy and package/loader guards are present, including
fail-closed unreadable files and unsafe paths. Rejection tests and end-to-end
publication acceptance remain pending.

- [x] Write
      `docs/architecture/runtime/downloadable-content-execution-policy.md`
      with these decisions:
  - [x] **Current policy:** downloaded worlds and avatars carry data only.
        Behavior comes from engine-compiled, statically registered components.
  - [x] **Target direction:** compile a restricted C# subset to IL, verify it
        against an allowlist, translate it to bytecode, and run it in a metered
        engine VM. Visual graphs target the same VM.
  - [x] **Rejected for now:** an embedded WASM runtime and an out-of-process
        script host. Record why.
  - [x] **Constraints:** NativeAOT cannot load new assemblies, and
        `AssemblyLoadContext` is not a security boundary.
- [x] Enforce data-only packages in `WorldPackageManifestBuilder`:
  - [x] Reject files by content signature, not extension alone: PE files with or
        without a CLI header, ELF files, and Mach-O files.
  - [x] Also reject `.dll`, `.exe`, `.so`, `.dylib`, `.cs`, `.csproj`, `.ps1`,
        `.bat`, `.cmd`, and `.sh` files.
  - [x] Apply the same checks in `CreateFromDirectory`, `Verify`, and
        `StageVerified`.
  - [x] Each diagnostic names the file, the detected kind, and the policy
        document.
- [x] Make the control plane reject non-compliant packages in
      `LocalPackageCatalog` and `ManagedContentEndpoints` before staging or
      serving them.
- [x] Verify that `WorldPackageManifest.GameBootstrapId` resolves only to a
      bootstrap compiled into the player, never to package content. Document
      that contract.
- [x] Verify that published runtimes cannot reach `GameCSProjLoader` or any
      other managed-assembly loading path for downloaded content roots. Add a
      guard with a diagnostic if one can.
- [x] Update the design docs:
  - [x] Change the networking design's client boot flow from "binaries/assets"
        to "assets," and link the policy.
  - [x] Link the policy from the source-backed script components design.
- [x] Record the open v1 question: what creator behavior must be supported, and
      must the VM exist before v1? If the answer is yes, open a separate VM
      design and TODO.
- [ ] Validation tests:
  - [ ] A PE file renamed to `.bin` is rejected.
  - [ ] An ELF file is rejected.
  - [ ] A normal cooked package is accepted.
  - [ ] Staging rejects a manifest that was edited after verification.

Acceptance criteria:

- [ ] The policy document exists and is linked from the networking, scripting,
      and control-plane docs.
- [ ] Package creation, verification, staging, and catalog loading all reject
      executable payloads with actionable diagnostics.
- [ ] No published-runtime path loads managed code from downloaded content.

## P - Development-Mode AOT Parity Diagnostics

Implementation checkpoint: scoped diagnostics and the
[reflective-site classification](../../progress/runtime/runtime-reflective-site-classification.md)
are present. Error mode throws on every attempt; editor work is not classified by
a global play-session flag. Remaining implementation: migrate component construction
without changing pre-constructor scene-node access, and finish generated contracts
for the recorded reflective gaps. Strict smoke and warning evidence remain pending.

- [x] Define `XRE_AOT_PARITY=off|warn|error`:
  - [x] The default is `off` for interactive editor sessions and `error` for
        the unit-test lane and parity smoke.
  - [x] Published AOT builds ignore it because reflective fallbacks are already
        unavailable there.
- [x] Add one owner type in `XREngine.Data`, where most fallback sites live.
      It provides the mode, a deduplicated per-type and per-category log, and a
      dedicated exception type for `error` mode.
- [x] Define the player-path scope precisely. It is an ambient scope entered
      by:
  - [x] published-content loads;
  - [x] play-mode world begin-play and component construction;
  - [x] cooked snapshot loads.

      Editor import, inspector, YAML editing, and cooking never enter it.
- [x] Include these fields in each diagnostic:
  - [x] type full name;
  - [x] category: type-resolution scan, reflective cooked deserialization,
        reflective factory, reflective member binding, or polymorphic YAML
        scan;
  - [x] call-site owner;
  - [x] remediation, naming the registration or generated contract that is
        missing.
- [x] Instrument these fallback sites:
  - [x] the assembly scan in `XREngine.Data/Core/Files/CookedAssetTypeReference.cs`;
  - [x] the assembly scan in `XREngine.Data/Serialization/AotRuntimeMetadataStore.cs`;
  - [x] the assembly scan in `XREngine.Data/Core/Files/RuntimeCookedBinarySerializer.cs`;
  - [x] the reflective `BinaryV1` deserialization and assembly scan in
        `XREngine.Data/Core/Files/CookedBinary/CookedBinarySerializer.cs`;
  - [x] `XREngine.Data/Serialization/Yaml/PolymorphicYamlNodeDeserializer.cs`,
        but only if B or A0 shows that runtime YAML is reachable in the player;
  - [x] reflective property, field, and method binding in
        `XREngine.Animation/Property/Core/AnimationMember.cs`;
  - [x] each `Activator.CreateInstance` or `Type.GetType` call classified as a
        player-path site.
- [x] Classify every reflective site as player-path or authoring-only. Record
      the table in the ledger and share it with hardening item A0.
- [ ] Add a parity smoke that enters MonkeyBall play mode in an isolated editor
      session with `XRE_AOT_PARITY=error`. Archive the logs.
- [ ] For each gap found, add the registration or generated contract that
      removes it, using G when it exists. Do not add an allowlist or
      suppression.
- [x] Document `XRE_AOT_PARITY` in
      [AOT Final Game Builds](../../../developer-guides/runtime/aot-final-game-builds.md)
      and in the environment-variable references that list `XRE_*` settings.

Acceptance criteria:

- [ ] MonkeyBall play mode and the unit-test lane report zero parity
      diagnostics in `error` mode.
- [ ] Strict NativeAOT warning counts do not increase.
- [ ] Published-runtime behavior is unchanged.

## C - Cooked Asset Loads Without Transient Copies

Implementation checkpoint: retained archive mappings, shared exactly-once lease
ownership, pinned-owner retention, envelope/registry migration, and the published
reader split are present. Managed pooled buffers stop at 64 KiB to avoid pool
rounding into the LOH. Remaining implementation: native GDeflate and hardware LZ4
span overrides still use default array fallbacks; finish direct destination writes
and remaining texture source/YAML extraction contracts. Lifetime tests, load/churn
measurements, and warning refresh remain pending.

### C1 - Long-Lived Archive Handles

- [x] Add a published archive handle under
      `XREngine.Data/Core/Files/AssetPacker/`. It owns the file map, header,
      footer, string dictionary, and TOC lookup structure for all three lookup
      modes: hash buckets, sorted-by-hash, and linear.
- [x] Make lookups thread-safe and read-only. Use after disposal must raise a
      clear diagnostic.
- [x] Remove duplicated format parsing. Rebuild `AssetArchiveReader` on the
      shared reader, or delete it, keeping one set of magic, version, footer,
      and TOC definitions.
- [x] Keep one registry of open archives for each published content root:
      config, game content, and common assets. Open each archive once, and close
      it when the runtime shuts down or swaps content.
- [x] Route `AssetManager.Published` through these handles, including
      `EnumeratePublishedArchiveRequests` resolution.
- [x] Keep a one-shot API for tooling such as `ProjectBuilder`, implemented on
      a temporary handle.
- [x] Replace `AotRuntimeMetadataStore.MetadataArchiveReader`, currently
      `Func<string, string, byte[]>`, with the handle-based reader.

### C2 - Payload Leases

- [x] Define the lease type:
  - [x] Use a synchronous `ref struct` for normal deserialization.
  - [x] Use an explicit owner object for the rare transfer across a job
        boundary.
  - [x] Document both lifetimes.
- [x] Return a span over the mapped file for stored entries, without copying.
- [ ] Decompress compressed entries into a lease-owned buffer:
  - [x] Add a `Compression.Decompress(ReadOnlySpan<byte>, CompressionCodec,
        Span<byte>)` overload that writes into the caller's buffer.
  - [ ] Verify that every codec supports writing to a span destination.
- [x] Back leases with bounded pooled storage. Above a documented threshold,
      use native memory so temporary payloads never reach the LOH.
- [x] Expose retained capacity, rent and miss counts, and high-water marks in
      the existing pool statistics.
- [x] Balance native allocations with `NativeMemoryPressureTracker`.

### C3 - Cooked Envelope V2

- [x] Define a fixed binary header containing magic, envelope version,
      `CookedAssetFormat`, length-prefixed type-reference bytes, payload
      offset, and payload length.
- [x] Choose the type-reference encoding in the same format change. This
      resolves the `CookedAssetBlob.TypeName` naming item in
      [cooked-asset-aot-and-io.md](../../../architecture/assets/cooked-asset-aot-and-io.md#6-open-items).
- [x] Emit V2 from both writer sites in `XREngine.Editor/ProjectBuilder.cs`:
      `RuntimeBinaryV1` and `BinaryV1`.
- [x] Parse the header from a span and slice the payload without
      materializing a `byte[]`.
- [x] Bump the archive or envelope version. Older content must fail with one
      actionable diagnostic that tells the user to re-cook content.
- [x] Update the launcher code generated in `XREngine.Editor/CodeManager.cs`.
      It currently emits `AssetPacker.GetAsset` and
      `CookedAssetReader.LoadAsset(bytes, ...)`.

### C4 - Span-Based Registry Delegates

- [x] Change the deserializer delegate to
      `(ReadOnlySpan<byte> payload, Type assetType)`, and change the serializer
      to write to `IBufferWriter<byte>`.
- [x] Update every registration owner:
  - [x] `XREngine.Runtime.Rendering/Core/Files/PublishedCookedAssetRegistryRegistration.cs`
        for `XRMesh` and `XRTexture2D`;
  - [x] `XREngine.Animation/Serialization/AnimationPublishedCookedAssetRegistration.cs`;
  - [x] `XREngine.Data/Core/Files/DataPublishedCookedAssetRegistration.cs`;
  - [x] `XREngine.Runtime.Host/Serialization/BootstrapPublishedCookedAssetRegistration.cs`;
  - [x] `Samples/MonkeyBallVR/Assets/Scripts/MonkeyBallRuntimeRegistration.cs`.
- [x] Replace the linear full-name scan under lock in
      `TryResolveByFullName` with a dictionary maintained during registration.

### C5 - Published Reader Split

- [x] Separate the published `RuntimeBinaryV1` reader from the editor and
      development `BinaryV1` reflective reader.
- [x] Remove `RequiresUnreferencedCode` and `RequiresDynamicCode` from the
      published path, including the `AssetManager.Published` load methods.
      Keep those annotations only on the authoring reader.
- [ ] Record the change in the strict warning report, using the B baseline
      cluster counts.

### C6 - Texture-Streaming Payloads

- [ ] Convert payload creation and extraction in
      `XREngine.Runtime.Rendering/Objects/Textures/2D/XRTexture2D.StreamingPayload.cs`
      to span or lease contracts. This includes creating payloads, reading
      source files with `ReadAllBytes`, and extracting payloads from YAML
      assets.
- [ ] Coordinate this change with the texture-streaming TODO owner. Do not
      change streaming policy here.

### C Validation

- [ ] Add unit tests for:
  - [ ] each archive lookup mode through the handle;
  - [ ] lease lifetime and use-after-dispose diagnostics;
  - [ ] envelope V2 round trips;
  - [ ] rejection of older versions;
  - [ ] concurrent lookups.
- [ ] Repeat the B asset-load and 20-cycle avatar measurements, and record the
      differences.
- [ ] Run the MonkeyBall AOT smoke with `-AllowAotWarnings` until the strict
      gate is owned by the hardening TODO, and record the warning delta.
- [ ] Load cooked content in the editor and confirm that it still renders, using
      the isolated-session loop.

Acceptance criteria:

- [ ] Each archive is opened once per content root for the life of that root.
- [ ] Except for the final asset storage, the load path makes zero transient
      LOH allocations for each asset.
- [ ] Repeated avatar load and unload cycles show bounded LOH growth, with the
      bound recorded in the ledger.
- [ ] The published load path carries no reflection or dynamic-code
      annotations.
- [ ] Older cooked content fails with one actionable diagnostic.
- [ ] [cooked-asset-aot-and-io.md](../../../architecture/assets/cooked-asset-aot-and-io.md)
      and [AOT Final Game Builds](../../../developer-guides/runtime/aot-final-game-builds.md)
      describe the new format and APIs.

## N - Binary Realtime Payloads And Zero-Allocation Pose Path

### N1 - State-Change Contract

- [x] Replace the MemoryPack `StateChangeInfo` wrapper and its string `Data`
      with a fixed packet sub-header containing the state-change type, payload
      length, and payload bytes. Write it directly into the packet buffer.
- [x] Register one codec for each `EStateChangeType`, writing to
      `IBufferWriter<byte>` and reading from `ReadOnlySpan<byte>`:
  - [x] Use MemoryPack-generated formatters for the 16 types already marked
        `[MemoryPackable]`.
  - [x] Choose an encoding for `RemoteJobRequest` and `RemoteJobResponse`:
        MemoryPackable with explicit payload bytes, or a separate tooling
        channel. Json.NET may not remain in the realtime path.
- [x] Replace `MaxEncodedPayloadCharacters`, a 262,144-character string limit,
      with a byte limit enforced before decoding.
- [x] Delete `StateChangePayloadSerializer`, and remove Json.NET from
      `XREngine.Runtime.Core/Networking/`.

### N2 - Send And Receive Sites

- [x] Convert these send sites in `BaseNetworkingManager.cs`:
  - [x] `ReplicateStateChange`;
  - [x] `BroadcastRemoteJobRequest` and `BroadcastRemoteJobResponse`;
  - [x] `BroadcastServerError`;
  - [x] `BroadcastHumanoidPoseFrame`;
  - [x] `BroadcastAuthorityLeaseUpdate`;
  - [x] `BroadcastClockSync` and `SendClockSyncTo`;
  - [x] `BroadcastReplicationSnapshot` and `BroadcastReplicationDelta`;
  - [x] `BroadcastStateChange<T>`, `SendStateChangeTo<T>`, and
        `BroadcastStateChangeToTargets<T>`.
- [x] Convert these receive sites:
  - [x] the state-change dispatch in `BaseNetworkingManager.cs`;
  - [x] the player-assignment, transform, leave, error, lease, and clock-sync
        handlers in `ClientNetworkingManager.cs`;
  - [x] the baseline, delta, and sync-complete handlers in
        `ClientNetworkingManager.Replication.cs`;
  - [x] the input, heartbeat, and leave handlers in
        `ServerNetworkingManager.ManagedTransport.cs`.
- [x] Reject truncated, oversized, and unknown-type payloads with a counted
      diagnostic. Decoder exceptions must not escape the receive loop.

### N3 - High-Rate Channels

- [x] In `VRIKSolverComponent`, replace the per-send
      `HumanoidPosePacketBuilder` and `HumanoidPoseFrame` allocations.
      Write with `HumanoidPoseSpanPacketWriter` into a
      `RealtimePacketSendRing` slot.
- [x] Decode received poses from `PersistentReceiveSlabPool` slabs with
      `HumanoidPosePacketCursor`. Replace the `HumanoidPoseFrameReceived`
      event payload with a span-based or pooled view, and update
      `OnHumanoidPoseFrame`, `TryApplyReceivedBaseline`, and
      `TryApplyReceivedDelta`.
- [x] Delete `ApplyLegacyPoseFrame` if nothing else needs it.
- [x] Decide and record whether the server relay in
      `ServerNetworkingManager.cs` decodes and validates poses, or validates
      the header and authority lease before forwarding opaque bytes. Implement
      that decision without per-packet allocation.
- [x] Move clock synchronization and replication deltas to binary codecs on the
      realtime ring.
- [x] Coordinate with the generic ECS avatar networking design:
  - [ ] If ECS pose ownership has started, implement this path there. (Not applicable: ownership remains in `HumanoidPoseSync`.)
  - [x] Otherwise, implement it in `HumanoidPoseSync` and leave one ownership
        seam for ECS.
  - [x] Do not move pose ownership twice.

### N4 - Protocol Version

- [x] Add an explicit wire-protocol version that is independent of the build
      version string. `MultiplayerContracts.ProtocolVersion` currently
      defaults to `"dev"`. Increment the protocol version for this change.
- [x] Make join and admission reject mismatched protocol versions with a
      diagnostic that names both versions.
- [x] Update the state-change payload description in the
      [networking guide](../../../developer-guides/networking/networking.md).

### N Validation

- [ ] Add allocation tests that use the `RuntimeMemoryControlTests` pattern.
      After warmup, sending and receiving poses, replication deltas, and clock
      synchronization must allocate zero bytes per tick.
- [ ] Add malformed-payload tests for truncated, oversized, unknown-type, and
      mismatched-protocol inputs.
- [ ] Repeat the B networking measurements and record bytes per avatar packet
      and allocations per tick.
- [ ] Run a local two-client session plus a server relay, following the
      networking guide. Confirm that remote avatar poses apply correctly.

Acceptance criteria:

- [x] The realtime runtime networking path has no Json.NET use.
- [ ] Pose, replication-delta, and clock-sync send and receive paths make zero
      managed allocations after warmup.
- [ ] Pose packet size is no larger than the binary codec size, without base64
      inflation.
- [ ] Mismatched protocols are rejected at admission.

## G - Semantic Source Generator For Runtime Contracts

### G1 - Generator Project

- [x] Create `XREngine.SourceGenerators/XREngine.SourceGenerators.csproj`:
  - [x] target `netstandard2.0`;
  - [x] set `IsRoslynComponent` and `EnforceExtendedAnalyzerRules`;
  - [x] reference `Microsoft.CodeAnalysis.CSharp` and
        `Microsoft.CodeAnalysis.Analyzers` with `PrivateAssets=all`.
- [x] Add the project to `XRENGINE.slnx`.
- [x] Choose Roslyn package versions supported by the repository's .NET 10
      SDK compiler, and record why.
- [x] Reference the generator from runtime projects as an analyzer through one
      shared props or targets file. Use `OutputItemType="Analyzer"` and
      `ReferenceOutputAssembly="false"`.
- [ ] Run `pwsh Tools/Reports/Generate-Dependencies.ps1`, then review and commit
      `docs/DEPENDENCIES.md` and `docs/licenses/`.

### G2 - Factory-Generation Parity

- [x] Port the `Desktop` and `Portable` factory-registration modes from
      `Tools/Generate-AotFactoryRegistrations.ps1`.
- [x] Port the Rendering `-CommandsOnly` render-command registration.
- [x] Decide whether to port `BrowserManifest` mode, which uses
      `Build/Registration/BrowserStaticRegistrations.template.txt`, or keep it
      on its current path with a documented reason.
- [ ] Add a parity test that compares registered type sets from the PowerShell
      and Roslyn outputs on the current tree. Require equality.
- [x] After parity passes, remove the replaced PowerShell modes from:
  - [x] `Build/Registration/FactoryRegistrations.targets`;
  - [x] the `GenerateRenderCommandRegistrations` target in
        `XREngine.Runtime.Rendering.csproj`;
  - [ ] the script, if no mode remains.
- [x] Update `XREngine.UnitTests/Rendering/RuntimeModularizationPhase6BoundaryTests.cs`.
      It reads the PowerShell generator directly, so it must test the
      generator contract instead of the script text.
- [x] Update [AOT Final Game Builds](../../../developer-guides/runtime/aot-final-game-builds.md)
      and [Runtime Project Organization](../../../architecture/runtime/project-organization.md).

### G3 - Contract Model

- [x] Define the marker attributes and specify exactly what each one generates.
      Reuse existing markers where they already express the contract.
- [x] Define a stable type ID and schema version. Report collisions and
      unversioned schema changes at compile time.
- [ ] Add generator diagnostics, using a reserved ID prefix, for:
  - [x] unsupported member types;
  - [ ] a missing construction path;
  - [ ] open generics without closed registrations;
  - [x] a player-assembly contract that references an editor-only type;
  - [x] duplicate registrations.
- [x] Emit one install method per assembly that returns registration leases,
      using the existing `RegistrationLeaseGroup` pattern. Module installation
      calls it.

### G4 - Generated Outputs

Implement these outputs in A1 order. Each item removes a fallback site listed
in P.

- [ ] Texture-streaming payload codec for `XRTexture2D.StreamingPayload.cs`,
      the largest warning hotspot.
- [ ] Animation property bindings: generated typed delegates for published
      animation targets replace reflective binding in `AnimationMember.cs`.
- [x] Cooked codecs for published asset types, installed into
      `PublishedCookedAssetRegistry` and replacing hand-written registrations
      where possible.
- [ ] Formatters for closed generic collections, nullable values, and value
      tuples.
- [ ] Typed property, field, method, and event accessors required at runtime.
- [ ] Factories for components, transforms, assets, and controllers.
- [ ] Generated tables for `AotRuntimeMetadataStore`, with player-reachable
      assembly scans removed.

### G5 - Determinism And Cost

- [x] Produce deterministic output: stable ordering and file names, with no
      timestamps or machine paths.
- [ ] Measure clean and incremental editor build times before and after. Record
      the agreed budget in the ledger.
- [ ] Ensure generator failures are compile errors with actionable messages,
      not silent omissions.

### G Validation

- [ ] Run the parity test, generator diagnostic tests, and generated codec
      round-trip tests.
- [ ] Run the P parity smoke with `XRE_AOT_PARITY=error`.
- [ ] Refresh the strict warning report and record the delta for each cluster.

Acceptance criteria:

- [ ] No regex-based C# parsing remains in runtime contract generation, apart
      from any `BrowserManifest` exception documented under G2.
- [ ] Every G4 output is generated and installed, and the matching fallback
      sites are unreachable from the player.
- [ ] The cooked-binary and first-party reflection warning clusters fall by the
      recorded amounts. Strict-publication acceptance remains in the hardening
      TODO.
- [ ] Build-time cost stays within the recorded budget.

Current G code checkpoint: the semantic factory generator replaces the Desktop,
Portable, and CommandsOnly script modes after a current-tree type-set comparison
(Host 67/67, Rendering 265/265, Bootstrap 0/0). The comparison is preserved in
`Build/_AgentValidation/20261002-153000-runtime-contracts-resume/scratch/`;
an automated parity test remains open. `BrowserManifest` remains on its
template-backed script path. Per-assembly generated installers register nine
versioned asset type identities, the published Data/Animation/Rendering cooked
codecs, six representative closed formatters, three Transform animation
bindings, and one property, field, method, and event accessor. The closed
formatter and accessor declarations do not yet cover every player-used type
and member; those boxes remain open. The texture registration uses the existing
hand-written payload body in `XRTexture2D.StreamingPayload.cs`, so the payload
code generation box remains open.

Remaining G code: preserve SceneNode-before-derived-constructor semantics while
activating the generated XRComponent factories at `XRComponent.New`; expand
closed formatter and typed member declarations to the actual player contract
set; generate the remaining AotRuntimeMetadataStore table and remove its
player-reachable scan fallback; add compile diagnostics for missing construction
paths and unclosed generics. Then run the parity, codec round-trip, diagnostic,
smoke, warning-delta, and build-cost validation gates above. No acceptance box
is claimed by this checkpoint.

## L - Single-Source GPU Record Layouts

Before starting, read the
[DefaultRenderPipeline invariants](../../../architecture/rendering/default-render-pipeline-notes.md)
and [mesh submission contracts](../../../architecture/rendering/mesh-submission-strategies.md).

### L1 - Inventory

- [ ] Confirm that the B inventory covers:
  - [ ] the `GPUSceneLayoutContract` records: `DrawMetadata`, `TransformGpu`,
        `BoundsGpu`, `MaterialStateGpu`, `MeshDataEntry`, `LODTableEntry`,
        `GPULodTransitionState`, `GpuMeshletRange`, `GpuMeshletDescriptor`,
        `GpuMeshletTaskRecord`, `GPUSortKeyEntry`, `GPUBatchRangeEntry`, and
        `GPUViewBatchClassification`;
  - [ ] the advanced records: handle, lookup, remap, draw, instance,
        transform, deformation, render-state, and editor-identity records,
        plus material, light, shadow, view, mesh, and texture records from the
        `Advanced/Access` includes;
  - [ ] structs governed by `VulkanIndirectCommandLayoutContract`. These follow
        the Vulkan specification rather than shader blocks, so keep their ABI
        size checks.

### L2 - Reflection Validator

- [x] For each inventoried block, compile a validation shader with
      `Silk.NET.Shaderc` through the existing compiler path, or reuse an
      existing compiled shader.
- [x] Reflect member names, offsets, array strides, matrix strides, and struct
      sizes with `SPIRVCross.NET`.
- [x] Compare reflected values with `Marshal.OffsetOf` and `Unsafe.SizeOf` for
      the bound C# record. Make the name-mapping convention explicit through an
      attribute or a single convention table.
- [ ] Run the validator in three places:
  - [ ] a unit test covering every inventoried record;
  - [x] shader cooking in `ProjectBuilder`, where a mismatch fails the cook
        with a diagnostic naming the struct, member, and both offsets;
  - [x] optional debug-start validation.
- [x] Confirm that OpenGL consumers use the same includes and layout
      qualifiers, so one SPIR-V reflection covers both backends. Record any
      block that differs.
- [x] Replace hand-written expected offset constants for shader-bound records
      with the reflected comparison. Keep `GPUSceneLayoutContract` only for
      C#-side self-consistency, if it is still needed.

### L3 - Generated GLSL Declarations

Roslyn generators add C# sources only, so this step needs a separate tool.

- [x] Mark data-only records with a GPU-record attribute.
- [x] Add a repository tool that reflects the attribute and emits GLSL struct
      declarations into a generated include directory under
      `Build/CommonAssets/Shaders/Advanced/`.
- [ ] Check in the generated includes. Add a test that fails when regeneration
      would change them, following the existing settings-generation workflow.
- [x] Apply these mapping rules:
  - [x] `Matrix4x4` maps to `mat4`, with row-major qualification matching the
        `ShaderGenerator` convention.
  - [x] Scalar and `Vector2`/`Vector4` types map directly.
  - [x] Handles map to their GLSL structs.
  - [x] `Vector3` members are rejected unless padding is explicit.
- [x] Make the existing hand-written access helpers include the generated
      declarations, then delete the duplicate struct bodies.
- [x] Share this mechanism with the material-row layout generation in
      [dynamic indirect material bindings](../../design/rendering/dynamic-indirect-material-bindings.md).
      Do not create a second generator.
- [ ] Confirm that renderer hot reload picks up regenerated includes.

### L Validation

The editor validator compiled and reflected 33 advanced and 17 GPU-scene
records through the repository regeneration command. A scratch copy with
`AdvancedDrawRecord.PrimitiveSection` and `Flags` reordered failed the direct
validation command with `shader=68, C#=64`. The unit-test and cook negative
lanes, production shader permutations, and live visual comparison remain open.
The B inventory still needs a row-by-row audit of shader-bound blocks outside
the generated advanced and GPU-scene families, including packed deformation
vertices/jobs, physics-chain particle and dispatch buffers, material rows,
terrain, and other legacy shader snippets. Add fixed-layout validation where
these are engine-owned; leave Vulkan indirect commands under their Vulkan ABI
contract. The new generated declarations for skin influences, view constants,
bone mappings, and texture handles have passed synthetic SPIR-V reflection,
but their production shader permutations and runtime include resolution have
not yet been exercised.

- [ ] **Negative test:** reorder one GLSL member in a scratch copy. The unit test
      and cook must both fail with the expected diagnostic.
- [ ] Run the GPU-scene and advanced contract test lanes.
- [ ] Run the live editor loop on OpenGL and Vulkan. Capture and inspect
      images from at least two camera positions, then compare them with
      pre-change captures.

Acceptance criteria:

- [ ] Every shader-bound record is validated against reflected shader layouts.
- [ ] No hand-maintained duplicate offset constants remain for shader-bound
      records.
- [ ] Generated declarations replace hand-written struct bodies with no visual
      change on OpenGL or Vulkan.

## T - Dense Transform Hierarchy Storage

Implementation checkpoint: the world store, lifecycle, propagation, render-record
publication, accessors, and subclass integration are active and compile through the
editor. No new implementation expansion is pending in this section; baseline/target
closeout and all runtime, allocation, physics, editor and VR validation remain open.

Prerequisites:

- [ ] Close the
      [avatar scene publication stalls investigation](../../investigations/asset-import/avatar-scene-publication-stalls-2026-09-24.md).
- [ ] Complete the B transform baseline.
- [ ] Record numeric targets in the ledger before starting T2.

### T1 - Benchmark Harness

- [x] Make the B transform scenario deterministic and repeatable from one
      command, using avatar import plus RenderBench with 1, 8, and 32 animated
      avatars.
- [x] Add counters for transforms registered, dirty-local, dirty-world,
      propagated, render records published, and events raised. Keep them out of
      hot-path allocations.

### T2 - Store For Propagation And Publication

- [x] Add a per-world `TransformHierarchyStore` addressed by a generational
      `TransformHandle`. Its structure-of-arrays layout contains:
  - [x] parent index;
  - [x] hierarchy order index;
  - [x] local matrix;
  - [x] world matrix;
  - [x] flags for local dirty, world dirty, world override, subscribers, and
        retired entries;
  - [x] the render-publication slot.
- [x] Choose between fixed-size chunks and growable arrays using the target
      population per instance. Record the rationale; this is the design's open
      question.
- [x] Define the handle lifecycle:
  - [x] Acquire a handle when a transform attaches to a world.
  - [x] Release it with a generation bump when the transform detaches or is
        destroyed.
  - [x] Handle moves between worlds explicitly.
  - [x] Reject stale handles with a diagnostic.
- [x] Maintain parent-first order:
  - [x] Insertion, reparenting, and removal mark the order dirty.
  - [x] Reorder in batches at the end of `TransformHierarchyMutationBatch` or
        before propagation.
  - [x] Keep subtree ranges contiguous.
- [x] Propagate with a linear pass over dirty ranges. Parallelize by
      independent root ranges with cached delegates, replacing per-root `Task`
      recursion and closure-capturing `Parallel.ForEach`.
- [x] In `RuntimeWorld.Transforms.cs`, replace depth-bucketed
      `ConcurrentDictionary`/`ConcurrentHashSet` dirty tracking, hash-set
      duplicate removal, and ancestor walks with a dirty bitset and range
      list.
- [x] Define the ordering contract for physics interpolation and VR
      late-latch world overrides. Write it in the transform developer docs.
- [x] Copy dirty ranges directly into `AdvancedTransformRecord` and
      `TransformGpu` tables, and remove per-object render-matrix application.
- [x] Make `TransformBase` world, local, and render matrix accessors read from
      the store, and document their snapshot semantics.
- [ ] Compare against T1 and record the results in the ledger.

### T3 - Object-State Migration

- [x] Replace the six per-instance lock objects with the store's
      generation-based publication: sequence locks over arrays or double
      buffering.
- [x] Raise `LocalMatrixChanged`, `InverseLocalMatrixChanged`,
      `WorldMatrixChanged`, `InverseWorldMatrixChanged`, and
      `RenderMatrixChanged` after the pass. Use the changed list, and raise
      them only for entries with subscribers.
- [x] Track subscriber counts in event add and remove accessors.
- [x] Remove redundant per-instance matrix fields. Compute inverse matrices on
      request, or store them only for transforms that request them.
- [x] Move `_lastReplicatedMatrix` and the related replication delta state to
      the replication owner.
- [x] Use `SetField(...)` in every touched setter.

### T4 - Subclass And Tool Integration

- [x] Audit all 27 direct subclasses, and classify each as either computing a
      local matrix or owning world space. Cover at least
      `RigidBodyTransform`, the VR device and action transforms, and the
      lagged and noise transforms.
- [x] Integrate editor gizmos, transform tools, undo, and inspector editing.
- [x] Confirm that serialization is unaffected because the store contains
      runtime state only.
- [x] Preserve the `ELoopType` sequential, parallel, and asynchronous
      semantics, or deliberately simplify and document them.

### T Validation

- [ ] Run the existing transform tests: `TransformLocalPoseBatchTests`,
      `TransformAccessorFastPathTests`, and `ModelImporterTransformTests`.
- [ ] Add store tests for reparent ordering, generation reuse,
      world-override ordering, and subscriber-only events.
- [ ] Run the live editor loop with `Tools/Manage-McpEditorSession.ps1`:
  - [ ] the avatar-import scenario;
  - [ ] a physics scene;
  - [ ] gizmo editing;
  - [ ] OpenGL and Vulkan captures from multiple positions.
- [ ] Run VR validation on OpenVR hardware when available. Otherwise, mark VR
      rows unqualified rather than passed.
- [ ] After warmup, allocation scopes for propagation and publication must
      report zero bytes.

Acceptance criteria:

- [ ] Peak and steady-state `ProcessDirtyTransforms` costs and render-matrix
      applications meet the targets recorded before T2.
- [ ] Steady-state propagation and publication allocate nothing.
- [ ] Visual, physics, and editor-tool behavior remain at parity. VR is either
      at parity or explicitly unqualified.

## Final Validation Matrix

| Lane | Required evidence |
|---|---|
| Build | Independent builds of touched projects plus `dotnet build XRENGINE.slnx`; zero new warnings or errors |
| Assets | Archive-handle, lease, and envelope tests; B load and churn measurements repeated; AOT smoke |
| Networking | Zero-allocation send and receive tests; malformed-payload tests; local two-client session plus relay; packet sizes |
| Parity | `XRE_AOT_PARITY=error` clean in the unit-test lane and MonkeyBall play mode |
| Generator | Parity with the former PowerShell output, diagnostic tests, codec round trips, and a recorded build-time budget |
| GPU layouts | Reflection validation for every shader-bound record, the negative test, and OpenGL/Vulkan captures |
| Transforms | Benchmark targets met, zero steady-state allocation, live editor captures, physics and gizmo parity, VR result or unqualified status |
| Downloads | Package rejection tests and the linked policy document |
| Dependencies | Dependency report and license files regenerated and reviewed |
| Docs | Architecture, developer-guide, and work-index updates; links valid; `git diff --check` clean |

## Completion Gates

- [ ] B baselines are archived with exact commands.
- [ ] D policy is documented and enforced in package creation, verification,
      staging, and catalog loading.
- [ ] P reports zero parity diagnostics in `error` mode in the unit-test lane
      and MonkeyBall play mode.
- [ ] C makes each archive open once per root, removes transient LOH copies,
      leaves the published reader unannotated, and rejects older content with
      one diagnostic.
- [ ] N removes Json.NET from realtime networking, makes high-rate channels
      allocation-free after warmup, and gates protocol versions at admission.
- [ ] G replaces the PowerShell generation, installs every G4 output, and
      records warning-cluster reductions.
- [ ] L validates every shader-bound record against reflection, leaves no
      duplicate offset constants, and keeps visual parity.
- [ ] T meets its recorded targets, allocates nothing in steady state, and
      keeps parity, with hardware rows qualified or explicitly unqualified.
- [ ] No workstream IDs or TODO references appear in code, comments,
      identifiers, or diagnostics.
- [ ] The progress ledger records commands, results, dispositions, evidence
      paths, and exclusions.
- [ ] The design document's status reflects the outcome. Delete this tracker
      only after every gate passes and its design is in an architecture doc.

## Recommended Execution Order

1. **Measure:** B.
2. **Start policy and parity:** D and P in parallel, because they are
   independent and small.
3. **Change asset loading:** C, then hand its warning delta to hardening A1.
4. **Change networking:** N, after checking the ECS avatar-networking status.
5. **Validate shader layouts:** L1 and L2. These can run in parallel with C and
   N.
6. **Build the generator:** G1, then G2 parity, then G3 and G4 in A1 order.
7. **Generate shader declarations:** L3, after G.
8. **Rework transforms:** T, after the stall investigation closes, in the order
   T1, T2, T3, then T4.

Remove one root cause at a time with behavior-backed evidence. Lower warning or
allocation counts do not close a gate unless the corresponding change is
measured and explained.
