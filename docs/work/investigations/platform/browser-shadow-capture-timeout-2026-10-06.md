# Browser shadow capture timeout

## Observed failure

[Run 37438717618, job 112201291828](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37438717618/job/112201291828)
failed the first shadow OFF capture on source `f0d323533`. The ordinary
published engine reached `running`. The native no-modifier pipeline compiled
in 11,888.6 ms. The failure snapshot recorded seven present draws, 14 native
shading dispatches, 1,241 queue submissions, no device errors, and no GPU read
mappings. The ON comparison did not run.

The inspected full-page failure PNG shows the receiver and both occluders.
Its canvas-relative foreground bounds are approximately `[303,180,486,371]`.
The pinned fixture projection expects approximately
`[303.20,179.87,485.88,370.70]`. This image supports the expected placement;
it does not establish a successful bounded element capture or ON/OFF parity.
No `advanced-shadow-off-0-playing.png` was saved. Capture exceeded its
10,000 ms deadline before the first element screenshot returned, before
pixel inspection and alignment validation.

The original timeout did not retain the active operation. Scroll handling,
geometry retrieval, and screenshot generation remain possible causes. The
native compile duration alone does not explain this timeout: capture starts
after `running`, which requires a complete current-surface submission. That
state does not prove GPU completion. Passing ordinary Advanced captures do
not prove a 10-second capture time; their helper checks its deadline between
attempts and can accept a screenshot that returns after that deadline.

## Bounded diagnostic change

The shadow helper retains each awaited operation's phase, attempt, relative
start time, elapsed time, remaining budget, and outcome. It keeps at most
64 step records and an omitted-step count. A fixed phase map retains the
latest outcome even when that record limit is reached. Failure evidence also
includes the active phase, available pre/post geometry, and the last complete
pixel summary. Unrun values are explicit; attempt numbers distinguish a prior
complete summary from the current attempt. A copy is attached to the error
before browser evidence collection and cleanup.

The diagnostic-only change kept the absolute 10-second deadline, capture
operations, operation order, screenshot method, fixture geometry, comparison
criteria, and all assertions unchanged. It added no browser request, trace,
GPU readback, startup delay, retry allowance, or privilege.

Only after a failure, the report also samples the current asset delivery
counters after its unchanged browser evidence read. It does so only while the
expected ON/OFF document still reports `running`. The running player has evaluated
`engine-runtime.js`, including its static `./engine-assets.js` import. The
failure read resolves that same URL from the current publish mount and uses
the cached module. It does not import when that proof is absent. A one-second
absolute Node-side deadline bounds the added evaluation even if the page is
blocked. Page-side deadline checks also prevent a late evaluation from starting
the import or reading counters after the limit. Unavailable data has an explicit
fixed reason. The original browser evidence and failure cleanup are preserved.

Only `opening` or `ready` state and eight nonnegative safe-integer counters
are retained: `verifiedAssets`, `essentialVerifiedAssets`, `essentialAssets`,
`failedReads`, `cancelledReads`, `activeRequests`, `queuedReads`, and
`retainedAssets`. Raw errors, URLs, and asset lists are excluded. The snapshot
does not change successful execution, application state, or request-failure
acceptance. These aggregate counters cannot identify individual request attempts.

## Separate request failures

The same OFF log has 112 early `net::ERR_ABORTED` request events across 105
unique payload paths, from 09:28:21.826 to 09:28:28.358 UTC. These paths match
171 HTTP 200 server records. Passing ordinary Advanced runs have 69 and 70
such events. `run.mjs` sets the recorded status before streaming and retains
200 if streaming later fails after response headers. A server response status
does not prove that the client received and verified the complete body.

`content-loader.js` validates the full expected length before returning and
then verifies the payload hash. Its request cleanup calls `reader.cancel()`
even after stream completion. Read release in `engine-assets.js` also aborts
its controller, but normal completed-read cleanup occurs after the loader has
removed that controller's linked listener. These are candidate paths, not
proof of the observed abort cause. The request's 30-second deadline does not
explain events within the first eight seconds of the page lifetime.

The saved browser events have no request-attempt identity or abort initiator.
The original failure snapshot has no asset `failedReads`, `cancelledReads`, or
`lastError`, although delivery statistics expose those fields. Exact-path
matching cannot distinguish repeated attempts. The shadow assertion that
rejects request failures remains in place. Cancellation ownership and
completed asset reads require separate evidence before any exception can be
considered.

## Measured screenshot timeout and bounded correction

[Run 37447198630, job 112228105199](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37447198630/job/112228105199)
on source `e9fa29daf` retained the first OFF attempt's failing phase. Scroll
completed in 2,294 ms, and geometry read completed in 32 ms. The element
screenshot then exhausted its remaining 7,674 ms. Post-capture geometry and
pixel inspection did not run. The inspected failure PNG still shows the
receiver and both occluders. This establishes a screenshot-phase timeout;
it does not identify the stalled operation inside Playwright or Chromium.

Delivery was `ready`, with all 349 essential assets verified, zero failed or
cancelled reads, and no active or queued requests. The 88 browser
`ERR_ABORTED` events remain unclassified. Aggregate delivery success does not
permit a request-failure exception.

[Playwright 1.63's screenshot implementation](https://raw.githubusercontent.com/microsoft/playwright/v1.63.0/packages/playwright-core/src/server/screenshotter.ts)
performs another element-stability wait inside an element screenshot after
the harness has already scrolled and measured the canvas. Its page screenshot
path omits that second element wait. Font, layout, and browser capture work
still occurs. The correction uses this page path with a clip restricted to
the complete canvas. Removing the duplicate wait is a source-supported change;
the runtime evidence does not prove that wait caused the timeout.

The clip uses the pinned
[document rectangle rounding](https://raw.githubusercontent.com/microsoft/playwright/v1.63.0/packages/playwright-core/src/server/helper.ts):
add scroll offsets to the measured canvas origin, floor near edges with
`+0.001`, and ceil far edges with `-0.001`. Subtract the same scroll offsets
to obtain the page screenshot's viewport clip. Chromium adds the visual
viewport's page origin when it constructs the
[capture rectangle](https://raw.githubusercontent.com/microsoft/playwright/v1.63.0/packages/playwright-core/src/server/chromium/crPage.ts).
Unit visual-viewport scale and zero visual offset are required. The observed
`x=16`, `y=202.875`, `width=977`, `height=549.5625`, and zero scroll produce
`x=16`, `y=202`, `width=977`, `height=551`.

The harness rejects clips outside the viewport instead of accepting
Playwright's automatic clip trimming. It retains pre/post canvas, scroll,
DPR, and viewport geometry and requires exact screenshot clip dimensions.
The projection, exterior, reference alignment, surface, and receiver-effect
checks remain in place. A changed size, position, scroll offset, DPR, or
viewport cannot qualify the image. The original absolute 10-second budget,
first scroll, step timing, failure evidence, and request-failure assertion
remain unchanged. The correction starts no extra trace or GPU readback.

## Status layout after resize

[Run 37452750729, job 112252327999](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37452750729/job/112252327999)
on `d7fe4e8` passed the initial OFF image and GPU qualification. Its initial
image had no exterior mismatch and a maximum projection-edge error of 0.303
pixels. The first resized capture completed 44 screenshot attempts but did
not qualify an image within the unchanged 10-second budget. The ON run did
not start.

The last pre-capture canvas origin was `(16,178.875)`; the post-capture origin
was `(16,202.875)`. Both samples had the same CSS size, `813x457.3125`, backing
size, `813x457`, viewport, `860x780`, and zero scroll. The inspected resized
PNG includes a 24-pixel page strip above the canvas. The full-page failure
PNG shows the complete world and the running status wrapping to two lines.
The strict geometry and exterior checks correctly rejected that shifted clip.

The shipping `engine-player.html` placed variable status and delivery-progress
paragraphs before the canvas. `engine-player.js` replaces the status text,
and `EngineCanvasHost` reports a short preparation message after a size
change, then the longer running detail when the new output is ready. The
page's `16px/1.5` text has a 24-pixel line height. That source path explains
the observed displacement. The exact sequence of status strings was not
recorded; it remains an inference from source, geometry, and the final image.

The evidence does not support a repeated surface-generation loop. Texture
observation was unsaturated and recorded exactly one new canvas depth and
one `813x457` output family. Native compile and pipeline/module creation
snapshots did not change. `WebGpuCanvasRenderer.resize` retains the current
generation for unchanged dimensions. The host clears presentation only when
that generation changes; its attachment observer ignores ordinary status
text changes. Resource profiles use output dimensions and rendering settings,
not the DOM position. The submission snapshot's present count advanced from
9 to 14. The earlier GPU snapshot counted 7 because it was collected first.

The production correction places the existing status and delivery-progress
paragraphs immediately after the canvas. Their full text, IDs, status role,
visibility rules, and updates remain unchanged. Message wrapping and delivery
progress can change the space below the render surface without moving it.
The publisher copies this template to the shipped `index.html`; the legacy
diagnostic page is unchanged. The existing stable scrollbar gutter is preserved.
The renderer, readiness checks, capture helper, deadlines, and all assertions
are unchanged.

Delivery was ready with all 349 essential assets verified and zero failed,
cancelled, active, or queued reads. The 59 browser request-abort events still
require separate attribution. The layout correction does not classify those
events or establish that all resize preparation latency is resolved.

## Stable layout with late resized output

[Run 37460775847, job 112270435960](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37460775847/job/112270435960)
on `a9037c2` passed the initial OFF capture. The first resized capture kept
the canvas at `y=138.875` with identical pre/post geometry. Attempt 36 still
contained only the CSS background, RGB `(8,13,20)`, after 7,467 ms. Attempt
37 entered screenshot capture at 7,602 ms and exhausted the remaining
2,398 ms. The inspected later failure PNG shows the correct resized receiver
and occluders. The layout correction therefore holds, but timely resized
output remains unqualified.

Old native outputs, texture IDs `11/13/15/3`, last appeared at queue serial
1211. New `813x457` outputs, IDs `38/40/42/30`, appeared in five native
consumer submissions at serials 1566 through 1570. The original records have
no submission timestamps. Preparation-only iterations can submit an empty
command-buffer list, so the intervening serials do not establish expensive
GPU work. The harness completion serial 1208 belongs to its initial explicit
checkpoint. Production tracks its own completion receipts; the saved value
does not show that production completion stopped.

Source inspection found serial pending gates during logical materialization,
backend physical generation preparation, and later Advanced command preparation.
The backend returns at the first pending resource, which delays independent
resources. Batching that work requires retained completion state and bounded
attempt/request admission; it cannot be implemented safely by ignoring pending
exceptions or removing limits. The present evidence does not identify which
gate consumed the capture interval. No renderer change is included.

The next diagnostic packet reads existing state only for the first OFF resize
to width 860. It has four fixed checkpoints for the entire run: capture start,
first blank image, the first opportunity after half the capture budget, and
capture failure. Each read uses a maximum 200 ms Node-side cap within the
original absolute capture deadline. A failure after that deadline records an
unavailable checkpoint without another browser read. Missing checkpoints have
fixed reasons. Initial captures, later OFF resize, and both ON runs do not
collect these checkpoints.

Before that resize, a 500 ms bounded cached-module lookup installs a one-use
observer on the existing host's normal `syncSurface` call. The observer restores
the original method before forwarding the original receiver and arguments;
it never invokes an additional surface update. A bounded cleanup also restores
an observer that was not invoked. The live getter rejects stale host ownership
and late execution, including immediately before each managed export call.

Snapshots retain existing preparation state, a managed status summary of at
most 4,096 characters rebuilt from fixed enums and validated numeric/boolean
fields, selected frame/receipt counters,
the production completion sequence, and at most eight active receipt records.
At most 16 Advanced records retain allowlisted stage, phase, and state names,
numeric frame/generation values, and reason-presence flags. Unknown names and
missing fields remain explicit. Advanced preparation retains only draw count,
publication state, generation, and deferral-presence flags. The full managed
status can include descriptors and failure strings; those text values are not
copied into the diagnostic record.
Physical request summaries examine at most 64 retained requests and include
fixed kind/state counts plus omitted counts. Kind slots 1 through 7 mean
buffer, texture, texture view, sampler, binding layout, binding group, and
prepared commands. State slots 1 through 4 mean pending, ready, failed, and
cancelled. Slot zero records unknown values. No raw exception, descriptor,
buffer, asset list, or URL is retained. No statistics are enabled, and no new
GPU wait, submission, readback, frame, or profiling trace is requested.
Timestamp scalars are added to the existing capped GPU observation records
without adding a per-submit log. Their clock origin permits comparison with
the capture checkpoints and distinguishes enqueue time from completion.
An immediate evaluation failure has a different fixed reason from a read
timeout. Setup and cleanup caps are separate from image acceptance; all four
snapshot reads consume the original capture budget. A Node deadline cannot
interrupt a synchronous managed getter that started before that deadline.
The getter checks again before starting another managed call. Snapshot work
can affect scheduling, and samples cannot reconstruct every intervening frame.
Production completion sequences must be compared with the retained engine
frame sequence, not directly with the harness's queue-submit serials.

## Saved PNG analysis outside the player

[Run 37469658042](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37469658042)
on `883b1f8a` failed before resize. The first OFF attempt spent 2,364 ms
scrolling, 48 ms reading geometry, 6,935 ms taking the page screenshot, and
19 ms reading geometry again. Pixel inspection started with 633 ms left and
timed out. Geometry was stable. The four resize checkpoints did not run.
The saved 977-by-551 PNG is byte-identical to the accepted initial capture
from `a9037c2`: SHA-256
`e689725db8da363078aed195a5f4ce903cb4f9d28506a2b5b934146da646cd9e`.

The old shadow analysis sent each screenshot back to the live page for
`createImageBitmap`, a 2D canvas draw, and `getImageData`. Alignment decoded
the same image again. ON/OFF comparison decoded both images again. This is
additional browser work; the evidence does not establish which decoder
operation, or whether GPU work, consumed the remaining capture time.

The shadow helper now uses one owned Node worker and the PNG reader already
bundled with the exact pinned `playwright-core@1.63.0`. The package exports
`lib/utilsBundle` and `package.json`; the worker checks that version before
using `PNG.sync.read`. No dependency or lockfile changes are required.
The accepted screenshot subset has only IHDR, IDAT, and IEND chunks, 8-bit
RGB or RGBA samples, no interlace or color metadata, valid CRCs, and entirely
opaque pixels. Unsupported input fails. Encoded input is capped at 8 MiB
before hashing or worker transfer. The decoded dimensions are restricted to
the current fixture profiles: 977-by-551, 813-by-459, and 893-by-504, with
backing sizes 977-by-550, 813-by-457, and 893-by-502 respectively.
The last profile follows the current 940-pixel viewport: the observed
15-pixel stable gutter and 32 pixels of body padding leave 893 CSS pixels;
the 16:9 height is 502.3125, rounded to 502 backing pixels. At y=138.875,
the existing clip rounding gives a 504-pixel screenshot height.

The worker retains three OFF baselines and one current image, at most
8,613,232 retained RGBA bytes. This cap does not include transient encoded
input, decoder scratch buffers, or the image being replaced. Each image is
decoded once and bound to its original PNG hash. The summary, projection,
alignment, and receiver comparison arithmetic was moved without changing
thresholds. ON/OFF arithmetic now runs with alignment inside the capture
deadline; its existing acceptance assertions stay in their original places.
No image analysis calls back into the page or renderer.

One worker request can be pending. The parent and worker reject requests
and results at or after the same absolute capture deadline. The encoded
size guard and hash run inside the timed inspection step. A timeout starts
worker termination, and late replies cannot qualify the capture. The
10,000 ms capture budget is unchanged. Worker setup precedes player startup.
Cleanup has a separate one-second cap and preserves the original failure.
It reports success only after an acknowledged termination with a matching
observed exit code; worker errors and unexpected exits remain unverified.

Saved-image analysis through the worker reproduced every recorded summary
and alignment field for the initial `883b1f8a` PNG (21,102 colorful pixels,
accepted), the blank `a9037c2` resize (zero colorful pixels, rejected), and
the displaced `d7fe4e8` resize (15,663 colorful pixels, rejected). Original
PNG hashes were unchanged. These local analyses took approximately 106,
52, and 41 ms; they are not runtime performance or parity qualification.
No new browser, engine, trace, or test run was started.
All three source files passed `node --check`, and the scoped diff check
passed. Independent source review found no blocker after termination
acknowledgement and unexpected worker exit handling were made explicit.

## Validation and next step

The source and saved runtime artifacts were inspected. No new runtime or test
run was performed for this diagnostic change. `node --check` and the scoped
`git diff --check` passed. Independent source review found no blocking issue
in the capture timing change after the error-evidence access was made null-safe.
Narrow independent review also found no blocking issue in the final
delivery-counter snapshot with its absolute Node-side deadline.
The later saved run identified the screenshot phase. The bounded page-clip
correction passed `node --check`, the scoped `git diff --check`, and independent
source review with no blocking finding. Pre/post samples cannot exclude a
transient change that returns to the original geometry between samples. The
next authorized shadow run must establish whether capture completes and image
qualification remains valid. Source checks do not prove that the timeout,
request failures, or shadow parity are fixed.

The later `d7fe4e8` artifacts were inspected without a new local runtime or
test run. The production paragraph placement passed independent source review
and the scoped `git diff --check`. The review verified the publisher's template
copy and removal of stale compressed index variants. The later `a9037c2` run
confirmed stable canvas placement, while timely resized output remained open.
The four-checkpoint diagnostic packet passed syntax and scoped diff checks;
independent source review found no blocking issue after the managed status
was restricted to the safe field allowlist. Parsing depends on the current
engine status format and its 16,384-character input cap; missing or unknown
fields remain explicit. No runtime was started for that packet. The unrelated
TODO evidence-link edit was preserved.
