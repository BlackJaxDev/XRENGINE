# XRDataBuffer RHI Write Model Audit

Last updated: 2026-06-12

This audit classifies current `XRDataBuffer` call sites by intended memory
policy and migration owner. It is seeded from `docs/work/audit/new-allocations.md`
plus targeted `rg` sweeps for direct buffer APIs:
`ClientSideSource`, `Address`, `VoidPtr`, `SetDataRaw`, `WriteDataRaw`,
`MapBufferData`, `PushData`, `PushSubData`, `Flush`, and
`GetDataRawAtIndex`.

## Policy Classification

| Buffer family | Representative sources | Policy class | Migration owner | Status |
| --- | --- | --- | --- | --- |
| RHI wrappers and backend storage | `XRDataBuffer`, `GLDataBuffer`, `VkDataBuffer`, `VulkanStagingManager`, `VulkanDynamicUniformRingBuffer` | Backend-owned route selection | RHI | Implemented state reporting, route diagnostics, compatibility counters, and Vulkan staging reuse. Backend internals remain allowed to use raw pointers and map flags. |
| Serialized mesh and cooked asset buffers | `XRMesh.*`, `XRMesh.CookedBinary`, `GaussianSplatComponent` | Serialized CPU asset data, static upload | Mesh/runtime assets | Keep base `XRDataBuffer` declarations for serialized payload/layout metadata. New runtime-only mesh buffers should use typed facades when their element type is known. |
| Static mesh skinning/blendshape metadata | `XRMesh.Skinning`, `XRMesh.Blendshapes`, `XRMeshRenderer` | Static upload or dynamic CPU-to-GPU updates | Mesh/skinning | Writer migration is partially landed for renderer-side dirty uploads. Public properties stay base-typed for mesh/render API compatibility. |
| GPUScene mesh atlas and render submission | `GPUScene.*`, `GPURenderPassCollection.*`, `HybridRenderingManager` | Per-frame ring or staged dynamic upload | Render submission | Classified as persistent-ring candidates. Public buffer accessors remain base-typed because tests and pass descriptors assert the stable base contract. |
| UI batch/text buffers | `UIBatchCollector`, `UIText`, `UITextComponent` | Dynamic CPU-to-GPU upload | UI/rendering | Migrated to typed buffers for known `Vector4`, `float`, and `uint` payloads; direct pointer writes now commit dirty bytes through the write model. |
| Web view and texture streaming PBOs | `UIWebViewComponent`, `XRTexture2D`, `Mipmap*` | Texture/PBO interop, persistent mapped upload | UI/textures | Web view PBOs are typed byte buffers. Texture-owned PBO fields remain base-typed where the texture API exposes PBO identity. |
| Physics chain and softbody compute | `PhysicsChainComponent.GPU`, `GPUPhysicsChainDispatcher`, `GPUSoftbodyDispatcher` | Dynamic upload plus explicit GPU-to-CPU readback | Physics/compute | Upload buffers and readback pools are typed where element types are known. Normal uploads use writer/dirty commit paths. Readback buffers are marked `GpuToCpuReadback` and readback bytes are counted. |
| Particle compute | `ParticleEmitterComponent` | Dynamic CPU-to-GPU and GPU-written SSBOs | Particles/rendering | Migrated private particle, list, counter, params, and indirect argument buffers to typed variants. |
| Global skin palette packing | `GlobalSkinPaletteBuffers` | Dynamic CPU-to-GPU upload | Skinning/rendering | Migrated palette and blendshape-weight buffers to typed variants and dirty byte commits. |
| GI, AO, forward-plus, debug passes | `VPRC_SurfelGIPass`, `VPRC_ReSTIRPass`, `VPRC_SpatialHashAOPass`, `VPRC_ForwardPlusLightCullingPass`, debug renderers | Static setup, dynamic SSBO, diagnostic | Rendering features | Surfel GI migrated to typed buffers. Remaining feature-pass base declarations are classified by pass ownership and should migrate only when shader record structs are local and tests do not assert base declarations. |
| Diagnostics and tools | render stats, debug lines, RenderDoc/temp inspection, editor inspectors | Diagnostic or shared-memory | Tools/rendering | Keep explicit slow paths. Compatibility push/readback counters make remaining old paths visible. |

## Direct API Inventory

| API family | Current legitimate owners | Migration rule |
| --- | --- | --- |
| `ClientSideSource`, `Address`, `VoidPtr` | RHI wrappers, serialization/cooked asset code, low-level texture/image upload, explicit CPU mirror dedupe/copy helpers | Runtime feature code should prefer `XRDataBuffer<T>.Alloc`, `SetData`, `Write`, or dirty commits. Raw pointers remain valid only for interop and backend code. |
| `SetDataRaw`, `WriteDataRaw` | Serialized mesh loading, legacy mesh buffer initialization, physics copy helpers, low-level debug tools | New code should use typed writers. Existing direct writes must call `CommitDirtyElements` or `CommitDirtyBytes` instead of manual push calls. |
| `MapBufferData`, `Flush`, `FlushRange` | Backends, PBO/texture streaming, diagnostic inspection paths | Keep only where a real mapped resource is required. Persistent-mapped dynamic data should move toward ring slots. |
| `PushData`, `PushSubData` | Compatibility paths and backend internals | Old calls are still supported and counted. Migrated callers should commit dirty ranges so revisions, readiness, and telemetry update together. |
| `GetDataRawAtIndex` | Serialization, tests, editor/diagnostic inspection | Do not use in production GPU submission or zero-readback strategies. |

## Hot-Path Findings

- Per-frame render submission and GPUScene command buffers are the highest-value
  persistent-ring candidates. They should avoid temporary arrays and repeated
  full-buffer copies.
- Physics and softbody upload paths now use typed buffers and writer/dirty
  commits for normal CPU-to-GPU uploads. Readback remains explicit and counted.
- UI batch/text paths use typed buffers. Legacy text still writes through raw
  CPU mirrors but now commits dirty ranges through the write model.
- Serialized mesh/cooked asset paths intentionally keep CPU mirrors because
  they are asset data, editor inspection data, or source-of-truth rebuild data.

## CPU Mirror Policy

- Keep CPU mirrors for serialized asset buffers, editor diagnostics, cooked
  binary restore, and explicit CPU-side dedupe/copy helpers.
- Avoid permanent CPU mirrors for static device-local render data once the
  backend upload route owns staging and diagnostics.
- Readback-forbidden production strategies should not create CPU mirrors solely
  to inspect GPU-written results.

## Zero-Readback Classification

- `GpuIndirectZeroReadback`, meshlet zero-readback, and normal GPUScene render
  submission buffers are readback-forbidden in steady state.
- `XRDataBuffer.RequestReadback` rejects production readback unless the buffer
  policy is `GpuToCpuReadback` or `CpuGpuSharedDiagnostic`.
- Physics chain readback buffers are explicit `GpuToCpuReadback` paths, not
  zero-readback render-submission paths.
