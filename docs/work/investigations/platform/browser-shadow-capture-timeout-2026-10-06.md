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
first blank image, the midpoint of the capture budget, and
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

## Early physical-resource receipt poll

[Run 37478900636](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37478900636)
recorded a later Advanced resize failure. At 1.835 and 5.053 seconds after
resize, all GPU receipts had completed, but the Advanced profile was not
committed. One JavaScript-ready texture/view remained retained. The first
resized native enqueue appeared after 7.475 seconds. The capture then exceeded
its 10-second deadline. These samples support a receipt-publication delay;
they do not prove it was the only delay.

Source inspection found a one-frame round trip after an asynchronous physical
request completes. Frame N submits the request. If JavaScript marks it ready
between frames, frame N+1 still sees the managed `Submitted` state during
viewport preparation. The normal acceptance imports the ready receipt only at
the end of frame N+1. Frame N+2 can then use the physical resource.

The renderer now polls only already-submitted request identities once near the
start of each frame, before viewport preparation. It skips the poll when a
frozen creation batch must be retried unchanged. The poll calls no creation,
cancellation, acknowledgement, submission, wait, or frame-sequence operation.
It reuses retained receipt storage and sends no request JSON. Managed receipt
handling validates the complete result batch before it changes request state.
The poll keeps acknowledgements until the next normal frame acceptance sends
them. It resolves the complete batch before releasing task continuations.
The existing request, description, and initial-image budgets remain in force.

This change can make a resource that completed between frames available in the
next frame's preparation.

[Run 37487901613](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37487901613)
on `f10b19414633e3ac95d0ba1f33183dd2a69da39e` accepted the initial OFF capture
and both OFF resizes within their unchanged 10-second capture budgets. The
first resize produced an 813-by-459 PNG for an 813-by-457 backing surface;
the second produced an 893-by-504 PNG for an 893-by-502 backing surface.
Both inspected PNGs show the receiver and occluders. Both passed stable
pre/post geometry, projection alignment, and exterior-background checks.
They contained 14,839 and 18,156 colorful pixels, with zero exterior mismatch
pixels and maximum projection-edge errors of 0.802 and 0.473 pixels.
Their SHA-256 values are
`49a9862da14828e2ca077a34ba5c69c765acf7d13f349f3ee35157e63fcd8198`
and `b519b908491a32d52c2515d4f0573cc66497be13a91cea69b9c8e2e67f1be59e`.

For the first resize, the 2,277 ms checkpoint still showed a profile mismatch
and one JavaScript-ready texture receipt. At 5,011 ms, the profile mismatch
was false. The renderer was preparing three draws and ten commands, with
three JavaScript-ready binding-group receipts. Production completion and
last sequence were both 1,077, with no pending production scopes at that
sample. The first resized native consumer enqueue occurred approximately
5,328.9 ms after capture start, compared with 7,475.4 ms in `4d06`.
This single CI comparison supports the intended receipt progression; it is
not a deterministic performance measurement. Successful captures retain no
exact total duration, and the second resize has no four-checkpoint series.

The explicit GPU qualification checkpoints advanced from completed serial
905 for the initial image to 1,103 and 1,299 after the two resizes. The
selected consumer remained `engine-advanced-shade-native-no-modifiers`.
Present draws advanced from 20 after the first resize to 29 after the second,
and native dispatches from 40 to 58. No device error was retained. The image
worker reported acknowledged shutdown.

The shadow check then failed its unchanged browser-failure assertion after
the OFF action, before either ON run. The browser log contains 42
`net::ERR_ABORTED` request failures and no crash; all 42 events occurred during
startup, before the first resize. The failure-only delivery snapshot was
ready with all 349 essential assets verified and zero failed reads, cancelled
reads, active requests, or queued reads. These counters do not establish the
ownership or cause of the browser cancellations. Request-failure rejection
remains unchanged, and ON/OFF shadow parity remains unqualified.
The inspected artifact is `11425618833`, ZIP SHA-256
`e5035921e4d1f95f8e7a19ef237a1ae492b6661ab45701258e2a7147fbabfa45`.

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

## Shadow-only response revalidation candidate

[Playwright issue 42742](https://github.com/microsoft/playwright/issues/42742)
reports complete page-side stream reads followed by `ERR_ABORTED` events with
the same Playwright 1.63.0 and Chromium 153.0.8010.12 versions. Its synthetic
`no-cache` control reports normal completion. The report has no application
cancellation code, but its response framing differs from this fixture. It
supports a transport-diagnostic hypothesis; it does not prove the cause of
each event in this run. Canceling an already closed reader does not invoke the
underlying source cancellation under the
[Streams cancellation algorithm](https://streams.spec.whatwg.org/#readable-stream-cancel).
No production stream cleanup change was made on that hypothesis.

The smoke server now selects `Cache-Control: no-cache` only for the
`advanced-shadow-parity` case and its `/__game/content/manifest.json`,
`/__baseline/content/manifest.json`, and corresponding
`content/payload/<64 lowercase hex>.bin` paths. Every other path and game
retains `no-store`. Body bytes, content lengths, MIME types, path containment,
and payload hash checks are unchanged. Each OFF/ON page still uses a fresh
browser context. Existing request routing remains enabled; Playwright
[documents that routing disables HTTP cache](https://playwright.dev/docs/api/class-browsercontext#browser-context-route).
The server still serves full bodies and has no conditional-response branch.

The strict request-failure assertion, browser error checks, capture deadline,
image checks, and GPU completion checks remain unchanged. This is a bounded
test-server candidate, not a change to the published player's cache policy or
a claim that aborted requests are harmless. ON/OFF shadow parity remains open.

## Midpoint observation during a pending screenshot

[Run 37495279449](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37495279449)
on `20646` retained no browser request failures, crashes, or errors. The
delivery snapshot was ready with 349 verified essential assets and zero
failed or cancelled reads. The initial OFF capture passed, but the first
resize again exceeded its unchanged 10-second capture budget.

Nineteen completed resize captures contained only the CSS background with
stable geometry. The saved PNG has SHA-256
`98a01812cd1058d44aa1868d4694fbe1c7b90554a56e27d1dc9f4ba47972b95b`.
The first resized native consumer enqueued at capture +4,660 ms, with seven
calls from submit serial 1,183 through 1,190. Screenshot attempt 20 started
at +4,697 ms and used the remaining 5,303 ms before timing out. The later
full-page failure image shows the receiver and occluders. No device error
or new shader compilation was retained. These observations do not identify
the blocked screenshot operation or establish when the resized GPU work
completed. Playwright's page screenshot also includes page preparation,
font readiness, layout metrics, and cleanup around the Chromium capture.

The first resized enqueue was earlier than the successful `f10b` run's
approximately +5,329 ms. Another resource scheduling change is therefore
not justified by this result. The existing halfway read was never reached
because it ran only between capture operations; attempt 20 was still pending
at the midpoint. Its absence cannot be interpreted as incomplete GPU work.

The same halfway slot is now scheduled by an owned Node timer at absolute
capture start plus half the existing budget. A synchronous slot claim makes
the timer and existing loop calls mutually exclusive. The selector remains
only the first OFF resize to width 860, with four slots total, unchanged
fields, a maximum 200 ms read cap, and the original absolute capture deadline.
A closed page or expired deadline starts no read. The page-side getter also
rejects late execution before managed exports. If browser evaluation blocks,
the record remains explicitly unavailable with a timeout or failure reason.

Capture success or failure clears the midpoint timer. Before copying evidence,
it seals an in-flight midpoint record as unavailable; any later read reply is
ignored. The existing read cap still bounds that in-flight observation, and
cleanup adds no wait or capture allowance. The timer callback catches read
failures without replacing the primary capture failure. This change adds no
GPU completion wait, submission, readback, renderer pause, or new checkpoint.
It does not establish that the screenshot timeout is fixed.
The inspected artifact is `11429557034`, ZIP SHA-256
`5360c614ae793be58a268cdb43e3635f315860856eaf7c3aef433580a13f264b`.

## Redundant scroll before the initial capture

[Run 37504421975](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37504421975)
on `2535ffa9` failed in the first OFF capture. The first attempt spent 4,015 ms
in `scrollIntoViewIfNeeded`, 26 ms reading canvas geometry, and 5,963 ms in
the screenshot step before the unchanged 10-second deadline. The screenshot
started with 5,959 ms left. The first resize and its four checkpoints did not
run. The later failure PNG shows the receiver and occluders. The inspected
artifact is `11432144904`, ZIP SHA-256
`8f913669db65661fca198803e19e023bdc8ead2bf97d4b7f249469eee30d7d49`.

The initial canvas was already inside the viewport. Its measured rectangle
was `(16, 138.875, 977, 549.5625)` in a `1024x1100` viewport. The complete
rounded clip was `(16, 138, 977, 551)`. The pinned
[Playwright 1.63 source](https://raw.githubusercontent.com/microsoft/playwright/v1.63.0/packages/playwright-core/src/server/dom.ts)
shows that `scrollIntoViewIfNeeded` waits for element stability before it
checks whether a scroll is needed. The saved timing does not prove which part
of that call took 4,015 ms.

The shadow harness now reads geometry before scroll. It skips the scroll only
when the complete rounded clip passes the existing finite geometry, unit
scale, zero visual offset, viewport match, positive size, and containment
checks. An invalid or outside sample still calls `scrollIntoViewIfNeeded` and
then reads geometry again. The screenshot uses the same clip check. Diagnostics
mark a skipped scroll as `skipped-visible`; they do not report it as completed.
The absolute capture deadline, before/after geometry equality, image and
receiver checks, and first-resize checkpoint lifetime stay the same.

The factored clip check accepted the saved initial and resized geometry. It
rejected synthetic outside, invalid, scale, viewport, visual-offset, and DPR
cases. This was a local source check, not a browser or shadow-parity run.

## Current-extent completion before capture

[Run 37510579239](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37510579239)
on `e273530b8472222d77af641243f09a0873a91285` passed the initial OFF
capture. Its first resize returned 31 blank images. The last of those
screenshots returned at 4,271 ms. The first observed native consumer for the
new `813x457` backing extent was submitted at approximately 4,314.5 ms,
at queue serial 992. Attempt 32 started its screenshot at 5,306 ms and
exhausted the remaining 4,694 ms of the original 10-second capture budget.

The 5,001 ms checkpoint showed a matching resource profile, no retained
physical resource requests, six draws, and 14 commands. Production had
completed frame sequence 991, with last sequence 993 and two pending scopes.
These production sequence values are separate from the observer's queue
serials. The snapshot and enqueue timing do not prove a GPU stall or the
cause of the screenshot timeout. The inspected artifact is `11437160915`,
ZIP SHA-256
`19577a7be11f426853eb7e7b9d855efcce498e23c35d2930076739979e9b88a4`.

The capture helper now requires the published programs, light state, expected
extent, and prior completed queue serial. Initial captures derive their
extent from the real canvas backing geometry. Resize captures use the extent
returned by `resize` and the previous post-capture completion serial. Before
the first screenshot, the helper finds a submitted native consumer for that
extent. It uses the exact predicate shared with final GPU qualification:
`firstSerial > afterSerial`, the allowed real dispatch operation and count,
the selected native pipeline label, and binding zero of an output texture
with matching width and height. A later `lastSerial` cannot qualify an old
consumer record.

The gate polls a compact, read-only GPU projection and the existing native
selection, GPU READ-map count, and failure state. The projection retains the
existing bounded errors, overflow and serial counters, texture IDs and
dimensions, pipeline IDs and labels, and consumer first serial, operation,
count, pipeline ID, and binding-zero output texture ID. It excludes record
keys, modules, producers, and resource bindings. The default full snapshot
remains unchanged. Missing eligible work waits for the next ordinary browser
frame. Observed failures, GPU errors, overflow, READ maps, and invalid native
selection fail the gate.

Once a candidate exists, the gate freezes its first serial and calls the
existing queue-completion helper exactly once. The returned completion serial
must cover that candidate. It then reads the native compile recipe and runs
the existing full identity assertion. Pending recipe hashing can wait for an
ordinary frame within the same deadline; rejected or unavailable recipes and
identity mismatches fail. Fresh canvas geometry after this gate must still
match the expected backing extent. A changed extent fails without a new
completion request.

Polling, completion, identity verification, and all screenshot work share the
original absolute capture deadline, capped at 10 seconds. Node and browser
entry checks reject late work. Checks after awaits prevent late continuations
from starting new reads or screenshots. Each ordinary-frame wait owns and
clears its timer and pending animation-frame callback. A submitted queue wait
cannot be cancelled. At most one gate queue promise can remain pending; if it
settles after the deadline, it can update the observer's existing completion
counters but cannot take another snapshot or continue capture.

The three gate operations have fixed named diagnostic phases. A small gate
record identifies the selected and completed serials; successful image
evidence retains that record. Polls add no history or new diagnostic cap.
The four existing resize checkpoints, 64-step limit, full post-capture
snapshot, strict GPU assertions, image alignment, reference matching,
receiver-effect checks, and request-failure assertion remain in place. This
change submits no GPU work, reads back no GPU data, pauses no renderer,
enables no statistics, and starts no trace.

The source passed `node --check` and the scoped `git diff --check`. Nineteen
saved-data and synthetic logical cases passed in task scratch. They checked
predicate equivalence, the saved serial-992 boundary, compact/full snapshot
behavior, native selection, pending and failed recipes, extent changes, one
completion request, the shared deadline, queued late callbacks, and timer and
animation-frame cleanup. A late synthetic queue settlement changed only the
existing completion counters. Independent source review found no blocking
lifetime or predicate issue. These checks ran no browser or GPU workload.
The change remains runtime-unqualified; bounded capture and ON/OFF shadow
parity still require a later authorized runtime run.

## Separate layout metrics and Chromium capture

[Run 37517179081](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37517179081)
on `1f0e1730581d54f8ca2e4b91f5cde4f39fd80a58` passed the initial OFF
completion gate. The gate selected queue serial 1,201 and observed completion
through 1,203. Its completion wait took 4,216 ms; native identity verification
took 21 ms. The screenshot started at capture +4,276 ms and exhausted the
remaining 5,724 ms of the same 10-second budget. No resize or pixel analysis
ran. This evidence does not identify the blocked screenshot operation or its
cause. The inspected artifact is `11439313901`, ZIP SHA-256
`a3e33869387f11b8dd9dfbb23c4b48ff7a92ed401925226f31ca83dfcc4a5f5c`.

The shadow helper now creates one public `CDPSession` after the completion
gate and before the first attempt geometry read. It times
`Page.getLayoutMetrics` and `Page.captureScreenshot` separately. The pinned
[Playwright 1.63 screenshotter](https://github.com/microsoft/playwright/blob/v1.63.0/packages/playwright-core/src/server/screenshotter.ts#L189-L208)
and [Chromium capture implementation](https://github.com/microsoft/playwright/blob/v1.63.0/packages/playwright-core/src/server/chromium/crPage.ts#L252-L274)
define the preparation and clip conversion used for this diagnostic.
The existing rounded viewport clip is translated by `visualViewport.pageX`
and `pageY`. Dimensions use `floor(dimension / scale + 0.001)`. The command
keeps PNG format, scale 1, `captureBeyondViewport: false`, and the other
protocol defaults. Required visual metrics must be finite, with unit scale,
zero offsets, and page coordinates that match the preceding scroll sample.
Device pixel ratio must remain 1. Visual viewport width is not equated to
`innerWidth`; a scrollbar gutter can make them differ.

Each geometry read now checks the exact expected main-document URL supplied
from the known origin and mount. It does not retain that URL in capture
evidence. The document must contain one empty `HTMLCanvasElement` with the
expected ID. Its font set must be loaded and contain no custom font faces.
Editable inputs, text areas, contenteditable targets, design mode, iframe,
frame, object, embed, fencedframe, and open shadow roots are rejected. The
fixture's checkbox is permitted. This scan does not prove the absence of
closed author shadow roots. The hash-bound controlled fixture creates none;
generic inspection of closed roots is outside this helper.
These read-only checks replace the omitted font wait and caret preparation
for the controlled player fixture. The helper changes no caret, animation,
background, color, renderer, or GPU state. The existing before/after numeric
geometry equality remains exact. These samples cannot exclude a transient
change that returns to the same state between reads.

Session creation, metrics, capture, byte validation, output, image analysis,
and acknowledged detach share the original absolute deadline. One session
serves all attempts, with at most one owned protocol operation pending.
Normal continuations check the deadline and closed state. A late metrics or
capture reply cannot start another command, decode bytes, change evidence,
or write a file. A late session creation can only request its owned detach.
Detach is requested at most once. Failure cleanup waits only for time left
in the original budget, preserves the primary error, and does not overlap
a pending command. A successful capture requires detach acknowledgement
before the deadline. Expiry leaves cleanup explicitly unverified; the
existing owned context/browser teardown retires the remaining work.

Public session creation, send, and detach cannot be forcibly cancelled.
The deadline limits acceptance and dependent work, not the lifetime of a
command already submitted to Chromium. Before base64 decode, the response
must have at most 11,184,812 characters, a valid alphabet and padding, a
multiple-of-four length, and at most 8,388,608 decoded bytes. The actual
buffer length is checked again. A synchronous file write has checks before
and after it. An OS write cannot be forcibly timed; a late return fails and
starts no later capture work. A timely raw PNG stays available if a later
step fails. The unchanged image worker still validates the PNG profile,
pixels, alignment, reference, and receiver arithmetic. Full post-capture GPU
qualification and browser-failure assertions remain in place.

Source validation used Node `v24.19.0`; CI remains pinned to Node 22.
`node --check` and the scoped `git diff --check` passed. Sixty-two scratch
logical checks passed for session ownership, late and closed continuations,
detach outcomes, primary errors, write overruns, preparation guards, viewport
conversion, base64 limits, and geometry rejection. The saved failure PNG was
byte-identical after base64 decode and output, with SHA-256
`ec359da35f4feeb9b19f2f2a94f9d27aafba4e8255e31fb6df58f2f3f749d6b4`.
This byte check did not qualify that full-page failure image as a canvas
capture. No tracked test, browser run, runtime run, trace, install, or
publication was added. Independent final source review and a later authorized
runtime run remain necessary. This is a diagnostic split, not proof of a
timeout fix, runtime parity, or performance.

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
later runtime results described above establish bounded OFF capture for the
`f10b1941` run. Source checks alone do not prove runtime qualification;
request failures and ON/OFF shadow parity remain open.

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

## Smaller software capture profile

[Run 37526282428](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37526282428)
used source `4b05` and artifact `11443424461`. The artifact ZIP SHA-256 is
`36c5dcff402f869a28863a2f13d50be836f84912543edcf7203395239cb2604a`.
The saved artifact is under `shadow-protocol-capture/runtime-4b05` in the
reserved validation run. The completion gate passed native candidate serial
1,036 through completed serial 1,038 in 3,901 ms. Native identity took 41 ms,
and session creation took 4 ms. `Page.captureScreenshot` used the remaining
6,019 ms and timed out. No decode, file write, or resize ran. Session cleanup
was not verified before the existing context teardown. This result identifies
the pending CDP command. It does not identify why that command timed out.

The shadow job now runs a separate small software profile first. It uses a
`640x780` viewport, then widths 560 and 600 at height 780. The source-derived
canvas backing extents are `593x334`, `513x289`, and `553x311`. The expected
rounded PNG sizes are `593x335`, `513x290`, and `553x312`. Runtime canvas
geometry must match each backing extent. The image worker accepts only the
PNG and backing-size tuples for its selected profile. One worker retains the
same three OFF images and current ON image, with the same byte limits. The
small worker must stop with an acknowledged exit before the full profile starts.

The original full profile still runs at `1024x1100`, then `860x780` and
`940x780`. Its backing extents, strict PNG validation, four first-OFF-resize
checkpoints, GPU assertions, and receiver thresholds stay in place. Both
profiles use the same pinned ON/OFF worlds, 256-pixel shadow resources, two
fresh ON starts, two resizes, and a fresh OFF reference. Each capture has the
same absolute 10-second limit. The report identifies each profile. Small
images have a distinct name; full images keep their original names.
The full profile stays unrun if the small profile fails; that failure also
fails the job. A passed small profile does not qualify full-size shadow parity.
The pure projection gives 4,602, 3,284, and 3,936 small-profile receiver
centers. These counts are only source calculations. Runtime images must still
pass the unchanged alignment and receiver checks.

Passing `captureUntil` to the Advanced capture helper only bounds its retry
loop. It does not prove a strict total 10-second bound. This change does not
alter that helper or claim that the screenshot timeout is fixed. Source and
scratch checks passed: Node `v24.19.0` syntax checks, scoped `git diff --check`,
and 62 scratch cases for profile isolation, geometry, PNG slots, image
references, alignment, receiver projection, and worker cleanup. The local
Playwright package resolved from `Tools/BrowserSmoke` is `1.63.0`. The scratch
cases ran against the exact shipping `shadow-image-worker.mjs` source without
a codec guard change. CI remains pinned to Node 22. These checks ran no
browser or GPU workload. Independent source review found no blocking geometry,
GPU, or lifetime issue. A later authorized runtime run remains necessary.
The later small-profile result is recorded below. Full-size shadow coverage
remains open.

## Native shading with shadows and no decals

The saved small-profile run under
`Build/_AgentValidation/00000000-000000-shared/small-shadow-profile-publication/runtime-1e5/`
passed the first shadow OFF image and two OFF resizes. The first shadow ON
start failed its 45-second deadline while the native
`engine-advanced-shade-native-depth` compute pipeline was pending for
37.2 seconds. The exact selected descriptor is
`efc4c15b7c14dc2f7e76633e7b6695e4f8507666f669ade963d665d84ec68d22`.
Its 379,453-byte WGSL has SHA-256
`084e8517586fcbf7b094e1a55686f7bed23b880840f18bcf60b3503edbc54353`.
The source cook is under the adjacent `native-cook-7e/` directory. The full
shader includes decal evaluation even when the frozen publication has no
selected decals. This makes decal code size a candidate cause of the longer
ON pipeline creation. The evidence does not establish the compiler's internal
cause or a corrected startup time.

The bounded production specialization selects eight optional native programs
with `xrengine.engine.native-no-decals.v1`. Each program retains its exact
full counterpart's ABI, shadow logic, depth comparison bank, surface exports,
and multisample mode. The new define removes only the decal evaluator. Both
original decal call points still validate the authored selection range when
the authored-decal flag is set, including an empty range with an invalid
offset. Selection requires a published package, matching nonzero global and
material sequences, zero selected authored decals, and no enabled generic
decal in the complete physical range for the view. Shadow lights and the
depth comparison bank remain eligible. The existing no-modifiers family takes
priority when its proof and companion programs are complete. A selected
family stays fixed while pipeline preparation is pending. Missing optional
programs use the full family; a present malformed program fails installation.

The saved runtime result predates this specialization. No new ON runtime
qualification or performance result is claimed here. The strict startup,
shadow parity, image, and request-failure checks still apply.

The isolated .NET 10.0.401 ShaderCooker build completed with no warnings or
errors. The final Slang 2026.8 cook packaged the 20 existing programs and
eight new variants. Its exact command, build log, cook log, manifest, and
per-variant comparison are under
`Build/_AgentValidation/00000000-000000-shared/small-shadow-profile-publication/native-cook-no-decals/`.
All 20 existing WGSL SHA-256 values match `native-cook-7e/`. Each new
variant has the same reflected layout, entry point, workgroup, required
features, and limits as its full counterpart. The generated WGSL retains
`XR_ADV_EvaluateShadow`, `XR_WEB_ShadowCoordinate`, and standalone shadow
sampling. It has no `XR_ADV_ApplyDecalToSurface`,
`XR_ADV_ApplyDecalToReceiverNormal`, or `XR_ADV_TryNextDecal` evaluator.
The depth/no-decals WGSL is 344,507 bytes; its full counterpart is
379,453 bytes. These static checks do not establish a browser startup time
or shadow image parity.

The final isolated WebGPU Release build completed with no warnings or errors.
Its log is
`Build/_AgentValidation/00000000-000000-shared/shadow-decal-specialization/native-build-final.log`.
Independent production source review found no blocking issue in the frozen
decal proof, companion selection, or shader contract. The selected family
keeps its sample and surface-export requirements during pending preparation;
a later classified frame can select a new complete family when those
requirements change. The passing build, cook, and source review do not prove
the shadow ON runtime result. That validation remains open.

The CI inventory now requires all 28 named native artifacts. The eight new
companions must match their full counterpart's layout, coordinate contract,
entry point, workgroup, dependencies, features, and limits, with the exact
additional define and semantic identity. The shadow harness recognizes the
exact depth/no-decals program and still requires real shadow production and
consumption. The existing compile diagnostic recognizes the same companions;
this change does not enable another trace or change a deadline. Node syntax,
PowerShell parsing, and checks against the cooked artifacts passed. Negative
descriptor cases rejected changed coordinates, matrix layout, pass, schema,
target, and missing shadow defines. Independent review of these three checks
found no remaining issue. Runtime acceptance remains separate.

[Run 37539269319](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37539269319)
passed the real 28-artifact Windows cook and both shadow publications. All 28
WGSL files matched the reviewed local cook. The native cook artifact
`11448771127` has ZIP SHA-256
`caf53e76b13c6b1ce83c176e122c60387c0cade85c0e28c137c6939e33cd010d`.
The shadow check then failed before world startup. Its outer pipeline identity
helper still required the single-sample `advancedShadeNative` entry point for
the newly inspected MSAA companions, which use `advancedShadeNativeMsaa`.
The helper now requires the exact entry point for the pass's sample form.
The name, single-entry, workgroup, source hash, shader schema, resource layout,
and real shadow assertions stay in place. This result gives no ON compile
time or shadow comparison result for the new specialization.
The corrected artifact and pipeline helpers passed all 28 real Windows-cooked
descriptors, with descriptor and WGSL hashes checked. All 112 mutations of
entry point, extra entry, name, and workgroup were rejected. The previous
helper rejected all 14 MSAA descriptors and accepted all 14 single-sample
descriptors, which reproduces the live failure. Independent review passed the
sample-specific correction. These checks did not start another browser.

The prepared shadow primitive now passes through seven Slang `__constref`
consumer parameters. The pinned Slang 2026.8 [parser](https://github.com/shader-slang/slang/blob/v2026.8/source/slang/slang-parser.cpp#L10218),
[borrow lowering](https://github.com/shader-slang/slang/blob/v2026.8/source/slang/slang-lower-to-ir.cpp#L3225),
and [WGSL legalization](https://github.com/shader-slang/slang/blob/v2026.8/source/slang/slang-emit.cpp#L1972)
support this source-level form. The cook packaged all 28 native programs. Its
ABI, entry points, layouts, defines, features, and limits match the saved CI
cook. All 12 packet-free WGSL files are identical.
Independent review found seven function-pointer signatures and direct
forwarding in each of the 16 packet programs, with only the existing sample
local packet variable. After reversing the pointer syntax, each emitted WGSL
file is identical to its baseline. The command, log, comparison, and hashes
are under
`Build/_AgentValidation/00000000-000000-shared/shadow-primitive-borrow/`.
[Run 37543918054](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37543918054)
predates this change and tested the no-decals selection. Native pipeline
creation was still pending for 37.34 seconds at the 45-second startup limit.
Its three small-profile OFF captures passed; the full profile did not run.
The source cook does not prove an ON runtime result or a startup-time benefit.

[Run 37546153259](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37546153259)
tested the borrowed parameters at commit
`37db74ddb1524b31f4536b0d95556919d88fa689`. Its real Windows cook produced all
28 WGSL files with the same bytes as the reviewed local cook. The depth/no-decals
program is 344,708 bytes, with WGSL SHA-256
`15c26ae39bfb45b1640e9091e381c357fd889429b84888fe61f49f05b66269df`
and descriptor SHA-256
`f81f44c6868773f0936efebaf72f03b146bc6d6c98aacf4e0f462d044eaf8a6c`.
The run finished with seven passing checks and two failures: UI frame progress
and the shadow comparison.

The small OFF initial capture and both resized captures passed. The first
small ON startup reached the unchanged 45-second deadline. At its retained
failure checkpoint, native creation of
`engine-advanced-shade-native-depth-no-decals` was still pending after about
37.30 seconds. Validation and memory error scopes had completed without an
error after 22.9 milliseconds. This did not establish a rendered ON frame.
The full-size profile did not run. The owned image-analysis worker stopped.
The shadow result artifact is `11452016895`, with ZIP SHA-256
`eef87b49461a7070e430e9064f002fd65fa59988d548d1032f926c2648a8f112`.
These results do not show an ON rendering pass or a native compilation benefit
from borrowed parameters. The next diagnosis needs evidence of work inside
native pipeline creation; the current result does not name a compiler pass.
