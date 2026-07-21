# Physics Chain Benchmark Contract Progress — 2026-07-20

This note records benchmark infrastructure completed while executing
`physics-chain-thousands-scale-optimization-todo.md`. It is implementation
evidence, not named-hardware performance acceptance. Matrix runs, budgets,
profiles, before/after tables, and break-even results remain open until they
are measured on the named target.

## Deterministic scenario data

`PhysicsChainBenchmarkDeterministicScenario` is a stateless input source keyed
only by the complete matrix case, configured seed, chain or segment index, and
fixed simulation frame. It defines:

- linear and branched parent topology plus non-uniform rest lengths;
- none, two-simple, five-mixed, and 64-shape broadphase collider layouts;
- shared versus unique collider-set assignment;
- fixed-step root motion and external forces;
- 100%, 50%, 10%, and sleeping/offscreen-heavy activity and visibility.

It does not use process-global random state, so matched runs and stable matrix
shards receive identical input. Focused Release validation:

- `PhysicsChainBenchmarkDeterministic.Tests.csproj`: 3/3.

## Timing and evidence acceptance

- Settle frames remain outside the timed window.
- Accepted evidence requires both 1,000 measured frames and 30 seconds after
  settle. Shorter interactive runs may diagnose behavior but cannot pass the
  acceptance validator.
- Raw whole-frame CPU samples are retained, and resolved whole-frame GPU
  timestamp samples now use the same p50/p95/p99/maximum summary contract.
- Static, state, collider, palette, bounds, and readback arenas have separate
  capacity, live, high-water, fragmentation, and growth records. Acceptance
  rejects an unavailable or internally invalid per-resource breakdown.
- Strict GPU acceptance still rejects missing GPU traces and nonzero readback;
  all accepted profiles require CPU hardware-counter evidence.
- Focused Release validation:
  - `PhysicsChainBenchmark.Tests.csproj`: 25/25.

## Named run controls

The named primary target and required process controls are recorded in
`docs/work/testing/physics-chain-named-hardware-matrix-2026-07-20.md`. A timing
run must use a new bounded validation root and a Release editor process:

```powershell
$env:XRE_PHYSICS_CHAIN_BENCHMARK_RUN_ROOT = "Build\_AgentValidation\$(Get-Date -Format yyyyMMdd-HHmmss)-physics-chain-scale"
$env:XRE_UNIT_TEST_WORLD_KIND = "MathIntersections"
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj -c Release -- --unit-testing
```

The current Math Intersections UI harness is useful for individual diagnostic
points. Full acceptance must use the focused runner/scenario bridge so the
complete lazy matrix, all three measurement kinds, matched-run indices,
preflight state, unsupported-backend records, raw evidence, and summaries are
captured without manual preset drift.

## Hardware-dependent blockers

## Headless CPU runtime scenario

`PhysicsChainCpuBenchmarkScenario` is the production bridge for CPU-strict and
CPU-quality-tiered matrix points that do not require live mesh rendering. It
registers the requested population in `PhysicsChainCpuBackend`, builds the
linear or branched immutable template from deterministic inputs, distinguishes
shared from per-instance collider ownership, applies activity/visibility and
quality cadence every fixed step, and records population plus per-resource
arena metrics. Warm steady frames reuse handle, input, batch, and readback
scratch arrays; they do not allocate managed memory.

The editor has a headless stable-work-item entry point. Work index `0` is the
first CPU-strict, no-rendering steady-state point; other indices retain the lazy
matrix's stable numbering. The environment file is a serialized
`PhysicsChainBenchmarkEnvironment` with exact named-hardware metadata.

```powershell
$env:XRE_PHYSICS_CHAIN_BENCHMARK_RUN_ROOT = "Build\_AgentValidation\$(Get-Date -Format yyyyMMdd-HHmmss)-physics-chain-scale"
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj -c Release -- `
  --physics-chain-cpu-benchmark-work-index=0 `
  --physics-chain-benchmark-environment=Build\_AgentValidation\named-environment.json
```

This bridge intentionally rejects GPU modes and identical/diverse rendered
mesh buckets. Those need a live graphics backend and renderer counters; the
headless path does not synthesize GPU timings, draws, hardware counters, or
renderer submissions. Evidence from this command therefore remains
implementation/runtime evidence until separately paired with required profiler
captures and accepted by `PhysicsChainBenchmarkAcceptanceValidator`.

Focused tests cover deterministic active/sleeping population, both linear and
branched templates, strict and quality-tiered execution, shared versus unique
collider accounting, deliberate rejection of unsupported graphics buckets,
and zero managed allocations across warmed frames.

No performance budget or break-even point is inferred from contract tests.
The following still require real captures:

- three matched Release runs for each accepted matrix point;
- CPU strict profile, strict zero-readback GPU trace, and rendered crowd trace;
- before/after p50/p95/p99/max, slopes, bytes, dispatches, barriers,
  allocations, arena memory, low-count latency, and disabled-system overhead;
- absolute strict and quality-tier budgets for the named hardware;
- cross-vendor GPU dependency-ordering and correctness evidence.
