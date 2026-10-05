# S14e: capture the intermittent play-exit exception

Status: In progress (September 27, 2026).

Gate record for
[S14e](../../todo/rendering/vulkan-stall-remediation-todo.md#s14e-capture-the-intermittent-play-exit-exception)
under the todo document's one-by-one protocol. Opened by S14; see the
[S14a record](2026-09-27-s14a-play-transitions.md#the-exit-exception).
Evidence root: `Build/_AgentValidation/20260927-095558-s14-core-update/`
(ignored and disposable; findings are copied here).

## Entry evidence

- In S14, 2 of 7 exits after the full probe sequence (load, stationary view,
  camera motion, an object added, moved and removed, then play entry and
  exit) threw and left the world unhooked. The world tick counters placed the
  throw after node deactivation and before any persistent root was
  reactivated, in the world host's backend teardown. The exception itself was
  not captured: isolated sessions write no general log, and Release builds
  compile engine logging out.
- S14a made any failed transition recover into a live edit-mode world and
  write the failing step and exception to `playmode-transitions.log`, which
  Release builds keep. S14b removed one candidate, the light-probe spawner's
  background placement retry that ran through every exit. The exception did
  not recur in the 13 exits that followed.

## Measurement declared

The item's gate is repeated full probe runs without an exit failure, with any
failure's cause named by `playmode-transitions.log` and fixed. On the final
S14 build (S14d's and S14f's changes included, which also change the exit
path: a restore now destroys the copy it replaces), run the full S14 probe
sequence five times, each on a fresh process
(`scratch/Run-S14eProbes.sh`), and copy any `playmode-transitions.log`
written. A recurrence is any failure log, or an exit that does not return to
edit mode with the world updating. Separately, count every exit of this
work's measurement rounds and gates on builds that write the failure log.

At the S14 rate (2 of 7 full-sequence exits), five clean runs would still
happen about one time in five, so a clean campaign bounds the rate without
proving the cause gone; the disposition has to say so.
