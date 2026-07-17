# Desktop Visibility Generation Handoff Investigation

Date: 2026-07-16  
Status: P0.1 implemented and validated  
Related TODO: [Vulkan core hardening and device-loss TODO](../../todo/rendering/vulkan-core-hardening-and-device-loss-todo.md)

## Problem

Desktop camera movement could render a previous frame's visible command set with
the current camera. `EngineTimer` defaulted to `ReusePreviousVisibility`, and its
render/collect handoff used level-triggered events without identifying which
collect result a render consumed. A late collection could therefore be skipped
more than once, causing frustum-edge pop independent of occlusion culling.

An exception in collect/swap could also terminate the collect thread without
signaling the render waiter. An unhandled render exception was logged and retried
indefinitely, allowing repeated first-chance exceptions and an apparent freeze.

## Implemented Solution

- Added a single-producer/single-consumer collect generation gate. Generation
  zero represents the empty bootstrap buffer; collected generations must be
  requested, completed, published, and consumed in strict sequence.
- Changed the default and invalid-value fallback to `BlockUntilFresh`.
- Made a generation renderable only after all swap listeners finish. A failed
  collect or swap never publishes a partial generation.
- Kept collection overlap intact: generation `N+1` is built while render `N`
  runs, publication waits until render-side command consumption is released,
  and render `N+1` waits for publication before consuming it.
- Kept `ReusePreviousVisibility` as an explicit environment policy and bounded
  it to one stale render for each required generation.
- Made stop/terminal failure wake the visibility and render waiters. Collect,
  swap, and unhandled render exceptions now stop the timer instead of leaving a
  waiter blocked or retrying indefinitely.
- Exposed requested, completed, published, consumed, and required generations in
  profiler packets, NDJSON capture, the profiler UI, and the editor MCP stats.

## Automated Validation

Command:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~EngineTimerTests|FullyQualifiedName~CollectVisibleGenerationGateTests"
```

Result: 19 passed, 0 failed. The tests cover:

- default/invalid/explicit late-policy resolution;
- bootstrap and strict sequential generation consumption;
- a delayed publication that blocks until the matching generation arrives;
- one stale reuse maximum per required generation;
- terminal wakeup of a blocked render waiter;
- a real frustum transition in which an AABB becomes visible only when the
  moved-camera visibility generation is published.

A broader rendering-contract run passed 81 tests and failed five unrelated
`ImportedTextureStreamingContractTests`. Four failures reference the old
pre-decomposition command-recording source path; one expects outdated texture
streaming source text. None exercise the visibility handoff.

## Live Vulkan Desktop Validation

The editor was built and launched in the Unit Testing World with Vulkan,
`XRE_OCCLUSION_CULLING_MODE=Disabled`, and
`XRE_COLLECT_VISIBLE_LATE_POLICY=BlockUntilFresh`. Three camera-dependent MCP
screenshots were captured and visually inspected:

- `Build/_AgentValidation/20260716-p01-visibility-handoff/mcp-captures/Screenshot_20260716_120400.png`
- `Build/_AgentValidation/20260716-p01-visibility-handoff/mcp-captures/Screenshot_20260716_120424.png`
- `Build/_AgentValidation/20260716-p01-visibility-handoff/mcp-captures/Screenshot_20260716_120450.png`

Across the samples, live telemetry reported:

- effective occlusion mode `Disabled` and submission strategy `CpuDirect`;
- late policy `BlockUntilFresh`;
- published generation equal to consumed generation;
- required generation equal to the in-progress requested/completed next
  generation, demonstrating collection/render overlap;
- zero stale visibility reuse;
- zero Vulkan validation errors.

The machine-readable sample summary is under
`Build/_AgentValidation/20260716-p01-visibility-handoff/reports/live-validation.json`.

## Remaining Work

P0.1 corrects visibility ownership and synchronization. It does not address the
separate CPU command-recording, descriptor invalidation, pipeline warming, or
indirect-draw work owned by P0.2 through P0.6.
