# Browser GPU resources and ordered submission

**Date:** 2026-09-28. **Status:** Source implementation; builds, tests, shader
execution, browser rendering and device acceptance are deferred at the user's request.

The registered WebGPU module now exposes general resources, reusable ordered
commands and asynchronous readback through the portable `IBrowserRendererHost`.
The existing engine camera/mesh/material packet path remains explicitly CPU-direct.
These mechanisms do not qualify the full `BrowserWebGPUBaseline` rendering profile.

## Device and ownership

Adapter acquisition checks the cooked shader's exact feature/limit requirements
before requesting a device. Startup verifies the selected device, awaits shader
and pipeline readiness, then publishes its actual features and numeric limits.
`BrowserDeviceCapabilities` is an immutable managed snapshot. The reported
`BrowserWebGPUCore` profile describes the admitted resource mechanisms, not desktop
feature parity or hardware qualification. Snapshots are unavailable before readiness
and cleared on failure, loss or disposal. Canvas output continues to use the existing
`RenderFrameOutputDescription`, without public JS/GPU object handles.

All resources share a session-owned, generation-stamped handle table. Dependencies
hold references: views retain textures, groups retain bindings, pipelines retain
their shader/layout inputs, and prepared commands retain their resources. Releasing
a resource with live dependents is rejected. Release dependents first; destroying
the renderer releases the whole device lifetime. Buffers and textures retire after
submitted work completes. Views, samplers, layouts, groups, shader modules and
pipelines release references because WebGPU exposes no destroy method for them.
No finalizer or synchronous GPU wait participates in teardown.

## Resource API

| Capability | Implemented scope |
| --- | --- |
| Buffers | Vertex, index, uniform, storage, indirect, copy source/destination; four-byte writes/copies; device-aware uniform/storage binding ranges and dynamic alignment |
| Texture dimensions | 2D, one layer; explicit mip count; one or four samples |
| Color formats | `rgba8unorm` and `rgba8unorm-srgb`; tight RGBA8 region uploads at a selected mip |
| Depth/stencil formats | `depth16unorm`, `depth24plus`, `depth24plus-stencil8`, `depth32float`; attachment and sampling uses within the declared view/binding restrictions |
| Views | Explicit mip range and aspect; retained parent texture; no format reinterpretation |
| Samplers | Nearest/linear filtering and mip filtering; clamp/repeat/mirror address modes |

`BrowserBufferDescription`, `BrowserTextureDescription`,
`BrowserTextureViewDescription` and `BrowserSamplerDescription` are cold resource
descriptions. `WriteBuffer` and `UploadTextureMip` synchronously copy managed memory
into reusable backend staging; no borrowed span escapes an import. Queue writes
replace persistent native mapping. Buffer allocations are bounded to 256 MiB and
the device's maximum; conservative texture allocation estimates are bounded to
256 MiB. An individual upload is bounded to 64 MiB. These are safety ceilings,
not recommended mobile budgets.

Color encoding is explicit: an sRGB sampled texture performs WebGPU's sRGB
interpretation; linear textures do not. Uploads do not flip rows, convert color,
generate mips or reinterpret channels. Supply every required mip. Depth transfers,
storage textures, compressed/optional formats, arrays/cubes/3D, comparison samplers
and anisotropy are outside this initial API. Unsupported usages are rejected.
The legacy material and batched upload routes also check the extended metadata,
so a depth or multisample resource cannot enter an RGBA8-only operation.

## Pipelines, attachments and commands

The device-local `GpuPipelineCache` snapshots complete explicit descriptors,
including shader object identity, entry points/constants, binding layouts, vertex
formats/strides, raster state, depth/stencil, blend/write masks, attachment formats
and sample state. Unknown fields are rejected. A shared 128-entry bound evicts
completed entries; pending compilations cannot grow the cache past the bound.
Externally retained wrappers remain valid after cache eviction. Async results
cannot publish into an abandoned device generation. The existing mesh pipeline
also uses this cache.

`GpuPassPlan` lowers color and depth/stencil attachment policies into a WebGPU
render-pass descriptor. It checks usages, mip selection, writable subresource
aliasing, extents, formats, sample counts, resolves and pipeline compatibility.
It retains no native framebuffer object. Prepared canvas attachments reacquire
the current output for each submission and release that view after encoding.
Resize makes size-dependent plans obsolete; rebuild them against the new output.
Depth/stencil render attachments use an all-aspects view; independent read-only
and load/store policies select aspect behavior. Split aspect views are rejected
as render attachments. `BrowserFrameBufferAdapter.FromXRFrameBuffer` reads the
engine's actual target/mip/layer tuples, resolves them through caller-owned ready
views and requires explicit policy callbacks because `XRFrameBuffer` does not own
load/store policy. Unsupported multiview and separate depth/stencil targets fail.

The general command API creates shaders, binding layouts/groups and render/compute
pipelines, then prepares a bounded command description once for repeated submission.
Parsing and resource lookup preparation are cold operations. Render, compute and
buffer/texture-copy operations execute in their supplied order within a command encoder and one
queue submission. Readiness and dependency checks precede use. The existing compact
binary mesh/upload packets remain available for continuously changing scene data.
General compute primitives do not implement GPU scene culling, sorting or indirect
submission policy; those remain separate engine work.
See the [command description schema](browser-gpu-command-schema.md) for the precise
managed entry points, handle references, pipeline descriptions and prepared commands.

## Asynchronous readback

`ReadBufferAsync` and `ReadTextureAsync` return independent managed byte arrays.
`CompleteSubmittedWorkAsync` uses the same bounded completion-ticket mechanism.
Cancellation releases the caller's wait, while pending staging reservations remain
charged until the map and submitted GPU work settle. Renderer shutdown rejects
pending tickets. Session and ticket generations reject stale completion/copy calls.
No managed memory view is retained while awaiting a GPU promise.

Requests use validation/out-of-memory scopes, copy-source checks and request-owned
MAP_READ staging. Texture copies align staging rows to 256 bytes and strip padding
when publishing the result. Results preserve native encoded RGBA/BGRA channel bytes;
there is no gamma correction or channel swizzle. Depth and multisample readback are
rejected. Limits are 16 live requests, 16 MiB per result and 32 MiB aggregate
staging/output reservations. Explicit statistics expose live/retiring resources,
cache residency and readback reservations.

## Remaining work

No build, test, browser run, GPU validation, shader cook, benchmark or Python
execution was performed for this delivery. Acceptance still requires real engine
rendering, resize/resolve/depth/stencil cases, mixed command ordering, stale resource
rejection, cancellation/loss, memory pressure, allocation measurements and clean
WebGPU diagnostics. The source implementation is not evidence that those checks pass.

The focused shaded browser pipeline, automatic arbitrary desktop-world binding,
broader material features, GPU scene submission and device reconstruction remain
subsequent work. The earlier shader work also retains its documented original Slang
source-mapping and toolchain qualification gaps.

Reference: [WebGPU resource, command and mapping specification](https://www.w3.org/TR/webgpu/).
