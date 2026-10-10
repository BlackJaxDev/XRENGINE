# Browser engine resource acceptance

The engine callback now has one `SubmitEngineFrame` acceptance import per begun
attempt. It carries the existing ordered scene packet and a separate retained
preparation journal. A pending or aborted scene supplies empty scene spans and
accepts preparation only. Engine-owned physical creation requests and their result
storage share that acceptance. Request identities are separate from native
handles; the executor still uses the existing resource table and renderer.

## Ownership and ordering

Fresh and resized `XRDataBuffer` images, large backend-owned slot images, and
indirect-count parameters retain immutable bytes until acceptance. The journal
allows 4096 records and 256 MiB of exact preparation payload. Ordinary authored
buffer mutations keep the existing 8 MiB/4096-record retry preamble and an
independent 8 MiB/4096-record current-attempt budget. Initialization does not
consume that dynamic budget. Upload status distinguishes pending initialization
from an allocated physical handle.

CPU texture uploads and safe lazy array copies share the preparation journal.
The executor validates the complete preparation and scene packet before issuing
GPU writes. Texture rows use a reusable, 256-byte-pitched transfer buffer and
encoder copies. Consequently an upload, array copy, and later upload of the same
source preserve all three versions in order. The padded transfer arena is also
bounded by 256 MiB and the selected device's maximum buffer size; exceeding it
fails explicitly rather than truncating or using a different rendering path.

A source produced in the current scene uses an ordered retained texture-copy
command. Those commands do not enter a preparation-only acceptance. An array
from an abandoned producer rebuilds its generation before reuse; an earlier
uncommitted source keeps the scene pending until a new producer is recorded.
Already committed sources remain eligible. Copy-source generations remain pinned
through accepted work or exact retry, including destruction before acceptance.
Destination cancellation drops its transfers and permits source retirement.

Every handle resolves through the existing session-owned generation table.
Buffer initialization and texture transfer snapshots are cleared only after a
returned acceptance; a thrown import preserves their exact bytes. Returned
acceptance transfers scene and upload ownership before validating the receipt
metadata or notifying managed owners. Notification failures cannot replay an
accepted image. Resize, disposal and terminal device teardown retire or clear
the same owned journals and dependencies.

The acceptance result packs presentation with the executor's cached asynchronous
queue-completion watermark. Advanced and authored-indexed slot reclamation use
that cached value without hot completion-poll imports. Preparation-only attempts
advance observation while leaving rejected scene slots unsubmitted. Readback,
raw destruction and standalone submission cannot overtake pending transfers.

Engine texture wrappers use explicit queued-transfer methods. Public raw mip/layer
upload and subresource-copy APIs retain immediate standalone behavior; they do not
create a preparation journal that only an engine frame could drain. Raw buffer and
texture writes/copies reject an active engine recording and conflicting pending
transfers before import, including a destination pinned as a pending copy source.
No engine wrapper calls those raw methods. This preserves the standalone resource
contract without inserting extra imports into ordinary engine transfers.

## Physical preparation

Engine buffers, textures, views, samplers, binding layouts/groups and retained
commands carry immutable, owner-generation-scoped requests. Each request identity
names one descriptor revision and moves through queued, submitted, ready, failed
or cancelled state. A request identifier never enters a resource binding or draw
packet. The executor creates resources through the existing helpers, settles
validation and allocation error scopes asynchronously, and returns actual packed
handles or failure text through the next acceptance's borrowed result storage.
Retries retain the request identity and cannot allocate a duplicate native object.
Acknowledgment releases transport identities; normal wrappers own claimed handles.

The journal admits 4096 unresolved requests, 15 MiB of queued descriptions and
256 MiB of exact initial CPU images. CPU buffer and texture bytes can therefore be
captured before physical storage exists. Explicit content updates during this
interval keep their exact latest buffer image or ordered mip snapshots without
cancelling an unchanged allocation. A ready candidate publishes only after its
initial transfers are retained. Replacement preparation keeps the old physical
generation live; supersession, disposal and failed-device teardown cancel waiters
and snapshots, and a late native result retires once.

Physical dependencies progress in order: buffers/textures/layouts, then views and
samplers, then binding groups, then retained commands. Only ready physical handles
appear in dependent descriptors. Initial and replacement resource generations
retain their physical cursor and yield after four completed objects or two
milliseconds; pending work preserves the active pipeline generation. Completed
factory outputs and asynchronous continuations retain their exact owner until
publication or cancellation. The browser starts its frame pump without waiting
for physical resource completion, and incomplete attempts continue accepting
preparation before any complete scene is available.

Animated clear values are per-record snapshots in internal engine-frame schema 5.
RGBA and depth use otherwise unused offset words on explicitly opted-in clear
commands. Clear descriptors remain stable while colors change, so asynchronous
physical preparation cannot starve animated output. The executor validates these
values before encoding, including integer attachment ranges. This changes only
the internal managed/JavaScript packet; authored, stored, network and frozen
reference packet formats are unchanged.

## Validation

The integrated preparation and texture-transfer group passed the narrow WebGPU
project build with zero warnings or errors. The subsequent combined native Browser build also passes with zero warnings and errors. Focused linked-production-method probes cover
45 ownership and lifetime checks: immutable/padded initialization, preparation-only
acceptance, import rejection and retry, resize generations, destruction guards,
source pinning/cancellation, notification failures, invalid receipts and terminal
reset. In that isolated harness, 2048 warmed preparation/acceptance iterations
allocated zero managed bytes. A separate probe linking the production 2D and
layered texture wrappers passes 12 producer, retry and generation checks. The
existing dynamic-buffer journal probe passes 291 checks. This is not a whole-frame
allocation measurement.

The JavaScript probe exercises the production executor and generation table with
a deterministic GPU substitute. Seventeen assertions cover initialization before
dynamic mutation, preparation-only completion without canvas acquisition,
CPU-upload/copy/upload order, one-byte rows and exact array layers, current-frame
producer/copy order, rejected producers, obsolete generations, and completion
progress with no separate polling or GPU readback. The previous ordered-buffer
executor witness also passes unchanged. These are ownership/executor witnesses,
not physical-device rendering acceptance.

Luminance admission now uses the same generation-specific uncommitted-producer predicate as texture copies. A regenerated CPU-populated texture retains its monotonic lifetime ticket without being mistaken for a pending GPU write. Exact allocation and ticket capture still reject replacement or a new producer while asynchronous reduction prepares. The extended real Texture2D/LayeredTexture/Array method probe passes 16 checks, including this generation state; its renderer/import services are mocked, so it does not establish GPU luminance output.

An extended linked-method probe passes 58 assertions, including the existing
45 lifetime checks plus immediate standalone transfers, no queued side effects,
active-frame rejection, conflicts with retained images/copies and resumed raw
transfers after acceptance. The real texture-wrapper retry probe still passes
16 checks after switching its callers to the explicit queued methods. These
probes use mocked imports; they do not establish physical GPU output.

## Evidence limits

The physical-creation group passes the narrow WebGPU build with no warnings or
errors. Linked production request-ledger probes cover twelve receipt, retry,
cancellation, failure, snapshot-reset and full-capacity acknowledgment scenarios.
Five additional linked production checks establish warmed canvas clear variants,
exact managed clear records and surface replacement retirement. Twenty-five
wrapper assertions execute production buffer and texture code against a stubbed
renderer, including queued/submitted/ready content changes, exact offset writes,
cancellation and aborted GPU-array retry. Nine JavaScript transport scenarios exercise the production resource table and
helpers with an instrumented device, plus two synchronous error-scope failure
cases. Sixteen dynamic-clear scenarios exercise production command validation and
encoding, including changing values across frames and explicit rejection of stale
schema, missing opt-in, invalid offsets and invalid values. A static r32uint clear
preserves 4294967295 exactly; an opted-in float packet that rounds it outside the
integer range is rejected before submission. Native visibility uses exact zero
color and zero/one depth. These are deterministic
ownership/executor checks, not physical-device rendering evidence.

Raw/standalone creation, uploads, copies and command APIs remain synchronous.
Existing asynchronous shader and native pipeline compilation retain their separate
completion APIs. Copied snapshots do not claim zero-copy WASM marshalling.
Device-loss/recovery, repeated resize, large texture pressure and rendered
array-copy output still require live browser validation on the selected device.
No shader source, persistent asset format, dependency or tracked test change is
part of this work.

## Default material-helper admission correction — 2026-10-04

The first fresh browser run of commit `2f61fd7ea927cc09d6f8f11a76cc037f98109ace`
compiled and published the native host and cooked the diagnostic shaders, but
Default lit, effects, directional-shadow and debug-overlay frames failed during
physical generation preparation. The explicit error was
`WebGPU.FrameBuffer.OperationUnsupported: Create: the framebuffer has no declared render attachments`.
The independent depth, texture, world-lifecycle, audio, asset-delivery and native
Jolt checks passed. See [the exact CI run](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37196549762).

The registry intentionally stores `QuadMaterialSpec` fullscreen draw owners in
its framebuffer collection. Helpers such as `WebTonemapMaterial` have no render
attachments because they draw into a separately bound target. The new physical
generation cursor had incorrectly requested a framebuffer wrapper for those
material-only owners.

WebGPU preparation now resolves each retained quad's declared resource kind.
An `XRQuadFrameBuffer` declared as a `QuadMaterialSpec` prepares its retained
fullscreen renderer through `PrepareForInitialRendering`; physical framebuffer
specifications and undeclared framebuffers retain the existing generation and
empty-attachment rejection. The rule applies to custom named helpers as well as
Default. Pending preparation still returns before advancing the same retained
cursor, and shared factories, resource ownership and desktop behavior are
unchanged. No diagnostic assertion is relaxed.

The source correction passes the focused diff check, independent review and a
fresh combined Rendering/WebGPU build with zero warnings and errors. Repeated
browser execution is pending. This correction adds no completed acceptance item.

## Advanced storage allocation correction — 2026-10-04

The next [exact browser run](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37199108586)
published the Advanced player through the Editor, but its Chromium startup failed
in `WebGpuDataBuffer.Generate` with the obsolete 8 MiB storage-profile rejection.
A concrete affected resource is `Advanced.Visibility.PersistentState`:
65,536 draws × nine view slots × 32 bytes requires 18 MiB. Its factory sets
`GpuProduced = true` and allocates no CPU image. It is the first oversized buffer
in declaration order, but physical preparation snapshots an unordered concurrent
registry. The captured exception omits the buffer name, so it cannot establish
which oversized resource encountered the guard first.

Physical allocation no longer consumes or compares against the dynamic upload
budget. The existing resource path still validates four-byte alignment, checked
managed extents, the 256 MiB physical allocation ceiling and the selected device's
`maxBufferSize`; each storage binding still validates its device range limit.
GPU-produced buffers require no retained CPU transfer. CPU-backed initial images
continue through the bounded 256 MiB preparation journal, while later mutations
keep the independent 8 MiB current-attempt and retry budgets. Snapshot ownership,
queue ordering, range validation and capacity diagnostics are unchanged.

The focused source and diff checks cover this separation. The correction still
requires fresh exact-commit compilation and physical browser execution; it does
not establish a rendered Advanced frame or completed acceptance item.
