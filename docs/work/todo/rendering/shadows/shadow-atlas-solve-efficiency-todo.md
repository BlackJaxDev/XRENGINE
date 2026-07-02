# Shadow Atlas Solve Efficiency TODO

Status: code implementation complete, live benchmark/merge follow-up pending,
created 2026-06-12, updated 2026-06-12.
Branch: `rendering/shadow-atlas-solve-efficiency` (create before Phase 1, merge
to `main` after Phase 5 validation).

This TODO tracks targeted work to reduce `ShadowAtlasManager.SolveAllocations`
cost without weakening the v1 shadow-atlas contract. The immediate symptom is a
single solve taking tens of milliseconds in the Unit Testing World while render
thread logs also show directional cascade fallback and shadow tile budget
overruns.

Primary files:

- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasManager.cs`
- `XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Shadows.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasFrameData.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasTypes.cs`
- `XREngine.UnitTests/Rendering/ShadowAtlasManagerPhaseTests.cs`
- `XREngine.UnitTests/Rendering/PointShadowAtlasStabilityTests.cs`

Related docs:

- `docs/work/todo/rendering/shadows/shadow-atlas-overhaul-todo.md`
- `docs/work/design/rendering/shadows/dynamic-shadow-atlas-lod-plan.md`
- `docs/architecture/rendering/default-render-pipeline-notes.md`

## Current Problem

- `SolveAllocations` can restart the whole balanced solve after each demotion.
  In over-budget or fragmented states, one failed request can cause repeated
  full-state clears, reserve attempts, allocation scans, and sticky demotion
  passes.
- Demotion selection scans all entries, then sticky demotion target application
  scans them again. Directional cascade demotion can add more full-list scans.
- Directional cascade and point-face group publishing are discovered after
  allocation through repeated whole-request-list scans.
- Buddy page allocation scans free blocks within levels to preserve deterministic
  placement. That is acceptable per attempt, but retry churn multiplies it.
- Diagnostics currently expose the elapsed solve scope, but not the counts that
  explain why the solve was expensive.

## Goals

- Keep allocation deterministic across equivalent request sets.
- Preserve prior-slot reuse, sticky demotion behavior, resident TTL reuse, and
  `NotRelevant` stale-tile reservation.
- Make solve cost predictable under capacity pressure.
- Make logs/profiler data explain whether time was spent in request
  classification, retry/demotion, page allocation, group publishing, or
  descriptor publish.
- Avoid heap allocations in steady-state per-frame solve hot paths.

## Phase 0: Branch And Baseline

- [x] Create the dedicated branch `rendering/shadow-atlas-solve-efficiency` for
  this TODO before making code changes.
- [ ] Capture a current baseline for `ShadowAtlasManager.SolveAllocations` in the
  Unit Testing World (steady-state and worst observed solve time, plus the scene
  configuration used) so later phases can prove the improvement rather than
  asserting it. Record the numbers in this doc.
  - Not captured before implementation because this follow-up was requested as
    an immediate implementation pass. Targeted unit and build validation is
    recorded below; live Unit Testing World timing remains the acceptance
    follow-up.

## Phase 1: Instrument The Solver

- [x] Add per-frame solve diagnostics with request counts by atlas kind and
  encoding.
- [x] Count balanced-solve attempts, failed candidates, demotions, sticky
  demotions, prior-slot reserve hits/misses, page allocations, and page clears.
- [x] Count group publishing work: directional group seeds, directional group
  members, point group seeds, point group members, and missing/co-location
  failures.
- [x] Add a slow-solve warning that reports the counters when solve time exceeds
  a configurable threshold.
- [x] Surface the same counters in the profiler/rendering stats packet so the
  ImGui profiler can correlate `SolveAllocations` time with allocator behavior.

## Phase 2: Reduce Retry Churn

- [x] Replace one-demotion-per-restart with batched demotion when the same solve
  attempt fails multiple candidates at the same effective level.
- [x] Preserve reusable page/free-block state between retries when only request
  levels changed and page topology did not need a full reset.
  - Absorbed by the allocation/threading pass: page occupancy is persistent
    across frames, and local repair frees/replaces only the failed entry or
    group instead of clearing every page.
- [x] Track the lowest failing size per kind/encoding and skip immediately
  impossible candidates in the next attempt.
  - Superseded by the feasibility waterline pre-pass plus local repair. The
    solver demotes over-capacity buckets before placement instead of learning
    impossibility through repeated candidate failures.
- [x] Add a hard attempt ceiling with visible diagnostics and deterministic
  fallback demotion, not silent failure.
- [x] Add tests for near-capacity scenes that previously required many restart
  attempts.

## Phase 3: Make Group Publishing Linear

- [x] Build directional cascade groups from an indexed key rather than scanning
  all requests for each seed.
- [x] Build point face groups from an indexed key rather than scanning all
  requests for each seed.
- [x] Reuse scratch lists and member arrays or publish into pooled buffers to
  avoid per-frame group allocation churn. The current per-frame allocations are
  the `new ShadowAtlasGroupedAllocationMember[...]` arrays in
  `BuildDirectionalCascadeGroups` and `BuildPointFaceGroups`; target those sites
  specifically.
- [x] Add tests that many point lights scale linearly with face request count.

## Phase 4: Improve Allocation Data Structures

- [x] Track best free block per level so deterministic block choice does not
  scan every free block on every retry.
  - Implemented as sorted free-block lists per level with deterministic first
    block selection.
- [ ] Add fragmentation metrics per page and trigger targeted compaction or
  repack only when it can reduce solve attempts.
- [x] Keep prior-allocation lookup O(1) for all request domains and validate no
  linear fallback remains in common solve paths.

## Phase 5: Validation

- [ ] Add a deterministic benchmark scene with one 4-cascade directional light,
  several spot lights, and several point lights.
- [ ] Add an over-budget benchmark scene that forces demotion without unbounded
  retry churn.
- [ ] Acceptance: steady-state solve in the in-budget benchmark scene is below
  1 ms after warmup, measured against the Phase 0 baseline on the same machine.
- [ ] Acceptance: over-budget solve in the over-budget benchmark scene remains
  below 2 ms and emits one clear diagnostic when deterministic fallback demotion
  is used.
- [ ] Acceptance: static camera/light scenes produce no allocation churn after
  warmup unless relevant settings or scene content change.
- [x] Regression guard: `ShadowAtlasManagerPhaseTests` and
  `PointShadowAtlasStabilityTests` still pass, confirming deterministic
  placement, prior-slot reuse, and sticky demotion are unchanged by the
  optimizations.

## Phase 6: Close Out

- [ ] Merge `rendering/shadow-atlas-solve-efficiency` back into `main` after the
  Phase 5 acceptance criteria pass.
- [x] Update `docs/architecture/rendering/default-render-pipeline-notes.md` if
  solve diagnostics or invariants changed for downstream readers.

## Implementation Notes

- `ShadowAtlasSolveDiagnostics` is published on `ShadowAtlasFrameData`, forwarded
  through runtime render host stats, serialized in `RenderStatsPacket`, and
  displayed in the profiler render stats panel.
- Slow solve warnings use `XRE_SHADOW_ATLAS_SOLVE_WARN_MS` when set, otherwise
  the larger of 2 ms and the configured shadow render budget.
- Balanced allocation retries are bounded by entry count, use batched demotion,
  demote page-sized candidates together, and apply a deterministic fallback
  demotion when the ceiling is reached.
- Directional cascade and point face group publishing now use indexed group keys
  and pooled member arrays.
- The buddy allocator uses per-level slot bitsets and deterministic lowest-bit
  selection, so alloc/free no longer pays sorted-list insert churn.
- Shadow atlas phase tests now install a minimal test render host so directional
  lights can construct their pipeline-dependent shadow resources without the
  full editor host.

## Validation Performed

- `dotnet build .\XREngine.UnitTests\XREngine.UnitTests.csproj /p:UseSharedCompilation=false`
  passed with 0 warnings and 0 errors.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-build --filter "FullyQualifiedName~ShadowAtlasManagerPhaseTests|FullyQualifiedName~PointShadowAtlasStabilityTests"`
  passed: 28 tests.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-build --filter "FullyQualifiedName~ProfilerProtocolTests|FullyQualifiedName~RuntimeRenderingHostServicesTests"`
  passed: 23 tests.

## Notes

- Rendering cost and solve cost must stay separate in diagnostics. Slow
  directional sequential atlas rendering belongs to the render-tile path; slow
  request classification, demotion, and group publishing belong to solve.
- Do not mask unsupported grouped cascade rendering with a CPU or sequential
  fallback unless the log states why the fallback was selected.
