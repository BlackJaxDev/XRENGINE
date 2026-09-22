# S10 Toolbar Icon Preparation Gate Record

Date: 2026-09-21

Status: Validated

Owner: ImGui editor toolbar and renderer preview publication

## Problem And Entry Evidence

The first toolbar draw synchronously searched for SVG files, parsed and
rasterized each SVG with Skia, constructed an `XRTexture2D`, and requested a
backend preview handle. The existing one-icon-per-frame throttle limited count,
not duration: the matching cold Vulkan capture recorded `UI.DrawToolbar` at
729.970 ms inclusive/self inside a 746.790 ms frame. The warmed toolbar draw was
0.070 ms, which isolated first-use icon work rather than normal ImGui drawing.

The falsifiable hypothesis was that removing path lookup, file reads, SVG parse,
and Skia rasterization from `DrawToolbar`, while publishing only bounded results
on the render owner, would eliminate that cold draw leaf without changing icon
appearance or backend ownership.

## Acceptance And Ownership

- `DrawToolbar` may only look up an already-ready renderer-local handle. It must
  not perform file IO, SVG parsing, rasterization, texture construction, task
  creation, or upload.
- A fixed 12-icon manifest is prepared by one cancellable sequential worker.
  Inputs are capped at 512 KiB, file reads get at most two attempts, and only
  immutable pixel results cross to the render owner.
- The render owner drains at most four completions, constructs at most two
  textures, and starts at most one upload per renderer per render frame. A 1 ms
  elapsed guard prevents starting additional owner work after the budget is
  consumed; it cannot preempt one native backend call already in progress.
- Upload state is keyed by renderer instance, not only backend generation, so a
  same-backend replacement cannot reuse a stale native handle. Upload attempts
  are capped at three with frame backoff.
- Prepared pixels and logical textures survive renderer replacement; native
  wrappers remain renderer-owned and retire through the existing preview
  service. Shutdown cancels preparation, rejects stale completions, waits a
  bounded two seconds, and releases managed cache ownership after engine
  teardown.
- While pending or failed, the existing text button remains interactive.

The design review rejected moving the current mutable texture factory wholesale
to a worker and rejected a broader SVG service or atlas rewrite. It also required
renderer-instance identity, bounded queues/retries, explicit pixel ownership,
and a pure draw lookup; the implementation follows those constraints.

## Implementation

`EditorImGuiUI.Initialize` now starts CPU preparation before the first toolbar
draw. The worker snapshots candidate paths, performs bounded reads and Skia
rasterization, and enqueues tightly packed RGBA pixels. A separate
`UI.ToolbarIconOwnerWork` scope immediately before `UI.DrawToolbar` publishes
textures and preview handles under the render owner. Toolbar UV orientation now
uses the backend preview service's `requiresVerticalFlip` result instead of an
unconditional flip.

The cache uses a fixed toolbar-size key. Multiple toolbar sizes are therefore
outside the active surface, while the key and preparation record retain size as
part of their identity. The toolbar itself belongs to the main ImGui viewport;
multi-viewport rendering does not create a second toolbar cache. Global result
publication runs once per render frame, and renderer-local upload accounting runs
once per renderer instance per frame.

## Validation

- `dotnet build XREngine.Editor/XREngine.Editor.csproj --no-restore` passed with
  zero warnings and zero errors. Both isolated validation-session builds also
  passed with zero warnings and zero errors.
- Vulkan prepared and published all 12 icons. CPU rasterization totaled
  995.149 ms, including a 954.963 ms first Skia warmup, entirely on the worker.
  Once ready, `UI.DrawToolbar` measured 0.023 ms and
  `UI.ToolbarIconOwnerWork` measured 0.013 ms in the sampled warm frame.
- A same-backend Vulkan renderer restart reuploaded all 12 handles while the
  ready summary remained at exactly 12 CPU rasterizations and the same
  995.149 ms total. This rejects stale-handle reuse and duplicate CPU work even
  though that renderer reports generation zero.
- A clean OpenGL startup prepared and published all 12 icons. CPU rasterization
  totaled 570.402 ms, including 549.196 ms for first Skia warmup. A composited
  window capture was opened and inspected: transform, space, snap, and playback
  icons were present, upright, correctly sized, and had no fallback labels.
- The Vulkan viewport readback succeeded, but Vulkan composited-window capture
  timed out. Vulkan correctness is therefore supported by its 12/12 nonzero
  handle publication, warm toolbar profile, and renderer-restart result; the
  actual full-toolbar image was inspected on OpenGL.
- Missing, malformed, oversize, stale-session, and exhausted-upload paths are
  isolated from drawing, bounded, diagnostic, and retain the text-button
  fallback. They were reviewed in the implementation but were not fault-injected
  into the live asset tree. No automated tests were added or run because test
  changes for this integration were not authorized.
- Both owned editor sessions shut down through the session manager without a
  toolbar exception or retained worker.

## Result And Remaining Risk

S10 passes its gate: synchronous file lookup/read, SVG parse, Skia rasterization,
texture construction, and upload are absent from the normal draw path; icons
eventually render on both affected backends; and the warmed draw/owner scopes are
well below 0.1 ms in the sampled ready frame.

The largest observed first Vulkan preview upload still occupied 85.753 ms (later
cold calls included 30.366, 10.283, and 9.413 ms). The owner pump limits count and
does not start another operation after its elapsed guard, but an individual
native upload is not preemptible. This residual cold backend tail is explicitly
outside `UI.DrawToolbar`; it is diagnosed below rather than being hidden as a
completed time-bound upload solution.

## Follow-Up Diagnosis: Synchronous Vulkan Preview Publication

Status: Remediated and live-validated

Two additional isolated cold Vulkan launches reproduced the variable owner-side
tail. The first toolbar publication measured 62.970 ms in one launch and
5.535 ms in the next; subsequent toolbar publications in the latter launch were
0.863-1.922 ms. Each icon contains only 2,304 bytes of RGBA pixels, so transfer
bandwidth cannot explain the cold latency.

Source tracing found that Vulkan preview registration ignores
`RenderTexturePreviewOptions.UploadIfNeeded`. When the global synchronous
resource-upload gate is open, it creates the texture wrapper and immediately
uses the legacy synchronous upload path. One tiny icon consequently performs
three separate graphics-queue submissions and unbounded fence waits: an initial
layout transition, the buffer-to-image copy, and the final shader-read layout
transition. It also creates and destroys a one-shot command buffer and fence for
each operation. The toolbar path bypasses `VulkanTextureUploadService`; its
service telemetry remained at zero while these preview uploads ran.

Temporary phase timing of the same synchronous command helper observed fence
waits of 5.150 ms for a layout transition and 3.263 ms for a buffer-to-image
copy during startup. Those samples were not the original 85.753 ms toolbar call,
but they directly demonstrate that queue waits dominate this mechanism and that
unrelated graphics-queue backlog can be inherited by the calling render owner.
Together with the reproduced 62.970-to-5.535 ms cold variance and the 2.3 KiB
payload, the evidence localizes the residual tail to synchronous queue
serialization and cold native-resource setup rather than pixel preparation or
copy throughput.

The implemented remediation uses the existing Vulkan texture upload pipeline.
Preview registration now performs published-descriptor lookup only: it never
consults the synchronous-upload gate, calls `PushData`, or creates native image
data inline. When `UploadIfNeeded` is true and the image genuinely needs data,
the frame loop admits one renderer-local request through
`TryScheduleRawResidentDataForVulkan`; the existing manager owns its generation,
cancellation, transfer, descriptor publication, and retirement. Repeated polls
observe the exact upload ticket and return pending without consuming a toolbar
failure retry. Descriptor-allocation failures are classified separately and do
not spuriously schedule another upload.

The ImGui registration path now commits a new or replacement descriptor only
after both descriptor-set allocation and active descriptor-heap payload writing
succeed. Renderer detachment cancels preview admission and drops the weak
renderer-local ticket ledger, while the upload service performs its existing
prepared-resource cleanup. No toolbar-specific Vulkan queue was added.

An isolated Vulkan editor run validated the change:

- all 12 toolbar icons produced 12 worker preparations, 12 completed 2,304-byte
  transfer chunks, and 12 final descriptor publications with zero upload
  failures;
- transfer recording measured 0.130 ms CPU, native allocation 0.055 ms,
  staging copy 0.007 ms, and the sampled GPU transfer 0.001 ms;
- after the first combined completion/texture/scheduling frame, owner-pump calls
  measured 0.011-0.476 ms; same-backend renderer-restart reupload calls measured
  0.013-0.091 ms;
- both the initial and post-restart ready summaries reported 12/12 icons with
  the original 12 CPU rasterizations, and composited Vulkan captures were
  inspected with the transform, space, snap, and playback icons present and
  correctly oriented;
- no Vulkan validation message or preview-upload failure was recorded. One
  handled framebuffer-publication exception occurred earlier in the broader
  dirty-branch run and recovered; it did not coincide with preview upload or
  descriptor publication.

Diagnostic instrumentation was removed after the isolated sessions. A RenderDoc
capture was not taken because source and timing evidence identified a CPU-side
fence wait; capture overhead would perturb the cold launch without resolving a
remaining GPU-resource-content question.
