# Runtime Data Layout And Generated Contracts TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Runtime Project Organization](../../../architecture/runtime/project-organization.md), [Downloaded-Content Execution Policy](../../../architecture/runtime/downloadable-content-execution-policy.md), [Cooked Asset Serialization](../../../architecture/assets/cooked-asset-aot-and-io.md), [Runtime Data Layout Measurements](../../../developer-guides/diagnostics/runtime-data-layout-measurements.md), [Generated Runtime Factories](../../../developer-guides/runtime/generated-runtime-factories.md), [CPU Memory Ownership](../../../architecture/rendering/cpu-memory-ownership.md)  Design: [Runtime Data Layout And Generated Contracts](../../design/runtime/runtime-data-layout-and-generated-contracts-design.md)
Validation: [Runtime And AOT Validation](../../testing/runtime/runtime-and-aot-validation.md)

## Current State
The code already has policy guards for downloaded content, scoped AOT parity diagnostics, archive handles, cooked payload leases, a published reader split, binary realtime state changes, a semantic source generator, GPU record reflection tooling, and dense transform storage. Open work remains in direct decompression destinations, texture payload leases, generated player contract coverage, shader-bound inventory closeout, transform target closeout, and unit-test code.

## Open Code Items

### Downloaded Content Policy
- [ ] Add policy validation tests. Files or types: package creation, verification, staging, catalog loading, and managed content endpoints. Done when tests reject PE files renamed to `.bin`, ELF files, and edited manifests, and accept a normal cooked package.
- [ ] Guard published-runtime managed-code loading paths for downloaded content. Files or types: published content loaders and `GameCSProjLoader` reachability. Done when any reachable managed-code load emits a diagnostic or is unreachable.

### AOT Parity Diagnostics
- [ ] Add remaining registrations or generated contracts for parity gaps. Files or types: `AotRuntimeMetadataStore`, cooked deserialization, component construction, animation binding, and YAML fallback sites. Done when no player-path fallback is reachable in error mode.
- [ ] Preserve SceneNode-before-derived-constructor semantics while enabling generated component factories. Files or types: `XRComponent.New` and generated component factory output. Done when constructors keep the required scene-node access order.

### Cooked Archive And Payload Leases
- [ ] Finish span destination support for every compression codec. Files or types: `Compression.Decompress(ReadOnlySpan<byte>, CompressionCodec, Span<byte>)`, GDeflate, LZ4, and fallback codecs. Done when compressed entries can decompress into caller-owned storage without a transient full array.
- [ ] Record the published reader split in the strict warning report. Files or types: NativeAOT warning report inputs. Done when the warning cluster delta includes the reader split.
- [ ] Convert texture-streaming payload creation and extraction to span or lease contracts. Files or types: `XREngine.Runtime.Rendering/Objects/Textures/2D/XRTexture2D.StreamingPayload.cs`. Done when source reads, YAML extraction, and payload creation avoid full transient managed arrays.
- [ ] Add archive and lease unit tests. Files or types: archive handle lookup modes, lease lifetime, use-after-dispose diagnostics, envelope V2 round trips, older-version rejection, and concurrent lookup tests. Done when each ownership rule has a deterministic unit test.

### Binary Realtime Payloads
- [ ] Add zero-allocation realtime networking tests. Files or types: `RuntimeMemoryControlTests`, pose send and receive, replication deltas, and clock synchronization. Done when warmed send and receive ticks allocate zero bytes.
- [ ] Add malformed realtime payload tests. Files or types: state-change packet readers and admission checks. Done when truncated, oversized, unknown-type, and mismatched-protocol inputs fail with diagnostics.
- [ ] Confirm ECS pose ownership remains outside this path or move the path there. Files or types: `HumanoidPoseSync` and generic ECS avatar networking. Done when pose ownership has one seam and no duplicate migration.

### Semantic Source Generator
- [ ] Regenerate dependency and license reports for the generator dependency change. Files or types: `Tools/Reports/Generate-Dependencies.ps1`, `docs/DEPENDENCIES.md`, and `docs/licenses/`. Done when the reports match the current generator package graph.
- [ ] Add an automated parity test for former PowerShell output and Roslyn output. Files or types: source generator tests and former registration modes. Done when registered type sets are equal on the current tree.
- [ ] Remove replaced PowerShell modes from the generator script when no mode remains. Files or types: `Tools/Generate-AotFactoryRegistrations.ps1`. Done when only still-owned modes remain.
- [ ] Add generator diagnostics for missing construction paths. Files or types: `XREngine.SourceGenerators`. Done when a player contract without a construction path is a compile error.
- [ ] Add generator diagnostics for open generics without closed registrations. Files or types: `XREngine.SourceGenerators`. Done when an open generic contract without a closed registration is a compile error.
- [ ] Generate the texture-streaming payload codec. Files or types: `XRTexture2D.StreamingPayload.cs` and source generator outputs. Done when the generated codec replaces the warning hotspot.
- [ ] Generate animation property bindings. Files or types: `AnimationMember.cs` and generated typed delegates. Done when published animation targets do not use reflective binding.
- [ ] Generate closed collection, nullable, and value-tuple formatters. Files or types: player-used formatters. Done when closed formatter declarations cover the player contract set.
- [ ] Generate typed property, field, method, and event accessors. Files or types: runtime member access declarations. Done when accessors cover every player-used member.
- [ ] Generate factories for components, transforms, assets, and controllers. Files or types: runtime factory declarations. Done when player construction uses generated factories.
- [ ] Generate AOT metadata tables and remove player-reachable scan fallback. Files or types: `AotRuntimeMetadataStore` and generator output. Done when player lookup no longer scans assemblies.
- [ ] Make generator failures compile errors. Files or types: source generator diagnostics. Done when silent omissions cannot produce a partial registration set.

### GPU Record Layouts
- [ ] Complete the shader-bound record inventory. Files or types: `GPUSceneLayoutContract` records, advanced records, generated includes, material rows, deformation records, physics-chain buffers, terrain records, and legacy shader snippets. Done when every engine-owned shader-bound record has an inventory row or a documented ABI exception.
- [ ] Add unit-test coverage for the GPU record validator. Files or types: `GpuRecordSpirvValidator` tests. Done when every inventoried record is covered by a unit test.
- [ ] Check in generated GLSL includes and add regeneration drift tests. Files or types: generated Advanced shader includes and generation tooling. Done when a stale include fails the test.
- [ ] Confirm renderer hot reload loads regenerated includes. Files or types: shader hot reload and generated include output. Done when a regenerated include is visible without a full editor restart.

### Dense Transform Storage
- [ ] Close transform prerequisites. Files or types: avatar scene publication stalls investigation, transform baseline, and transform target ledger. Done when targets exist before closeout.
- [ ] Compare dense transform storage against the benchmark harness. Files or types: `TransformHierarchyStore`, RenderBench transform lane, and ledger. Done when p50, p95, max, publication time, and allocation results are recorded.
- [ ] Add dense store unit tests. Files or types: reparent ordering, generation reuse, world-override ordering, and subscriber-only events. Done when each store invariant has a deterministic unit test.

## Decisions Needed

- [ ] Choose the published cook format for large mesh streams. Owner: Runtime and rendering owners.
- [ ] Decide whether ECS pose ownership has started. Owner: Runtime networking owner.
- [ ] Decide the retained `BrowserManifest` generation path. Owner: Runtime generator owner.
- [ ] Decide the accepted generator build-time budget. Owner: Runtime owner.

## Out Of Scope

- Visual, hardware, benchmark, smoke, and profiler execution. Those checks live in the validation doc.
- Broad asset pipeline redesign outside the cooked archive and payload lease work.
- Sparse texture residency and render-target aliasing work owned by rendering memory tasks.
