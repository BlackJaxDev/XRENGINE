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
