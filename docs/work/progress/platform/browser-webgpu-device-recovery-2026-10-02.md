# Authored browser WebGPU device replacement

Date: 2026-10-02

## Delivered behavior

`EngineCanvasHost` distinguishes unexpected device loss from ordinary rendering
failure and intentional teardown. Unexpected loss freezes the animation clock
and input and asks the existing `BrowserEngineSession` to replace only its GPU
owner. It does not call world startup, end play, stop the engine, reload assets,
or recreate the player, physics scene or authored world.

The session retains the live `RuntimeWorld`, `GameState`, player, input viewport,
render viewport, pipeline asset and immutable cooked shader/material/compute
catalogs. The failed renderer first retires all its API wrappers through the
existing `BeginBackendRetirement` / `DestroyCachedAPIRenderObjects` lifetime.
Fresh renderer ownership and a monotonically increasing browser session ID
separate each physical device. The module generation remains the identity of
the installed module, not a device-recovery counter.

The browser requests a new adapter/device and takes a fresh capability snapshot.
A new canvas target permits preferred-format renegotiation. All artifact
bindings, including optional skinning and luminance kernels, are restored before
engine resource creation. CPU/cooked mesh, texture and material descriptions are
retained; ordinary wrappers reconstruct physical storage and cached bindings.
Existing asynchronous compilation, upload, readback/map and completion paths
check renderer/session ownership, and the old JavaScript session map entry is
removed before the replacement is published.

`XRRenderPipelineInstance.ResetAfterRendererRetirement` is an explicit terminal
device boundary, requiring the render thread and a retired owner with no API
wrappers. It uses the existing resource cleanup path to dispose pending
incremental factories, active and retired output resources, and old completion
receipts. It clears obsolete failure/backoff and current-frame readiness and
invalidates histories. Only aliases to destroyed resources are removed;
numeric/string pipeline values and surviving external asset aliases remain.
The pipeline asset, scene commands and viewport binding are retained. This is
not the normal resize path; transactional resize behavior and desktop call sites
are unchanged. A normal physical-resource invalidation was insufficient here:
same-key layouts intentionally reuse their active/pending generation, and a
previous device's generation failure otherwise aborts each retry prematurely.

Replacement preparation runs `StepFrame(0, dispatchSimulation: false)`, so jobs,
collection, swapping and rendering can rebuild resources while fixed/variable
gameplay and elapsed simulation time do not advance. New shadow-wrapper
ownership invalidates the existing shadow-reuse receipts. Preparation exceptions
are retained as recovery diagnostics instead of terminating the caller-thread
timer. A timer already stopped for another reason is rejected explicitly.

Before gameplay resumes, the host requires a complete engine output submission,
successful validation/out-of-memory scopes, and bounded queue completion. A
resize/hide transition during that wait cannot qualify the obsolete surface.
Stop/start epochs and exact renderer/session comparisons reject late startup,
failure and completion callbacks. Explicit renderer disposal already suppresses
its own `device.lost` callback.

## Bounds and failure policy

- Three replacement attempts per world session, including attempts after an
  earlier successful recovery
- 20 seconds per device acquisition; canceled or late device results are retired
- 45 seconds of active frame time for replacement preparation; hidden/detached
  output suspends this clock
- 10 seconds per replacement validation/completion wait
- First and subsequent failed-owner diagnostics retained in a bounded list and
  reported to the browser console
- Exhaustion retires the candidate device and keeps the existing world paused
  with an explicit **Retry reloads the world** message
- No WebGL switch, CPU rendering substitute, automatic whole-game restart, or
  attempt to restart a stopped engine timer

## Validation and remaining acceptance

The narrow Release browser build passed with zero warnings and zero errors,
using the existing browser source/native Jolt inputs and existing in-process
WASM SDK task overrides. The initial build without those overrides stopped at
the known `MarshalingPInvokeScanner` task-host creation failure
(`MSB4216`/`MSB4027`); it was not a C# diagnostic. The later alias-preservation
refinement passed the integrated Editor, Server, VRClient, desktop WebGPU,
RenderingParity, nineteen portable-project builds and fresh native-Jolt WASM
publication with zero compiler warnings/errors. This compile evidence does not
replace live forced-loss acceptance.

JavaScript syntax and whitespace validation pass. An independent read-only
lifetime review found and closed the stale generation/backoff and partially
materialized same-key resource hazards. Disposable production-host probes pass
with controlled renderer and managed boundaries for:

- No host world restart, fresh owner/session/format and removal of old map entries
- Readiness held until replacement validation/completion, with stale callbacks ignored
- Resize during queue completion requiring a fresh output frame
- Rejected preparation retry without calling engine Stop
- Finite exhaustion retaining a paused world and releasing candidate ownership
- Stop during pending acquisition, with late completion unable to resume or publish
- Loss during initial acquisition, with the obsolete startup continuation unable
  to fail its replacement
- Hidden output during completion remaining paused until a new visible frame,
  without accumulating suspension time

The probes execute the unchanged production host body but use controlled device
and engine boundaries. They do not prove an actual GPU restoration, authored
game-state identity, physics continuity, or physical mobile behavior. No tracked
tests were added. Local browser launch and cloud loopback access were already
restricted, and those restrictions were not bypassed. Live forced-loss acceptance
must exercise the authorized browser milestone, including loss during initial
preparation, repeated loss, outstanding shader/readback/map work, hidden/visible
and detached/reattached output, orientation while recovering, and explicit
stop/retry. Desktop renderer qualification remains separate.

Disposable evidence under
`Build/_AgentValidation/20261001-225000-lit-surface/`:

- `logs/browser-device-recovery-build.log`
- `logs/browser-device-recovery-build-final.log`
- `scratch/browser-device-recovery-probe.mjs`
- `logs/browser-device-recovery-probe.log`
