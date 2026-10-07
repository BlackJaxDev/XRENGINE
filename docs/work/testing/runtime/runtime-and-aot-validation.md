# Runtime And AOT Validation

Scope: Validate runtime regression repairs, NativeAOT publication, runtime data layout, hot-path memory control, and editor memory reduction. This doc owns manual, runtime, visual, hardware, profiler, benchmark, and soak checks for these areas.

Architecture: [Runtime Project Organization](../../../architecture/runtime/project-organization.md), [AOT Final Game Builds](../../../developer-guides/runtime/aot-final-game-builds.md), [Cooked Asset Serialization](../../../architecture/assets/cooked-asset-aot-and-io.md), [Runtime Data Layout Measurements](../../../developer-guides/diagnostics/runtime-data-layout-measurements.md), [Hot-Path Memory Control](../../../developer-guides/runtime/hot-path-memory.md), [CPU Memory Ownership](../../../architecture/rendering/cpu-memory-ownership.md), [Downloaded-Content Execution Policy](../../../architecture/runtime/downloadable-content-execution-policy.md)

Code todos: [Runtime Regression And NativeAOT Hardening](../../todo/runtime/runtime-regression-and-nativeaot-hardening-todo.md), [Runtime Data Layout And Generated Contracts](../../todo/runtime/runtime-data-layout-and-generated-contracts-todo.md), [Editor Memory Reduction](../../todo/rendering/optimization/editor-memory-reduction-todo.md)

Template: [Memory-Control Investigation Template](memory-control-investigation-template.md)

## Setup

- Build tasks: `Build-Editor`, `Build-Editor-Release`, `Build-Server`, `Build-VRClient`, `Build-RenderBench`, and `Build-Profiler`.
- Runtime tasks: `Start-Editor-NoDebug`, `Start-Editor-WithProfiler-NoDebug`, `Start-Profiler-NoDebug`, `Start-LocalPoseSync-NoDebug`, `Start-2Clients-NoDebug`, `Start-Server-NoDebug`, `Start-Client-NoDebug`, `Start-Client2-NoDebug`, `Start-Editor-UnitTesting-OpenXR-SteamVR-NoDebug`, and `Start-Editor-UnitTesting-OpenXR-Monado-NoDebug`.
- Report and publish tasks: `Report-NewAllocations`, `Publish-VRMonkeyBall-NativeAOT-Package`, and `Generate-UnitTestingWorldSettings`.
- Launch profiles: `Editor (Default World)`, `Editor (Unit Testing World)`, `Editor (Unit Testing OpenXR SteamVR)`, `Editor (Unit Testing World, Validation Layers)`, `Debug Client (Server & other client run separately)`, `Debug Server (Server only)`, `Debug VRClient (Editor runs separately)`, `Debug Profiler`, and `Debug Profiler (with Editor)`.
- NativeAOT commands: `powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Publish-MonkeyBallVR.ps1` and `powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Run-AotParitySmoke.ps1 -Mode error`.
- Runtime data layout command: `dotnet run --project XREngine.RenderBench --no-build -- --scenario runtime-data-layout --runtime-lane all --runtime-manifest <fixture-manifest.json> --scenario-repeats 2 --warmup-frames 60 --scenario-frames 120 --capture-frames 1000 --output-dir <run-root>\reports\runtime`.
- Local realtime command: `pwsh Tools\Measure-LocalRealtime.ps1 -OutputDirectory <run-root>\reports\local-realtime -BasePort 25000 -WarmupSeconds 10 -MeasureSeconds 30`.
- Editor memory setup: Use an isolated MCP editor session. Enable user settings, OpenXR, `XRE_UNIT_TEST_WORLD_SETTINGS_PATH`, and `XRE_UNIT_TEST_VR_MODE=OpenXR`. Measure after private bytes stay stable for 20 seconds.
- Key environment variables: `XRE_AOT_PARITY`, `XRE_MEMORY_PROFILE`, `XRE_MEMORY_DIAGNOSTICS`, `XRE_GC_LATENCY_MODE`, `XRE_DISABLE_MAINTENANCE_GC`, `XRE_BENCHMARK_NOGC_REGION`, `XRE_BENCHMARK_NOGC_BYTES`, `DOTNET_gcConcurrent`, `DOTNET_GCConserveMemory`, `XRE_WORLD_MODE`, `XRE_UNIT_TEST_WORLD_KIND`, `XRE_UNIT_TEST_VR_MODE`, `XRE_UNIT_TEST_PREVIEW_VR_STEREO_VIEWS`, `XRE_NET_MODE`, `XRE_UDP_CLIENT_RECEIVE_PORT`, `XRE_NETWORKING_POSE_ROLE`, `XRE_POSE_ENTITY_ID`, `XRE_POSE_BROADCAST_ENABLED`, and `XRE_POSE_RECEIVE_ENABLED`.

## Checks

### Runtime regression baseline

Architecture: [Runtime Project Organization](../../../architecture/runtime/project-organization.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Build non-Vulkan owners and consumers. | Run relevant project builds with zero warning tolerance. | Each owned project builds with zero errors and no new warnings. | Open | none |
| Re-run focused regression lanes. | Run profile, naming, publish, collectible, animation, cooked/snapshot, OpenGL/shared-rendering, physics-boundary, editor, and tooling lanes. | Each retained lane has a current result. | Open | 2026-10-03 |
| Inventory failures. | Generate a machine-readable list with identity, subsystem, signature, owner, isolated result, suite-order result, and disposition. | Each root cause has one owner and one reproduction path. | Open | none |
| Classify failures. | Classify each retained result as product regression, fixture defect, shared-state leak, stale contract, missing generated asset, excluded Vulkan issue, excluded hardware issue, or duplicate symptom. | No failure is counted twice. | Open | none |
| Prove deterministic runs. | Run retained tests individually, in suite order, and with at least three deterministic randomized seeds. | Results, registry indices, generations, settings, and failure counts stay stable. | Open | none |
| Verify no actionable skips. | Inspect source, asset, and shader contract fixture output. | Missing code or asset inputs fail with diagnostics instead of skipped or inconclusive results. Unit tests do not read Markdown. | Open | none |
| Check documentation references. | Review changed links and referenced paths in runtime work docs outside the unit-test suite. Record broken links and repair them in the docs. | Links resolve to current files and headings; no Markdown-reading unit test is added. | Open | none |
| Record fixture decisions. | Record reusable lane and fixture choices in the [Unit Test Project Reorganization TODO](../../todo/tests/unit-test-project-reorganization-todo.md). | The shared choices are documented without unrelated directory moves. | Open | none |

### Animation and humanoid runtime

Architecture: [Runtime Project Organization](../../../architecture/runtime/project-organization.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Validate representative imported clips. | Exercise imported clips across full time ranges. | Curves, root motion, retargeted muscles, and preview poses have independent evidence. | Open | 2026-10-03 |
| Audit animation allocations. | Capture allocation scopes after correctness repairs. | Animation evaluation and imported-event dispatch do not add per-frame allocations. | Open | none |
| Run animation and humanoid tests. | Run retained non-hardware tests individually and in suite order. | The tests pass in both modes. | Open | 2026-10-03 |

### Shared rendering and OpenGL runtime

Architecture: [CPU Memory Ownership](../../../architecture/rendering/cpu-memory-ownership.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Validate OpenGL fixes live. | Run an isolated OpenGL editor session after each behavior fix cluster. Inspect screenshots and logs. | Pixel and GPU-resource changes have evidence from more than one camera view. | Open | 2026-10-03 |
| Re-run rendering allocation checks. | Capture allocations for material publication, pipeline preparation, texture streaming, and shadow scheduling. | No new steady-state hot-path allocation appears. | Open | none |
| Run non-Vulkan and OpenGL lanes. | Run shared-rendering and OpenGL lanes. | The lanes pass without obsolete private-source contracts. | Open | 2026-10-03 |

### Cooked assets and snapshots

Architecture: [Cooked Asset Serialization](../../../architecture/assets/cooked-asset-aot-and-io.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Validate cooked and snapshot round trips. | Run cooked asset, scene snapshot, animation snapshot, and generated identity lanes. | Round trips pass without facade identities or reflective compatibility fallback. | Open | 2026-10-03 |
| Validate small residual failures. | Run focused validation for editor exit ordering, MCP permissions, generated shader references, and document references. | Each residual has focused behavior evidence. | Open | none |

### NativeAOT graph and publication

Architecture: [AOT Final Game Builds](../../../developer-guides/runtime/aot-final-game-builds.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Refresh strict warning inventory. | Run `Tools\Publish-MonkeyBallVR.ps1 -AllowAotWarnings` for diagnosis. | Counts by warning cluster are current. | Open | 2026-10-02 |
| Publish with strict warnings. | Run `Publish-VRMonkeyBall-NativeAOT-Package` or `Tools\Publish-MonkeyBallVR.ps1` without `-AllowAotWarnings`. | Publish succeeds with zero IL2xxx or IL3xxx diagnostics. | Open | 2026-10-03 |
| Inspect final package. | Inspect manifest, hashes, cargo, native files, and facade content. | The player graph contains only justified runtime content. | Open | none |
| Run packaged runtime smoke. | Run the produced launcher with `--aot-smoke`, and run an interactive smoke when needed. | Cooked load, scene activation, begin play, input setup, 300 update ticks, physics progression, shutdown, and missing-metadata checks pass. | Open | 2026-10-03 |
| Validate representative AOT content. | Include animation clips, humanoid bindings, cooked collection payloads, custom-object payloads, snapshots, texture payloads, and session metadata. | The smoke exercises generated metadata and codecs. | Open | none |
| Audit allocations after generated-code migration. | Capture update, animation, physics, render, streaming, and network allocation scopes. | The migration adds no hot-path allocation. | Open | none |

### AOT parity diagnostics and generated contracts

Architecture: [AOT Final Game Builds](../../../developer-guides/runtime/aot-final-game-builds.md), [Generated Runtime Factories](../../../developer-guides/runtime/generated-runtime-factories.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Run AOT parity smoke. | Run an isolated editor with `XRE_AOT_PARITY=error`, and enter MonkeyBall play mode. | MonkeyBall reports zero parity diagnostics. | Open | 2026-10-02 |
| Run parity in the unit-test lane. | Run the unit-test lane with `XRE_AOT_PARITY=error`. | The lane reports zero parity diagnostics. | Open | none |
| Run generator parity tests. | Compare registered type sets from former PowerShell output and generated Roslyn output. | Type sets are equal for the current tree. | Open | 2026-10-02 |
| Run generator diagnostic and codec tests. | Run generator diagnostic tests and generated codec round-trip tests. | Missing contracts are compile errors with actionable messages. | Open | none |
| Measure generator build cost. | Measure clean and incremental editor build times. | Cost stays within the recorded budget. | Open | none |
| Refresh warning deltas. | Re-run strict warning inventory after generated contracts. | Warning-cluster reductions match the ledger. | Open | none |

### Downloaded-content execution policy

Architecture: [Downloaded-Content Execution Policy](../../../architecture/runtime/downloadable-content-execution-policy.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Test package rejection. | Run tests with a PE file renamed to `.bin`, an ELF file, a normal cooked package, and a manifest edited after verification. | Executable payloads and edited manifests are rejected. Normal cooked packages are accepted. | Open | none |
| Verify published content loading. | Inspect published runtime package paths and loader behavior. | No published-runtime path loads managed code from downloaded content. | Open | none |

### Cooked archive and payload leases

Architecture: [Cooked Asset Serialization](../../../architecture/assets/cooked-asset-aot-and-io.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Test archive handles and leases. | Run tests for archive lookup modes, lease lifetime, use-after-dispose diagnostics, envelope V2 round trips, older-version rejection, and concurrent lookups. | Handles and leases meet the documented ownership contract. | Open | none |
| Repeat asset load measurements. | Repeat asset-load and 20-cycle avatar measurements. | Archive opens, allocations, LOH size, gen2 count, and wall time are recorded. | Open | none |
| Run AOT smoke after asset-load changes. | Run MonkeyBall AOT smoke with warnings allowed only for diagnosis. | The warning delta is recorded. | Open | none |
| Load cooked content in editor. | Use the isolated editor loop to load cooked content and inspect the viewport. | Cooked content still renders. | Open | none |
| Verify cooked archive gates. | Inspect archive-open count, transient LOH allocation, published reader annotations, older-content diagnostics, and doc updates. | Each gate matches the cooked archive contract. | Open | none |

### Runtime data layout measurements

Architecture: [Runtime Data Layout Measurements](../../../developer-guides/diagnostics/runtime-data-layout-measurements.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Measure asset loads. | Run the RenderBench asset lane with MonkeyBall content and one representative imported avatar. | Allocation totals, LOH size, GC counts, wall time, archive opens, and churn are recorded by asset identity. | Open | none |
| Measure networking. | Run the local two-client session and synthetic harness with 1, 8, and 32 avatars. | Packet size, send and receive allocations, and server relay cost are recorded. | Open | none |
| Measure transforms. | Run the avatar-import scenario and RenderBench production scene with 1, 8, and 32 animated avatars. | Transform counts, dirty counts, propagation timing, publication time, and allocation bytes are recorded. | Open | none |
| Measure noise band. | Run two consecutive runs for each measurement. | Differences stay within the recorded noise band or the difference is explained. | Open | none |
| Inventory shader-bound layouts. | Record C# struct, shader struct and block, include file, binding, layout rule, and expected constant location for every layout. | The inventory covers generated and legacy engine-owned shader records. | Open | none |

### Binary realtime networking

Architecture: [Runtime Data Layout Measurements](../../../developer-guides/diagnostics/runtime-data-layout-measurements.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Run allocation tests. | Run allocation tests for pose, replication delta, and clock sync send and receive paths after warmup. | Each path allocates zero managed bytes per tick. | Open | none |
| Run malformed-payload tests. | Run tests for truncated, oversized, unknown-type, and mismatched-protocol inputs. | Bad inputs fail with diagnostics. | Open | none |
| Repeat networking measurements. | Repeat runtime data layout networking measurements. | Packet size and allocation results are recorded. | Open | none |
| Run live local networking. | Run a local two-client session plus server relay. | Remote avatar poses apply correctly. | Open | none |

### GPU record layouts

Architecture: [CPU Memory Ownership](../../../architecture/rendering/cpu-memory-ownership.md), [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Run GPU layout validator. | Run validator unit tests for every record, cook validation, and optional debug-start validation. | Mismatches name the struct, member, and both offsets. | Open | 2026-10-02 |
| Run negative layout tests. | Reorder one GLSL member in a scratch copy and run unit-test and cook validation. | Both fail with the expected diagnostic. | Open | none |
| Run GPU-scene and advanced contract lanes. | Run the GPU-scene and advanced contract tests. | Generated declarations and reflected layouts pass. | Open | none |
| Run live visual layout comparison. | Run OpenGL and Vulkan editor loops, capture at least two camera positions, and compare with pre-change captures. | Generated declarations cause no visual change. | Open | none |
| Confirm hot reload. | Regenerate includes and test renderer hot reload. | Hot reload picks up regenerated includes. | Open | none |

### Dense transform storage

Architecture: [Runtime Data Layout Measurements](../../../developer-guides/diagnostics/runtime-data-layout-measurements.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Complete transform prerequisites. | Close the avatar scene publication stalls investigation, complete the baseline, and record numeric targets. | Target and baseline exist before closeout. | Open | none |
| Compare against benchmark harness. | Compare dense transform storage against the benchmark harness. | Timing, publication, and allocation results are recorded. | Open | none |
| Run transform tests. | Run `TransformLocalPoseBatchTests`, `TransformAccessorFastPathTests`, and `ModelImporterTransformTests`. | Existing transform behavior remains correct. | Open | none |
| Run new store tests. | Run store tests for reparent ordering, generation reuse, world-override ordering, and subscriber-only events. | Store-specific rules pass. | Open | none |
| Run live editor transform loop. | Use isolated editor loop for avatar import, physics scene, gizmo editing, and OpenGL and Vulkan captures. | Visual, physics, and editor-tool behavior remain at parity. | Open | none |
| Run VR transform validation. | Run OpenVR hardware validation when available. | VR is at parity or explicitly unqualified. | Open | none |
| Measure transform allocations. | Capture allocation scopes after warmup. | Propagation and publication allocate zero bytes. | Open | none |

### Runtime memory and GC control

Architecture: [Hot-Path Memory Control](../../../developer-guides/runtime/hot-path-memory.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Capture live editor allocation baseline. | Capture default editor startup, Unit Testing World steady state, camera movement, and one model import or asset load. | Allocation totals per major thread are recorded. | Open | 2026-07-02 |
| Rank allocation scopes. | Rank render and VR allocation scopes by bytes per frame and frequency. | Each source has a hot-path classification. | Open | 2026-07-02 |
| Capture VR allocation baseline. | Capture OpenVR, OpenXR with SteamVR, and no-HMD OpenXR Monado smoke. | VR update and render scopes stay within budgets or are unqualified. | Open | none |
| Repeat VR capture before GC policy changes. | Repeat hardware VR profiler capture before changing GC policy defaults. | The before/after comparison uses the same setup. | Open | none |
| Compare GC conserve memory. | Launch with and without `DOTNET_GCConserveMemory=5`. | Default selection is supported by frame and pause evidence. | Open | 2026-10-05 |

### Editor mesh and texture CPU memory

Architecture: [CPU Memory Ownership](../../../architecture/rendering/cpu-memory-ownership.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Re-measure editor memory. | Run the OpenXR editor memory setup, force a full GC, and record private bytes, GC committed, live managed memory, DataSource memory, and device-local memory. | Process memory excluding device-local memory stays near the target. | Open | 2026-10-05 |
| Validate mapped cooked mesh data. | Load published cooked meshes with stored large buffers. | Stored entries can stay mapped instead of copied into new native memory. | Open | none |
| Validate upload without processing. | Upload cooked buffers that already match GPU layout, including GDeflate streams on supported Vulkan devices. | Upload uses one copy, and unsupported compressed payloads fail visibly. | Open | none |
| Verify no black frames. | Capture 15 seconds of SteamVR VR View. | No black frames appear. | Open | 2026-10-04 |
| Verify idle allocation rate. | Capture allocated bytes per second at idle. | Allocation rate does not increase beyond noise. | Open | 2026-10-05 |
| Verify frame time. | Measure editor frame time before and after memory changes. | Frame time remains within noise. | Open | 2026-10-05 |

### Editor texture and render-target memory

Architecture: [CPU Memory Ownership](../../../architecture/rendering/cpu-memory-ownership.md), [Texture Streaming](../../../architecture/rendering/texture-streaming.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Validate BCn texture payloads. | Import and stream BC7 colour, BC5 normal, and BC4 or BC7 mask variants after encoder approval. | Streamed texture memory drops, and unsupported hardware reports a diagnostic. | Open | none |
| Verify sparse residency limit. | Inspect Vulkan texture streaming capability. | Dense residency remains explicit until sparse residency work begins. | Open | 2026-10-05 |
| Validate render-target declarations. | Query Vulkan resource planner state for desktop and OpenXR. | Only active passes materialize targets. | Open | 2026-10-05 |
| Validate transient aliasing. | Inspect lifetime intervals, barriers, discard transitions, name lookups, and screenshots after aliasing work. | Only disjoint target lifetimes alias. | Open | none |
| Validate planner commit behavior. | Exercise OpenXR startup and planner scope commits. | Planner commits do not retire a live eye-planner allocator. | Open | none |

## Hardware Matrix

| Area | Hardware or runtime | Required result | Status | Last evidence |
|---|---|---|---|---|
| VR allocation baseline | OpenVR hardware | Steady-state scopes stay within budget. | Open | none |
| VR allocation baseline | OpenXR with SteamVR | Steady-state scopes stay within budget. | Open | none |
| VR smoke | OpenXR with Monado, no HMD | Smoke completes or reports an explicit unqualified state. | Open | none |
| Transform validation | OpenVR hardware | Dense transform storage keeps VR behavior at parity or marks the row unqualified. | Open | none |
| Texture compression | Vulkan device with BC compression | BCn payloads upload and sample correctly. | Open | none |
| GDeflate upload | Vulkan device with `VK_NV_memory_decompression` and buffer device addresses | Compressed mesh buffers use GPU decompression. | Open | none |

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
| Runtime regression baseline | Latest handoff still has unaccepted animation codec, nested MCP object path, object-cache tests, humanoid axis mapping, and package manifest edits. | [Runtime Regression And NativeAOT Hardening](../../todo/runtime/runtime-regression-and-nativeaot-hardening-todo.md) |
| Strict NativeAOT publish | Latest strict publish still had IL2xxx and IL3xxx warnings. | [Runtime Regression And NativeAOT Hardening](../../todo/runtime/runtime-regression-and-nativeaot-hardening-todo.md) |
| Runtime data layout closeout | Measurements and live validation remain pending after code checkpoints. | [Runtime Data Layout And Generated Contracts](../../todo/runtime/runtime-data-layout-and-generated-contracts-todo.md) |
| Editor memory budget | 2026-10-05 state improved memory but still exceeded the 1 to 2 GB ideal outside GPU memory. | [Editor Memory Reduction](../../todo/rendering/optimization/editor-memory-reduction-todo.md) |
