# S14c: restore per-component tick timing

Status: Validated (September 27, 2026).

Gate record for
[S14c](../../todo/rendering/vulkan-stall-remediation-todo.md#s14c-restore-per-component-tick-timing)
under the todo document's one-by-one protocol. Opened by the
[S14 gate record](2026-09-27-s14-core-update-owner.md). Evidence root:
`Build/_AgentValidation/20260927-095558-s14-core-update/` (ignored and
disposable; findings are copied here).

## Entry evidence

- The profiler's Component Timings panel, enabled by **Enable Profiler
  Component Timing** together with **Frame Logging**, shows no data. Its only
  producer was the legacy `Engine.TickList`, which records a component's tick
  time while a timed update frame is open; nothing constructs that list since
  world ticks moved to `RuntimeWorldLifecycle`.
- The profiler still opens and closes a component timing frame around every
  update dispatch (`EngineTimer.DispatchUpdate`) and builds the panel's
  snapshot from whatever was recorded, which is nothing.

## Hypothesis, change and acceptance

Declared after the change was drafted, while the S14b validation build ran,
and before this change was built or measured; recorded here as a deviation
from declaring before the change.

Hypothesis: the panel is empty only because no producer feeds it; recording
from the actual tick dispatch restores it, at no cost while the toggle is off.

Change:

1. Core defines `IRuntimeComponentTickTimingRecorder` and the static
   `RuntimeComponentTickTiming.Recorder`, null while timing is off.
2. `RuntimeWorldLifecycle` reads the recorder once per queue dispatch. While
   it is set, the dispatch times each callback and reports it with its owner:
   the callback's target when that is a component, or the component a
   compiler-generated closure captured (an animation tick wraps the caller's
   delegate in one). The observed dispatch of the world tick counters reports
   the same way when both are on.
3. The profiler installs itself as the recorder while **Enable Profiler
   Component Timing** is on and removes itself when it is turned off. Its
   recording method becomes the interface implementation, and the hooks only
   the legacy list used are removed.
4. The legacy `Engine.TickList` is deleted.

Acceptance, on the S13a fixture:

- with Frame Logging and Component Timing on, the CPU frame dump's
  Component Timings section lists the components that own ticks according to
  the world tick counters (the editor camera pawn, the FPS text through its
  animation tick, the skybox and the audio listener), with call counts that
  match their tick groups;
- with the toggle off, the tick dispatch allocates nothing (0 bytes per
  dispatch in the world tick counters, as in S14) and the dump reports no
  component timings once the toggle is turned off again.

Falsifiers: no components, components the counters do not show, or any
allocation per dispatch with the toggle off.

## Result

Release build with the S14a, S14b and S14c changes, S13a fixture, world tick
counters on, Frame Logging on, three 15 s windows: Component Timing off, on,
and off again. "Dispatch itself" is each group's allocation minus the
callbacks' own allocation, from the counters. Evidence:
`reports/s14c-timing`.

| Window | Normal: dispatch itself per dispatch | Late | Fixed-update groups | Component Timings in the CPU frame dump |
| --- | --- | --- | --- | --- |
| Off | 0 B | 0 B | 0 B | None captured |
| On | 288 B | 96 B | 0 B | 4 components (below) |
| Off again | 0 B | 0 B | 0 B | None captured |

With the toggle on, the dump's last frame lists:

| Component | Node | Calls | Group mask |
| --- | --- | --- | --- |
| EditorFlyingCameraPawnComponent | Editor View | 2 | Normal |
| AudioListenerComponent | Editor View | 1 | Late |
| UITextComponent | TestTextNode | 1 | Normal |
| SkyboxComponent | TestSkyboxNode | 1 | Normal |

These are exactly the tick owners the counters report: the camera pawn's two
Normal ticks (input and tick), the audio listener's Late tick, the skybox's
Normal tick, and the FPS text's Normal animation tick, whose registered
callback is a compiler-generated closure resolved to the text component.

- With the toggle off the dispatch allocates nothing in any group; the
  counters' observed dispatch, which the check runs through, reads the
  recorder and skips it. The unobserved dispatch has the same single static
  read.
- With the toggle on, the dispatch allocates 96 bytes per timed component
  per update: the profiler starts a new frame state each update and creates
  one accumulator per component in it. That is the profiler's existing
  per-frame design, paid only while the diagnostic is on; it is recorded
  here and not changed.

Acceptance met; no falsifier occurred.

## Disposition

Validated. The Component Timings panel is fed by the world tick dispatch
again, and the legacy tick list is removed. Tick callbacks run on the
fixed-update thread between timed update frames are not recorded, as before.

## Gate record

- Item / owner / status: S14c, Profiler; Validated.
- Prior validated item: S14b; no reordering.
- Hypothesis and disconfirming check: as declared above, with the deviation
  noted there (declared after the code was drafted, before it was built or
  measured); no falsifier occurred.
- Evidence: `reports/s14c-timing`; profiler guide updated.
- Tests: the tick lifecycle tests pass with the final build; see the
  [S14a record](2026-09-27-s14a-play-transitions.md#tests).
