# Browser mesh and packet bridge

**Date:** 2026-09-28. **Base:** `d64eb29937e0a8af38b1838d9f93d5a65f0b2fd6`.
**State:** Source implemented; builds, tests, browser/GPU execution, profiling and
mobile validation explicitly deferred at the user's request. Earlier scene-only
browser results do not qualify this rendering path.

## Implemented behavior

The canvas host now composes a real engine scene with indexed cube and quad
geometry, shared opaque unlit materials, an sRGB checker texture and depth-tested
WebGPU draws. The existing fixed-step component simulation and input move a
parent transform. One or two view projections collect its child renderables.
The page offers 16, 64 and 256 instances; these are independent indexed draws,
not GPU instancing. Lowering the count retains created nodes and shared resources
for reuse until session shutdown.

`BrowserMeshData`, `BrowserTextureData` and `BrowserMaterialData` own immutable
upload descriptions. `BrowserMeshComponent` associates them with real engine
`SceneNode`/`Transform` lifecycle. `BrowserSceneSession.AddRenderable` accepts
additional geometry and materials and adopts its transform into this scene.
Uploads are deduplicated by descriptor object identity. These focused descriptions
are not adapters for the existing `XRMesh`/`XRMaterial` asset graph.

Startup creates a managed session, assigns its renderer owner, registers interop
routing, uploads unique graphics resources, then starts surface-driven frames.
Mesh, texture and material creation/destruction use control calls. Each rendered
frame enters managed code once and submits all draws and views through one
synchronous packet import. Ordinary draw count does not add interop calls.

## Packet wire contract

All integers and IEEE float fields are little-endian. The 64-byte header and
112-byte indexed draw records are explicitly written by `BrowserFramePacket`;
no managed structure padding or pointer layout is part of the ABI.

| Header byte offset | Meaning |
| --- | --- |
| 0 / 4 / 8 / 12 | Magic `0x50524558`, version 1, backend `0x55504757`, header size 64 |
| 16 / 20 | Total bytes, command count |
| 24 / 28 | Session owner, device generation (same session ID) |
| 32 / 36 / 40 | Surface generation, arena generation, monotonic frame sequence |
| 44 / 48 | Frame slot 0, flags 0 |
| 52 / 56 / 60 | Reserved, must be zero |

| Draw byte offset | Meaning |
| --- | --- |
| 0 / 4 | Opcode 1 (`DrawIndexed`), record size 112 |
| 8 / 12 / 16 / 20 | Mesh slot/generation, material slot/generation |
| 24 / 28 / 32 / 36 | Physical-pixel viewport/scissor x, y, width, height |
| 40 / 44 | First uint32 index, index count |
| 48–111 | Sixteen finite float32 model-view-projection matrix fields |

Packets are bounded to 4,096 draws, or 458,816 written bytes including the header.
Managed storage grows only while idle and advances arena generation. A session
supports at most 2,048 retained renderables across both lists, sufficient for two
views within the draw bound. Overflow fails rather than dropping commands.

The managed arena moves through write, seal, consume and idle states. JavaScript
copies the transient .NET `Span<byte>` memory view into retained storage, checks
the entire packet, then encodes GPU work. It rejects incompatible versions,
backend/owner/surface identity, obsolete frame/arena generations, unsupported
commands, invalid typed handles, rectangles, index ranges and nonfinite matrices
before any GPU call for that packet. A failed packet is discarded as a whole.
A device replacement requires a fresh, non-reused session ID.

This is an **explicit-copy bridge**, not zero-copy. No .NET memory view or native
heap pointer survives the synchronous import. The generated .NET marshaller may
create transient wrappers/views per call; zero managed/runtime allocation has
not been established. Packet consumption releases the CPU arena independently of
GPU completion. No ordinary frame waits for the queue to finish.

## GPU data and lifetime

- Vertices contain five float32 values: position XYZ at byte 0, UV at byte 12,
  stride 20. Triangle indices are uint32. Each vertex or index payload is bounded
  to 64 MiB and the device buffer limit; invalid indices fail before upload.
- Textures are single-mip `rgba8unorm-srgb`, bounded to 64 MiB and the device's 2D
  dimension limit. The sampler uses linear filtering and clamp-to-edge. A missing
  optional texture selects a built-in white texel; a requested invalid texture
  fails instead of selecting a replacement.
- Materials contain a 16-byte linear tint with alpha 1, a texture and sampler.
  There is one cached opaque unlit pipeline, without face culling or blending.
  `depth24plus` clears to 1, writes depth and compares `less`.
- Group 0 has a 64-byte transform at a device-aligned dynamic uniform offset;
  group 1 contains tint, texture and sampler. Reusable staging storage receives
  all transforms and makes one uniform upload per submitted frame.
- System.Numerics row-major `model * view * projection` fields are interpreted as
  WGSL matrix columns for multiplication by a column vector. Projection depth is
  0..1. Full presentation color transfer, winding and offscreen orientation still
  need known-value acceptance evidence.

Resource handles pack `(generation << 16) | slot`, with slots 1–65,535 and
generations 1–32,767 in one shared typed table. Each entry has a session owner;
that owner identifies its device generation. Removal invalidates the handle
immediately. Reusing a slot increments its generation; exhausted slots never
wrap. Materials retain textures, and referenced textures cannot be destroyed.

Materials are released before textures and meshes. Buffer/texture retirement
uses the captured device queue's asynchronous completion only when resources are
removed, resized or replaced. Terminal renderer disposal destroys remaining
resources and the device. The current canvas output is acquired anew each frame;
the depth view is cached for the surface generation. Encoder, pass, command-buffer
and acquired-output objects remain per-frame WebGPU API products.

## Diagnostics and remaining work

The page's explicit **Capture counters** action snapshots submitted packets and
draws, copied/uploaded bytes, arena growth, rejected submissions and executor
entry counts. It allocates a snapshot only on request. These are instrumentation,
not measured performance evidence; no timing, allocation or mobile budget passes
are claimed. Resource uploads and dynamic uniform uploads count toward bytes.

Remaining implementation includes:

- Existing mesh/material/camera adapters, activation-aware visibility integration,
  render-buffer publication, renderer-module registration and generic resource /
  frame-output contracts. The current collector is CPU-direct, without spatial
  culling, sorting, GPU visibility or indirect submission.
- Batched streaming uploads, partial updates, completion/readback tickets, broader
  capability negotiation and automatic resource reconstruction after device loss.
- WGSL artifact cooking, scene/asset manifests and asynchronous production content
  loading; mip chains, compressed textures and broader material/pipeline variants.
- Lighting, shadows, transparency, skinning, HDR/tonemapping, engine UI and required
  gameplay services. Browser networking and WebGL2 remain separate open work.
- Release publish, visual matrix/layout/depth/texture checks, malformed packet and
  stale-handle cases, increasing draw counts, memory growth, resize/loss/restart,
  multi-canvas isolation, desktop preservation and physical mobile qualification.

Build and usage commands remain in the [browser README](../../../../XREngine.Browser/README.md).
No validation commands or new tests were run for this implementation.

Interop ownership references: [.NET 10 runtime memory-view contract](https://github.com/dotnet/runtime/blob/v10.0.0/src/mono/browser/runtime/dotnet.d.ts),
[marshaller implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/mono/browser/runtime/marshal.ts),
[queue completion](https://developer.mozilla.org/en-US/docs/Web/API/GPUQueue/onSubmittedWorkDone).
