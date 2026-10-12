# Engine WebGPU renderbuffers and color MSAA resolve

## Implemented contract

`WebGpuRenderBuffer` owns the physical attachment storage for `XRRenderBuffer`
inside one renderer generation. It uses the existing WebGPU texture/view resource
registry with render-attachment usage only; renderbuffers do not become sampled
textures or acquire transfer usages implicitly.

- Positive dimensions and exactly one or four samples are admitted
- Exact storage encodings are RGBA8, sRGB RGBA8, RGBA16F, R16F, depth16,
  depth24plus, depth32float and depth24plus-stencil8
- Unsized, RGB-only and other unsupported storage formats fail by name; there is
  no single-sample or format-conversion fallback
- A renderbuffer exposes mip zero and one non-layered render view
- Descriptor changes replace texture and view together; a recorded-frame
  mutation is rejected, and dependency retirement precedes physical retirement
- Shared logical objects retain output-local wrappers, allocation callbacks and
  generation checks

`WebGpuFrameBuffer` admits renderbuffers alongside its existing exact texture
views. Color/depth clear flags now track the aspects actually written, while
unchanged loaded attachments still retain their recorded-use protection. A
depth-only clear therefore cannot claim that untouched color was produced.

The renderer's `Blit` and `BlitWithDrawBuffer` entry points accept only a complete,
same-format, same-extent, four-sample to one-sample color resolve between engine
framebuffers. The explicit overload selects its destination attachment; the
implicit overload requires exactly one enabled destination color draw buffer.
Both selected views must be distinct, and the source must have recorded current
production or committed earlier production.

Canvas, depth/stencil, single-sample copies, scaled or offset rectangles,
format conversion, filtering and partial-scissor blits remain named unsupported
operations. Explicit rectangles must cover the actual attachment subresource.
The `TryBlit` convenience entry points derive their full extents from the backend
attachment views, including a destination mip whose size differs from its
backing texture's base dimensions. Accepted work reports `Enqueued`, never
immediate completion.

## Resolve ordering and lifetime

Each resolve uses a retained empty render pass with source `load`, source
`store`, and the destination as `resolveTarget`. This uses the existing generic
pass executor; no resolve policy, shader selection or scene-specific behavior
was added to JavaScript.

The WebGPU specification defines attachment loading before pass commands and
multisample resolution at pass end before the source store operation. Neither
requires a draw command. Both views must be renderable, the source multisampled,
the destination single-sampled, and their exact formats and selected extents
must match. The admitted color formats support resolve in the core format
table. See [render passes](https://www.w3.org/TR/webgpu/#render-passes),
[color attachments](https://www.w3.org/TR/webgpu/#dictdef-gpurenderpasscolorattachment)
and the [normative source](https://github.com/gpuweb/gpuweb/blob/main/spec/index.bs).

Each source framebuffer retains at most 32 resolve routes. Descriptions and JSON
are created only for a new route or resource generation. Warm execution appends
the prepared handle to the existing bounded engine command arena. Source and
destination storage, framebuffer replacement and renderer teardown invalidate
dependent routes. Already recorded handles survive through submission using the
existing deferred retirement path.

The source records a read dependency; only the selected destination records new
production. Existing production tickets and the whole-frame readiness gate
prevent abandoned asynchronous-preparation frames from committing a partial
result. A producer still pending preparation does not authorize an unexecuted
resolve destination.

## Validation and remaining acceptance

- Canonical Release build of `XREngine.Runtime.Rendering.WebGPU`: zero warnings
  and errors, including the portable project guards
- Independent GPU/lifetime source review: no remaining blocker; the selected-mip
  convenience-call mismatch identified in review was corrected
- Ignored managed production-boundary probe: 38 assertions pass, covering
  deferred arena recording, exact command identity, unsupported requests,
  untouched-color rejection, pending producer, committed and abandoned
  production tickets, selected destination mip extents, recorded-frame mutation,
  and route retirement on framebuffer and texture destruction
- The managed probe injects accepted resource and prepared-command handles; it
  does not validate native allocation or driver execution. Its 100 warm resolve
  calls allocate zero managed bytes on the measured calling thread
- A real-GPU probe is prepared for the existing production resource, pass and
  engine-arena executor. It covers RGBA8, sRGB RGBA8, RGBA16F and R16F partial
  coverage, two destinations, destination mip one, source preservation and warm
  replay. Local Chromium was blocked before launch by the cloud Unix-socket
  restriction; there is no pixel or GPU-validation result from that attempt

Live GPU qualification must use the authorized browser milestone route. It must
still establish native allocation, rendered edge pixels, repeat resolves,
resource replacement, pending first-use pipelines, teardown/restart and stable
resource counts in the real engine frame path.

This backend work does not enable `DefaultRenderPipeline` MSAA admission. Its
complete depth/preload/resolve/post-processing chain and authored effect
compatibility remain separate requirements. Desktop behavior and supported AA
selection are unchanged.
