# Runtime Data Layout And Generated Contracts Progress

Created: 2026-10-02
Checkpoint: 2026-10-02, paused at the owner's request.

Tracker: [Runtime Data Layout And Generated Contracts TODO](../../todo/runtime/runtime-data-layout-and-generated-contracts-todo.md)

Design: [Runtime Data Layout And Generated Contracts](../../design/runtime/runtime-data-layout-and-generated-contracts-design.md)

The implementation is not complete. The tracker now separates completed code,
remaining implementation, and unperformed validation. The owner's code-first
instruction remains in effect; no performance, visual, AOT publication, or VR
acceptance is claimed.

## Repository And Recovery

- Base commit: `5f9275469` (`Headless MCP improvements, more Vulkan debugging`).
- Branch: `master`; the working tree was already dirty. Existing staged changes
  and concurrent directional-shadow/Vulkan work were preserved.
- Recovered Claude Code session: `f83dd8dc-4337-4602-a833-27741e047a09`.
  Last successful edit preceded the usage-limit message at 2026-10-02 21:57:54 UTC.
- Evidence root: `Build/_AgentValidation/20261002-153000-runtime-contracts-resume/`.
  The retention command succeeded during recovery.
- No commit or push was made by this work.

Concurrent work included `StandardShadow.glslinc`, Vulkan frame-loop readiness
and preparation files, advanced global resource/shadow behavior, and associated
rendering investigations. Shared record annotations preserve that behavior.
Submodule dirtiness was not changed.

## Implementation State

| Area | Implemented at checkpoint | Remaining code |
|---|---|---|
| Baseline tooling | RenderBench asset/network/transform lanes, fixture identities and hashes, repeated 1/8/32-avatar runs, twenty-cycle asset churn, local server/two-client driver, opt-in real networking scopes | Full shader inventory remains open; no measured baseline yet |
| Downloaded content | Data-only policy, creation/verification/staging/catalog guards, bootstrap and managed-loader restrictions; unreadable-file checks fail closed | Runtime/security acceptance remains to be exercised |
| AOT parity | Explicit scoped diagnostics, repeated error reports keep rejecting fallback, classification inventory, repaired play-mode smoke | Eliminate remaining player reflection via generated contracts |
| Cooked loading | Versioned span envelope/registry, shared archive mappings, pooled/native payload leases, exactly-once transfer/disposal, mapped lease retention across archive replacement, pinned lifetime retention, sub-LOH pool buckets, cache eviction on asset destruction | Native GDeflate/hardware LZ4 direct-span overrides; remaining texture source/YAML lease extraction |
| Networking | Binary DTO/control framing, stack/ring high-rate pose/clock/delta paths, bounded deferred receive slabs, client/server/managed transport integration, authority validation, protocol version 2, opt-in measurements | Runtime allocation/admission/pose acceptance still open |
| Generated contracts | Default semantic factory generator, prior factory-set equality, versioned type/codec registrations, animation bindings, closed formatter declarations, representative typed accessors/event invocation | Safe generated component activation; complete formatter/accessor and metadata coverage, remove remaining player scans, payload-body generation, missing-path/open-generic diagnostics |
| GPU layouts | Generated declarations and actual Shaderc/SPIRVCross checks for 33 Advanced and 17 GPUScene records; cook/debug/CLI hooks; named-member diagnostics; legacy duplicate offsets removed in covered paths | Complete row-by-row all-shader inventory and confirm remaining families; compile real production consumers and qualify live behavior |
| Transforms | Active per-world generational dense store, parent-first propagation, bounded persistent workers, sequence-locked matrix storage, lazy inverses/replication state, subscriber-aware events, renderer rows and publication integration | Numeric targets and runtime qualification; no measured superiority claim |

The generated component factory branch in `XRComponent.New` was removed at this
checkpoint to preserve attachment of SceneNode before derived constructors run.
That migration is explicitly unfinished; the retained reflective path reports
parity diagnostics. Generated registration around an existing serializer body
is not counted as full generated serialization.

## Reproducible Entry Points

[Runtime Data Layout Measurements](../../../developer-guides/diagnostics/runtime-data-layout-measurements.md)
documents the manifest and exact commands. Main entry points:

- `XREngine.RenderBench --scenario runtime-data-layout --runtime-lane all|assets|networking|transforms`.
- `Tools/Measure-LocalRealtime.ps1` starts uniquely named owned sessions, checks
  admission/traffic, subtracts warm snapshots, archives results, and stops them.
- `Tools/Run-AotParitySmoke.ps1` loads/compiles the authoring project before world
  load, checks MCP errors and play-mode transitions, and fails on missing logs.
- Editor `--generate-gpu-record-includes .` regenerates and reflects layouts.

The transform harness uses actual imported hierarchies with deterministic local
rotations and the Advanced production scene. It separates propagation,
render-matrix publication, canonical world/viewport swap and package finalization,
and total frame costs. It does not claim authored animation-clip or headset parity.
`GC.GetGCMemoryInfo` records last-GC LOH sizes, not exact LOH allocation bytes or
counts; an allocation trace is required for the latter.

## Evidence And Limitations

- Recovered networking integration built Core, Editor and UnitTests successfully
  with zero warnings/errors. Test compilation did not execute tests.
- Semantic factory type-set comparison matched Host 67/67, Rendering 265/265,
  and Bootstrap 0/0 on the compared tree. BrowserManifest remains an explicit
  template path, without C# regex parsing. Automated regression coverage remains open.
- SourceGenerators, Data, Animation, Rendering and Host narrow checkpoint builds
  passed with zero warnings/errors. The RenderBench narrow build passed with zero
  warnings/errors; log: `logs/renderbench-narrow-build.log`.
- The final GPU slice Editor build passed, with one temporary component warning
  before restoration of the constructor path. Actual layout reflection passed for
  all 50 generated records. A scratch reordered `AdvancedDrawRecord` failed with
  `PrimitiveSection offset mismatch: shader=68, C#=64`. The installed SPIRVCross
  parser accepts the validation shader's SPIR-V 1.3; renderer targets were unchanged.
- Both new/updated PowerShell measurement/smoke scripts passed parser checks.
  They have not been executed as live acceptance runs.
- Final checkpoint Editor build passed with zero warnings/errors using
  `dotnet build XREngine.Editor/XREngine.Editor.csproj --no-restore -nologo -v:q -m:1`;
  log: `logs/checkpoint-editor-build.log`. MCP tool documentation was regenerated.
- Final checkpoint RenderBench dependency-closure build passed with zero
  warnings/errors using `dotnet build XREngine.RenderBench/XREngine.RenderBench.csproj --no-restore -nologo -v:q -m:1`;
  log: `logs/checkpoint-renderbench-build.log`.
- The combined staged/unstaged diff passed whitespace checks after cleanup.
- The approved Roslyn dependencies were reviewed with the dependency report;
  MIT notices are present for CodeAnalysis.CSharp 4.14.0 and Analyzers 3.11.0.
- No new tests were added. Prior minimal test edits only repaired compilation
  against changed networking APIs. Further tests follow live validation.

The [reflective-site classification](runtime-reflective-site-classification.md)
records player and authoring boundaries for the hardening work. The
[generator guide](../../../developer-guides/runtime/generated-runtime-factories.md)
and GPU/transform architecture guides describe the current contracts.

## Resume Order

1. Finish the explicit C/G/P code gaps above, preserving component constructor
   ordering and the download restrictions.
2. Complete the shader-bound inventory and qualify production shader includes.
3. Run the documented baseline and allocation measurements; establish targets
   and noise bands before assessing performance.
4. Run parity play-mode smoke, asset churn, two-client networking, transform and
   renderer live loops; then the planned deterministic tests and AOT warning/build
   cost comparisons. Record physical VR acceptance separately.
5. Close completion gates only with evidence. Keep the TODO active until then.
