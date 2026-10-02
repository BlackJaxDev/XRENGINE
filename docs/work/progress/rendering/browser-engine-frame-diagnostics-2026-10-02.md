# Browser engine-frame diagnostics

The engine WebGPU executor scopes every accepted frame's GPU allocation, encoding,
upload and queue submission for validation and out-of-memory errors. Error scopes
are pushed and popped on one synchronous stack. Asynchronous resource preparation
also pops its scopes before its first wait, so pending preparation cannot capture
another frame's errors or mis-nest its scope stack.

Sixty-four renderer-owned receipts retain their device, owner, surface generation
and frame sequence until both error-scope results settle. Exhaustion fails the
renderer explicitly rather than submitting unscoped work or growing the pool.
A validation or scope rejection fails the still-owning renderer even without a
pending screenshot. Resize does not discard errors from already submitted work;
the diagnostic retains the original surface generation. Teardown or replacement
prevents a late receipt from failing a new owner/device.

Screenshot and luminance requests share the producing frame's receipt, with one
additional promise only when a capture is pending. Their copy/map path keeps its
own error scopes. A successful queue submit is not proof of GPU validation or
completion; an asynchronous failure can still invalidate the renderer and capture.

Command encoders, finished command buffers, acquired canvas views, render passes
and compute passes use retained debug-label descriptors. Engine mesh descriptions
carry the cooked program name into the retained command and pass labels. Labels
are created with retained resource/command plans, not concatenated during replay.

## Accounting and allocation boundaries

The JavaScript renderer's existing cold `getStatistics()` snapshot now includes
`engineFrame`. It counts entered managed-to-JavaScript frame calls, successful
queue submissions, records/draws/uploads and byte volumes, as well as calls that
create WebGPU encoders, command buffers, texture views, render and compute pass
encoders, and the one-time staging buffer. Canvas texture acquisitions are counted
separately. These are API object counts, not implementation-defined heap bytes.

Its `errorScopes` snapshot reports pending/peak receipts, capacity failures,
completed receipts, errors and promise counts. An ordinary submitted frame creates
two WebGPU `popErrorScope()` promises and two JavaScript promise-observer results.
These are required by the asynchronous WebGPU error interface and are explicitly
reported. Receipt objects, callbacks, byte arenas, descriptors and submission
arrays are reused. The optional capture gate is counted separately. Failure
exceptions and cold statistics snapshots allocate outside the successful warm
engine-owned recording path. The browser still owns its native command/pass/view
objects; this accounting does not claim a zero-allocation browser runtime.

Managed allocation measurement is opt-in through
`WebGpuRendererHost.ConfigureEngineFrameStatistics(enabled, reset)` and a detached
`CaptureEngineFrameStatistics()` snapshot. It uses actual
`GC.GetAllocatedBytesForCurrentThread()` deltas for recording/setup, submission,
cleanup and the whole synchronous rendering callback. Recording includes resource
preparation, so cold frames and real allocations remain visible. Counters include
incomplete/faulted attempts, command/mesh/upload records, uniform/storage arena
bytes and attempted interop crossings. They exclude world update/collection
outside the callback, other threads and JavaScript/GPU allocations. Uniform byte
counts include alignment padding; mesh counts do not claim to count every GPU draw.

A capture window can be reset between frames, enabled after warm-up, then disabled
without reset to preserve measured totals. Configuration and snapshot requests
reject during an active callback. Serialization is cold and source-generated.
`BrowserEngineExports.ConfigureCanvasFrameStatistics(enabled, reset)` and
`GetCanvasFrameStatisticsJson()` expose engine-session measurements. The isolated
engine fixture provides `ConfigureFrameStatistics(session, enabled, reset)` and
`GetFrameStatistics(session)` with the same per-renderer counters. Returned and
presented/unpresented submissions are distinct: an offscreen-only frame can enqueue
successfully without presenting the canvas. Recovery creates a fresh, disabled renderer accumulator.

## Validation boundary

- All changed JavaScript modules pass syntax checking; independent JavaScript
  lifetime and managed accounting reviews found no blocking defect
- The 271-assertion ignored JavaScript production-boundary probe exercises 100
  warmed submissions, receipt/descriptor/callback reuse, finite pending capacity,
  capture gating, late owner/device/teardown callbacks, original resize attribution,
  ordinary-frame validation and out-of-memory failures, partial scope push/pop
  failures, concurrent preparation, compute labels and API object/promise accounting
- A source-linked managed production-boundary probe passes 20 assertions and
  compiles with zero warnings/errors. Actual minimal render callbacks and arena
  methods measure zero calling-thread bytes across 2,048 warmed iterations with
  diagnostics enabled or disabled, including a renderer property-change subscriber
- Before the final private readiness update stopped publishing notifications,
  the subscribed callback measured 81,920 bytes over 2,048 frames (40 per frame),
  all attributed to submission. The update now uses the existing non-notifying
  field-assignment path, matching the frame-start reset
- Injected recording and submission allocations are measured as 152 and 352 bytes,
  respectively, and their 504-byte total matches the whole-callback delta. Partial,
  no-output, invalid-input, throwing submission, presented/unpresented outcomes,
  capture reset and source-generated cold JSON are exercised

The JavaScript probe uses protocol fakes, not a GPU or a JavaScript heap profiler.
The managed probe source-links the renderer and serializer but injects synchronous
interop. An unused raster-admission compatibility helper allows that isolated
probe to reference the previously built rendering assembly while related pipeline
work changes its interfaces. It is neither a final current-graph build nor real
WASM/driver execution, and its minimal callbacks do not establish zero allocations
for arbitrary worlds or pipelines.

The final canonical Release WebGPU, Editor, Server and VRClient graphs, all
nineteen portable compile rows and fresh native-Jolt browser publication passed
with zero warnings/errors, including the private readiness-notification fix.
Warmed real-world GPU qualification remains outstanding. The authorized browser
qualification lane must measure the published counters after warm-up and exercise
asynchronous failure, capture, replacement and teardown. Source probes do not
substitute for that acceptance.
