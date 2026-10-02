# Browser snapshot transient input edges

Status: shared input correction passes a narrow production-assembly reproduction;
published Chromium gameplay acceptance remains required.

## Observed defect and root cause

The Editor-published RollingBall world in Chromium run `36995649490` rendered its
lit course, ball, and green HUD. A quick Escape press left the HUD green rather
than changing it to the yellow paused state. The adapter consumed ordered key
transitions only through raw `Keystroke` notifications. Mapped `Pressed` and
`Released` events used only the final held-key mask, so a down/up pair between
updates disappeared. Mouse-button transitions were retained by the snapshot
contract but ignored by the mouse adapter. A quick R reset was subject to the
same keyboard defect; generic frame pixel differences did not prove that its
gameplay reset callback ran.

## Correction

- Replay each device's ordered transitions with zero elapsed time, then reconcile
  final held state without advancing newly changed channels' hold timers
- Keep pending edges in bounded reusable storage; acknowledge repeated sequences
  once and abandon an overflowing batch without leaving a channel pressed
- Retire old edges at mapping, pawn, viewport, UI-capture, and source-reset
  boundaries; neutralize abandoned state without replacement callbacks and
  suppress abandoned held channels until up
- Retain previously accepted state across same-owner UI capture/source resets so
  its normal release can still clear gameplay state; freeze held timers while captured
- Guard raw key/text, button, cursor, and scroll callback continuation against
  registration and source-generation changes, including within a single edge
- Keep fresh nested snapshots queued for the next tick when ownership is unchanged
- Select physical cursor coordinates for physical button pulses even after touch
  ends; retain the existing physical/touch left-button union

No game-specific pause/reset delay, smoke assertion changes, new test suite,
gamepad-source changes, or virtual-control ledger changes are part of this fix.

## Validation

The shared InputIntegration Release build completed with zero warnings/errors.
An ignored runtime reproduction uses the actual `BrowserEngineInputViewport`
source and production snapshot-device assemblies. All 20 cases passed, covering rapid Escape/R mapped
events, rapid mouse edges, exactly-once consumption, held timing/repeats, multiple
snapshots before a tick, rebind suppression, UI capture, raw and mapped source
reset, fresh nested snapshots, overflow, touch/physical union, and physical cursor
ownership. Its 2,048 warmed keyboard/mouse ticks allocate zero managed bytes.

Evidence is under
`Build/_AgentValidation/20261001-225000-lit-surface/input-edges/`; the narrow build
log is `Build/_AgentValidation/20261001-225000-lit-surface/input-edge-build.log`.
These are CPU/input boundary checks. They do not establish the yellow paused HUD,
gameplay reset, or browser/device acceptance after publication. Keep the existing
Chromium GPU smoke assertions unchanged and validate the exact published commit.
