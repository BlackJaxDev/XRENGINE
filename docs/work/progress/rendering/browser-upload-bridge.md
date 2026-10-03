# Batched browser upload bridge

**Date:** 2026-09-28. **Status:** Source implemented; validation explicitly deferred.

The browser renderer now accepts a batch of updates to existing mesh buffers,
texture rectangles and material tints through `IBrowserUploadCapability`. One
generated synchronous `SubmitUploads` import carries a command arena and a
payload arena, independently of the number of commands. Resource creation and
destruction remain low-frequency controls. The existing frame packet stays at
version 2, preserving the checked-in shader layout contract.

## Storage and ownership

`BrowserUploadBatch` owns retained command and payload arrays. Its producer moves
from idle to writing to sealed; `BeginConsume` grants the importer temporary spans,
and `EndConsume` returns ownership in a `finally` block. Appending after sealing,
growing outside idle, or aborting/disposing during consumption fails. Failed
appends fault the batch; the caller must abort before rebuilding it. Frame packets
use the same ownership rule and now reject abort while borrowed and expose only
a read-only sealed inspection view.

The default upload capacities are 64 commands and 64 KiB of payload. The hard
limits are 4096 commands and 32 MiB of payload per batch. Overflow reports a
`BrowserArenaCapacityException` with the arena, required capacity, current capacity
and maximum. `EnsureCapacity` grows at idle boundaries and publishes independently
monotonic command/payload generations. A failed append never submits a partial
batch. Generation and sequence exhaustion requires a new arena/session rather
than wrapping identities. Each session owns one logical slot per lane and one
monotonic producer; multiple independent upload producers must share that batch
or coordinate before submission, not restart sequences in parallel batches.

JavaScript copies both borrowed memory views synchronously into its own retained,
bounded arrays and validates the entire used range before issuing any GPU writes.
No .NET view, pointer, or span is stored in a callback or promise. The import
returns only after source bytes have been supplied to the WebGPU queue methods;
GPU completion remains asynchronous. GPU buffers/textures are retired through
device/session-checked completion callbacks independently of arena reuse.
An unexpected write failure after execution starts terminates the renderer;
previous queue writes cannot be rolled back. Deferred failure notification avoids
destroying the managed scene while its import still owns borrowed spans.

This is deliberately a copied transient-view design. The [.NET interop lifetime
documentation](https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/?view=aspnetcore-10.0)
does not replace evidence from this application's selected runtime. Neither
persistent pinning nor zero-copy GPU upload is claimed. There is no new Python
dependency, and no Python was used for this implementation.

## Upload wire format

All fields are little-endian unsigned 32-bit values. The 64-byte header is followed
by exactly `commandCount` records of 48 bytes. The payload is a separate view;
record ranges are contiguous, four-byte aligned and cover its entire used length.

| Header offset | Meaning |
| --- | --- |
| 0 / 4 / 8 / 12 | Magic `0x55524558`, version 1, backend `0x55504757`, header size 64 |
| 16 / 20 | Total command-arena used bytes / command count |
| 24 / 28 | Session owner / device generation (same non-reused session ID) |
| 32 / 36 | Command-arena / payload-arena generation |
| 40 / 44 | Monotonic batch sequence / payload used bytes |
| 48 / 52 / 56 / 60 | Logical slot zero / flags zero / reserved zero / reserved zero |

| Record offset | Meaning |
| --- | --- |
| 0 / 4 | Opcode / record size 48 |
| 8 / 12 | Resource slot / resource generation |
| 16 / 20 / 24 / 28 | Buffer destination byte offset or texture x / texture y / width / height |
| 32 / 36 | Payload byte offset / byte length |
| 40 / 44 | Reserved zero |

| Opcode | Payload and restrictions |
| --- | --- |
| 1 | Packed position.xyz/UV.xy float32 vertices; destination and length multiples of 20; finite components; range inside the allocated vertex buffer |
| 2 | uint32 indices; destination and length multiples of 4; range inside the allocated index buffer; every value below the allocated vertex count |
| 3 | Tightly packed RGBA8 rectangle in the existing single-mip texture; exact `width * height * 4` bytes and in-bounds extent |
| 4 | Four normalized linear float32 tint channels, opaque alpha 1; exactly 16 bytes; rectangle/destination fields zero |

The parser checks lengths before field access, ABI/backend identity, owner,
monotonic sequence and arena generations, resource kind/generation/owner, reserved
fields, ranges, alignment and payload contents before execution. It carries no
WebGPU constants or GPU objects. The WebGPU module owns resource resolution,
buffer/texture writes and cached API descriptors. Frame parsing also now reports
command index, opcode and byte offset on rejection.

## Application integration and diagnostics

`BrowserSceneSession.UploadTextureRegion` resolves a live scene-owned texture and
submits through a retained batch. **Update checker texture** exercises this path
with retained demo pixels; imported scenes disable that fixture action. Lower-level
callers can put many updates into one `BrowserUploadBatch` before submission.

Updates change GPU contents, not the immutable CPU descriptors. On restart, the
original descriptors are uploaded again unless the application reapplies its
streamed state. Mesh upload users must keep CPU geometry/bounds consistent or
disable culling when appropriate; this change does not supply automatic engine
asset mutation tracking or recalculated bounds. Allocated resource extents and
counts do not change through an upload.

**Capture counters** includes supported packet versions/limits, frame and upload
submission attempts, accepted packet/command counts, copied bytes, payload bytes
queued to the GPU, arena growth, rejected packets and last command failure context.
`controlCalls` is the existing aggregate executor-call counter, including JS host
controls; `frameSubmitCalls` and `uploadSubmitCalls` specifically count submission
entry calls. They are not GPU-completion counters. Streaming `uploadBytes` is
separate from the existing creation/matrix `uploadedBytes` counter; CPU
`uploadCopiedBytes` includes command metadata and payload, including rejected
batches that were copied before validation.

The managed snapshot reports frame attempts/submissions, last and cumulative
current-thread allocated bytes, last successful packet size, capacities and growth.
The allocation window covers simulation, collection, submission and deferred
destruction; it excludes entry marshalling before `Frame`, other threads,
JavaScript/WebGPU allocations and cold diagnostic serialization. These counters
provide a measurement path, not a claim of zero allocation. WebGPU's per-frame
view/encoder/pass/command-buffer objects remain unavoidable API allocations.

Pipeline creation publishes its result only after cancellation/device checks.
Late host callbacks compare their captured host epoch; completion and retirement
compare device/session ownership. Teardown releases managed arenas and retained
JavaScript staging references after consumption has ended.

## Deferred evidence and remaining scope

No build, test, browser execution, benchmark or audit was run for this delivery.
The active TODO checks source work separately from acceptance. The following
evidence remains required before accepting the bridge:

- Exact .NET runtime memory-view lifetime/copy/disposal behavior, including managed
  and WASM memory growth; no persistent-view ABI is approved by this implementation.
- Malformed/truncated headers and records, invalid enums/ranges/lengths, stale and
  wrong-kind/owner handles, replayed sequences and obsolete arena generations.
- Overflow/abort/idle growth, queued GPU work during arena reuse, teardown during
  startup/completion, cancellation, device loss, repeated disposal and memory pressure.
- Warmed increasing-draw runs using 16/64/256 instances and split view: capture
  counter deltas, actual submitted draws, managed allocations and browser memory
  traces. Compare submission calls against submitted frames, with culling both on
  and off; report actual measured copies and startup/growth separately.
- Resource-range upload rendering and lifetime checks for every opcode, including
  multi-command batches and failure before versus after GPU execution starts.

Production shader generation, broader resource/pass lowering, arbitrary cooked
worlds, readback and physical-device qualification remain outside this source slice.
