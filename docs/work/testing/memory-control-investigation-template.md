# Memory-Control Investigation Template

[Back to testing docs](README.md)

Use this template when investigating GC, allocation, or frame-time regressions
in hot paths. Keep generated captures and logs under
`Build/_AgentValidation/<run>/`; copy only durable findings into the work doc.

## Investigation

- Date:
- Owner:
- Branch or commit:
- Run root:
- Renderer backend:
- Test scene or world:
- Runtime mode:
- Memory profile:
- VR runtime and hardware:
- Network/avatar scenario:

## Baseline

Record allocation tracking with profiler scopes enabled.

| Source | Bytes/frame or bytes/tick | Frequency | Classification | Notes |
|---|---:|---:|---|---|
| Render thread | | | | |
| Collect/swap thread | | | | |
| Update thread | | | | |
| Fixed-update thread | | | | |
| Profiler sender | | | | |
| Network receive/send | | | | |
| ECS systems | | | | |
| VR input/update | | | | |

Classifications: hot-path blocker, warmup-only churn, editor/tooling-only
churn, acceptable background allocation.

## Reproduction Steps

1. Build the target executable.
2. Launch with the intended memory profile and allocation tracking enabled.
3. Warm up the scene until one-time capacity growth has settled.
4. Capture steady-state profiler data or logs.
5. Repeat after the change with the same scene, backend, and runtime mode.

## Evidence

- Build logs:
- Test logs:
- Profiler capture:
- MCP screenshots or viewport captures:
- Engine log session:
- RenderDoc capture, if relevant:

## Findings

| Finding | Evidence | Proposed fix | Status |
|---|---|---|---|
| | | | |

## Validation

| Check | Command or capture | Result |
|---|---|---|
| Targeted build | | |
| Targeted tests | | |
| Editor launch path | | |
| Server launch path | | |
| VR client launch path | | |
| Profiler telemetry | | |

## Follow-Ups

- 
