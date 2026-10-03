# Browser view-history submission ownership

Date: 2026-10-03

## Implemented contract

`WebGpuRendererHost` now owns bounded receipts for the shared viewport temporal
view-history ledger. Previously it inherited the generic renderer fallback,
which accepted authoring ownership and then discarded every candidate.

The host preallocates eight metadata slots. Reservation requires an active,
ready engine frame and the bound mono canvas viewport. It validates the exact
active pipeline state, candidate identity, output request, source frame, viewport
identity and extent. Each receipt freezes the pipeline resource generation
separately from the canvas output description and surface generation, together
with the renderer generation, browser session and engine frame sequence.
Caller-provided terminal framebuffers are explicitly declined by this history
profile; ordinary intermediate framebuffer rendering remains supported.

Successful pipeline authoring marks a receipt ready while retaining backend
ownership. A terminal color write is attested only after its command has been
appended to the active frame arena:

- Indexed or vertexless direct draws require positive known index/vertex and
  instance counts, a fragment stage with a canvas color attachment, a nonzero
  color write mask and a nonempty effective scissor
- Explicit canvas color clears qualify independently of raster draw state
- Intermediate framebuffer work, GPU-defined indirect counts, compute dispatch,
  mesh-draw telemetry, and attachment load/store operations do not attest a
  terminal color write

The complete-frame gate still rejects pending draws and incomplete Advanced
families. After native engine submission returns, the host commits only ready,
current, color-attested receipts when the executor reports canvas presentation.
That boolean alone is insufficient because it can be true without a nonzero
draw. Settlement precedes optional submission statistics and later managed
bookkeeping. Frame cleanup discards every remaining receipt, including failed
authoring, incomplete or abandoned frames and exceptions. Viewport rebind,
surface synchronization, renderer failure and disposal also discard receipts.

Metadata slots are immediately reusable after settlement. They do not allocate
per frame, create tasks or fences, or wait for GPU completion. The existing
completion watermark continues to own physical GPU resource retirement. The
shared ledger, OpenGL/Vulkan behavior and JavaScript submission protocol are
unchanged.

## Validation and remaining acceptance

Source inspection and `git diff --check` passed. No tests were added or modified.
Build and runtime execution are coordinated with the broader browser renderer
integration and have not yet validated this change.

Runtime acceptance remains open until the live engine canvas demonstrates:

1. At least two accepted frames with camera or object motion, advancing
   `XRViewport.FrameViewHistorySnapshot` and observing the previous accepted
   view on the next frame
2. Pending/aborted or incomplete-family authoring that does not advance committed
   history and releases its pending candidate
3. Canvas resize and viewport rebind that cannot publish a stale extent or
   pipeline generation
4. Device loss/recovery that discards old renderer receipts and resumes history
   from the replacement renderer's valid output
5. Zero-count, clipped-out, depth-only, indirect-only and load/store-only output
   that cannot advance history without a separate qualifying terminal write

A static colored frame, successful shader compilation, or ordinary mesh-draw
count is not temporal-history acceptance evidence.
