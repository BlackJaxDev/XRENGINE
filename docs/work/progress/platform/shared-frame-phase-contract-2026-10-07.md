# Shared frame phase contract

Updated: 2026-10-07.

## Contract and scheduling

Desktop and browser use the same engine phase operations. The browser composes
them into one serial, host-driven frame. Desktop retains its independent update,
fixed-update, collection, and rendering schedules. The 2026-10-07 decision
preserves desktop and VR pacing; it does not require one desktop presentation to
wait for a new fixed tick, variable update, and collection transaction.

This resolves the earlier requirement's ambiguity. Running every phase through
desktop worker barriers would retain thread names but change cadence and
collection/render overlap. A method that only selects separate implementations
would not establish shared phase ownership either.

## Common operations

`EngineTimer.FrameDispatch.cs` owns the phase bodies:

| Operation | Desktop caller | Browser caller |
| --- | --- | --- |
| Accumulate fixed ticks with the existing four-tick debt cap | `FixedUpdateThread` after its own elapsed-time sample | `StepFrame` after its host elapsed-time sample and simulation admission |
| `DispatchAccumulatedFixedUpdates` | The fixed worker when a tick is due | The admitted simulation part of `StepFrame` |
| `DispatchVariableUpdate` | `DispatchUpdate` after its existing rate and pause checks | The admitted simulation part of `StepFrame` |
| `TryCollectVisibleGeneration` | `RunCollectVisibleIteration` | The visibility part of `StepFrame` |
| `TryPublishCollectVisibleGeneration` | The collect worker after render-consumption credit | The swap part of `StepFrame`, followed by exact-generation consumption |
| `DispatchRenderFrame` | The render owner through the existing timer dispatch | The render part of `StepFrame` on the caller/render owner |

The two fixed-debt expressions now call `AccumulateFixedUpdateTicks`. Its
arithmetic is unchanged: add the supplied elapsed ticks and cap debt at
`_fixedUpdateDeltaTicks * MaxFixedCatchUpSteps`. The existing dispatcher retains
its four-step limit and residual-debt handling. The new method adds no frame
object, task, delegate, collection, or allocation.

`BeginExplicitFrame` initializes deterministic timing and returns a scope.
Its caller must drive the real collection and rendering lifecycle. The normal
browser frame continues to call `BeginExplicitFrameClock` directly, so it does
not allocate that scope per frame.

## Preserved ownership

- The desktop fixed worker keeps a stable physics-thread ID, samples its own
  clock, drains physics work while paused, and waits on that same worker when
  no tick is due.
- Variable update keeps its target period, quantization correction and slow
  update policy. Desktop callback faults keep their existing handling.
- Collection retains render-consumption credit, generation-zero bootstrap and
  the optional stale-visibility reuse rule. World swaps precede viewport swaps.
- Rendering remains synchronous on its registered owner. Native event pumping,
  modal resize, and the collapsed or dedicated render-thread selection remain
  in their existing hosts.
- Vulkan can release collection after its next frame-slot wait and before
  blocking desktop presentation. The timer does not add an end-of-frame barrier.
- OpenXR and OpenGL context ownership remain in the existing render callbacks.
  Recording and GPU submission stay in the renderer; no second render model is
  introduced.
- Browser stepping retains its supplied clock, pause/single-step admission,
  phase order, terminal-fault handling and caller-specific publication identity.
  Its raw collect counter and canonical render ID remain distinct.

## Validation boundary

Source comparison confirms that the shared accumulator expression matches both
previous copies after whitespace normalization. The .NET 10.0.401 Release Host
graph build passes with zero warnings and errors. Its command is:

```sh
MSBuildEnableWorkloadResolver=false dotnet build XREngine.Runtime.Host/XREngine.Runtime.Host.csproj -c Release -m:1 -p:Platform=AnyCPU --artifacts-path Build/_AgentValidation/00000000-000000-shared/frame-phase-contract/host-build
```

The workload resolver is disabled for this managed Host-only check. This does
not publish WebAssembly or qualify a browser. The log is
`Build/_AgentValidation/00000000-000000-shared/frame-phase-contract/host-build.log`.
Independent source review passes. Both schedules reach the authoritative fixed,
variable, collection, publication, and render-dispatch bodies. The review found
no remaining implementation gap under the preserved-scheduling contract and no
new allocation or changed clock, fence, owner, wait, or fault path. The change
does not add or modify tests.

This closes the shared-phase implementation requirement in the active checklist.
It credits the audited combined implementation and the clarified contract; the
small accumulator extraction is not a newly implemented frame system. Support
for the complete caller-thread lifecycle is a separate requirement.

The existing caller-thread frame probes and engine timer/generation/ownership
tests remain the relevant behavior checks. The source extraction does not
establish live desktop editor or VR pacing, complete world allocation behavior,
or a new browser acceptance result. Those requirements remain open in the
[runtime checklist](../../todo/platform/unified-desktop-browser-runtime-todo.md).
