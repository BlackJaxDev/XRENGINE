# Browser HLOD and asynchronous impostor capture

Updated: 2026-10-04. The implementation passes the combined managed WebGPU, native-WASM Browser and Editor builds and independent lifetime review. This records source contracts and managed/production-module probes; browser GPU pixels remain unqualified.

## Shared output and producer ownership

`BrowserEngineSession` admits mono `OffscreenCapture` through the normal purpose-aware pipeline factory. Default remains the unconfigured source; explicit Advanced intent remains Advanced. `OctahedralImposterGenerator.Settings` retains an optional authored pipeline and mesh-submission override. Admission follows that asset's declared operations, resources and cooked programs, without a concrete-type whitelist, source substitution or CPU fallback.

The browser generator uses `IAsyncSceneCaptureBackendCapability`. A request owns its viewport/pipeline instance, camera, commands, `VisualScene3D`/`GPUScene`, target, cancellation and source checks. Its render-world facade is not registered globally and inserts no capture geometry into the normal world's tree. Normal collect/world-swap/viewport-swap phases prepare canonical and mesh-submission publications for the host's planned render-frame identity. Scoped `RenderWorldSnapshotPublication` ownership prevents the private capture scene from receiving the main world's same-frame GPU-scene token.

The existing WebGPU frame records capture after normal scene work. Complete-frame preparation, rejection, resource journals and one submission crossing remain shared. Offscreen texture production commits from accepted submission independently of canvas presentation; presentation and main-view history keep their existing gates. An offscreen-only packet does not acquire a canvas texture. There is no second renderer or canvas replacement.

A layer request verifies its exact color attachment, array layer, framebuffer revision, texture-generation handle and accepted sequence. Readback retains that producer's validation/completion gate before yielding. Public synchronous browser/caller-thread `Generate` reports `AsyncCaptureRequired`; threaded desktop synchronous capture retains its normal path. A caller-thread host without the asynchronous backend capability receives an explicit unsupported-operation diagnostic.

The production witness retains the original attachment plan and its exact revision, target, mip and layer. A callback cannot retarget a framebuffer and restore it to disguise a different output. Generation-owned base-layer content tickets reject later writes to the captured layer, whole-image writes, uploads and reallocation. Writes to another layer and derived-mip generation preserve the accepted base receipt. Finalization copies the caller's receipt list and expected hashes into owned immutable state before waiting.

## Frozen lighting and source state

Before accepting any atlas layer, capture queues owned copies of the admitted directional, point and spot shadow cohort. It freezes the producer record, projection and elapsed time from the successful complete-copy attempt, not an earlier resource-preparation attempt. The whole copy set must be accepted and complete before the lease returns.

Ordered copies preserve exact single-sample `r16float`, `r32float`, `depth16unorm`, `depth24plus` and `depth32float` encodings, with explicit cube source/destination layers. Depth copies require complete mip subresources and the depth-only aspect. CPU depth uploads and combined depth/stencil copies remain excluded. These are retained GPU transfers, without CPU shadow readback or per-operation submission. Device limits remain authoritative.

Capture-only forward bindings and canonical global rows use the copied images and matching records. Main light objects and main canonical publications are not rewritten. Main shadow refresh and relevance telemetry can continue. At most three copied shadow textures and 64 MiB of copied shadow images are admitted. Initially nonrelevant spot shadows follow the source world's existing publication cohort.

The source guard checks component/world/model identity, transforms, mesh geometry/buffer revisions, renderer deformation revisions, material bindings/shaders/options, texture pushes/mip edits, light identities/authored properties and probe output versions. Changes cancel the unpublished result. Shared view uniforms and `RuntimeEngine.ElapsedTime` use the accepted lighting snapshot's frozen time. Unrelated main-world caster movement after the shadow snapshot does not invalidate its copied pixels. Opaque callbacks do not acquire a separate renderer or a synthesized fallback.

The guard also retains exact renderer, submesh, primitive and LOD collection identity, order and membership, material pairs, instance counts and render-command membership. Its unchanged path uses cold snapshots and allocates no recurring managed objects. After the capture scene retires its viewport, finalization validates the retained authored pipeline generation without consulting that destroyed viewport's lazy pipeline getter.

## Readback, persistence and finalization

`BrowserTextureReadbackDescription` carries explicit array layer, native format and optional producer sequence. RGBA16F uses eight-byte pixels. JavaScript validates source format/extent/usage/generation, copies the selected `origin.z`, strips 256-byte padded GPU rows, and returns tightly packed rows. Little-endian IEEE half components expand into linear RGBA floats without clamping, gamma conversion or row reordering.

New impostor capture opts into a producer-only origin normalization. After the authored pipeline writes the exact layer, the same frame copies it into one RGBA16F scratch layer and uses integer texel loads to reverse Y back into the final atlas layer. X, RGBA channels and scene culling/winding are unchanged. Readback therefore hashes/persists the already-normalized retained GPU pixels. The canonical billboard shaders and old authored bytes retain their existing convention. Each accepted layer is normalized once; rejected frames retry after rendering that layer again, and final mip production follows the normalized layer receipts.

Tickets retain their source until mapping and queue completion settle, including cancellation and disposal. Existing limits remain 16 tickets, 16 MiB per payload and 32 MiB resident ticket storage. The generator reads one layer at a time and serializes full-atlas jobs before allocating their owners.

At most four full capture requests may retain active or queued source ownership; only one allocates atlas storage. Cancellation releases queue admission before an unstarted factory can run.

Capture allocates final natural-mip storage up front. After all 26 readbacks, it installs the existing asset representation: one RGBA/Float `Rgba16f` CPU base mip per slice and the original automatic-mipmap flags. It does not upload these pixels again. Finalization verifies all immutable source/session/generation/layer receipts and persisted float hashes, adopts metadata on the same physical allocation, and records ordinary color downsample passes in the normal frame. Publication waits for the exact mip producer's validation/completion gate and rechecks source lifetime and CPU bytes.

Hashing occurs once per completed layer, once during cold adoption and once after mip completion. Per-frame preparation uses retained references/generations without full-atlas hashing or copying. Failed or canceled finalization destroys the unpublished atlas, never a partial layer set.

Existing prebaked arrays retain authored float bytes; runtime upload lowers them to native half data. Automatic array/cube color mips use retained GPU passes. GPU-source automatic chains lacking an admitted producer still report a named failure. The exact cooked billboard shader family and generated-geometry meshlet payload are separate requirements, not implied by an asset property.

## HLOD replacement and cleanup

Browser HLOD rebuild stages its generated proxy and asynchronous capture. It retains its last completed state, or original source rendering before its first result. `ImposterCapturePending` and `ImposterRebuildFailure` expose pending work; an ordinary proxy is never labeled a completed impostor.

Proxy capture uses private commands, not an active temporary child model. The final billboard is prepared on an inactive node. Publication requires matching rebuild generation, world/component lifetime, source renderer/geometry/buffer revisions and source membership. Rebuild, detach, destruction or relevant setting changes cancel older work. Source hooks, proxy and billboard change together on the owner. Old proxy meshes and atlas CPU/GPU data are retired explicitly; teardown restores only callbacks still owned by the group.

Creation-time owners retain the exact generated asset, array/slice objects, mip data and proxy meshes. Cleanup does not traverse the billboard's current asset or a renderer's current mesh bindings. Replacing a generated result with an authored/shared asset leaves that borrowed asset and its pixels intact. Cancellation before adoption and throwing adoption callbacks restore prior state; failed candidates release their own resources. Two billboard consumers can share an authored result without either taking ownership of its textures.

`RuntimeImposterCapturePipeline` and `RuntimeImposterCaptureSubmissionStrategy` are nullable programmatic HLOD overrides. They are hidden from persisted-property authoring UI and excluded from YAML/MemoryPack; null retains the established factory and host submission policy. Saved HLOD assets do not acquire a new persisted pipeline/strategy choice.

Generated static proxy meshes receive the bounded managed meshlet payload. Their real triangle/corner order and conservative bounds are retained, so compute-meshlet capture preserves GPU culling/count/visibility ownership. This is initial CPU geometry authoring, not readback or rendering fallback.

## Budgets and startup

At 1024 pixels, a 26-layer RGBA16F natural chain is 277.333 MiB; persisted float base data is 416 MiB. A scoped 320 MiB texture-policy allowance covers this exact 26-layer, at-most-1024, single-sample full-chain profile. The general 256 MiB limit and actual device limits are unchanged.

One request's core GPU ownership is 277.333 MiB atlas, at most 64 MiB frozen shadows, 4 MiB depth, one 8 MiB readback staging buffer and one 8 MiB origin-normalization scratch layer: 361.333 MiB before selected pipeline/scene generations. A retained prior atlas adds 277.333 MiB, for 638.666 MiB of core GPU ownership during replacement. New CPU pixels and live conversion/copy buffers are bounded by 448 MiB; a prior persisted atlas adds 416 MiB, for 864 MiB during replacement. Queued requests retain source/proxy geometry but allocate no atlas. The no-reupload path avoids another 208 MiB upload snapshot and three 256 MiB transfer arenas for generated atlases. These are substantial desktop-class allocations, not mobile-memory qualification.

BeginPlay queues capture without awaiting a rendered result. Production JavaScript awaits `StartCanvasAsync`, independently requests adapter/device, calls `InitializeCanvasGraphics`, then schedules frames. Pending-device `Step` preserves queued work; adapter/device acquisition does not depend on that dispatch. Ready `Step` enters an allocation-free scope on the same renderer across jobs/collect/swap/render. Capture retains that exact owner/session across awaits. Cancellation before the first frame releases job/semaphore ownership without another renderer.

Startup retains its configured `JobManager` before waiting for atlas admission and observes the queued job's terminal handle. Scheduler shutdown settles the capture even if its startup action never runs. An admitted waiter cannot migrate onto a replacement scheduler, and cancellation releases both the atlas semaphore and request-admission count.

## Validation boundary

Evidence remains under `Build/_AgentValidation/20261001-225000-lit-surface/`:

- `layer-readback/`: production JavaScript row/layer/format/generation/budget tests; producer-gate failures; cancellation/disposal retention; completion with no extra submission; depth/cube copy validation; actual managed mip descriptors through production resource/binding/command validators
- Compiled production probes: IEEE half conversion; unchanged float bytes/metadata; aborted-frame mip replay; exact finalization receipt/content rejections; same-allocation adoption without uploads; frozen-light queue cancellation and unwind ownership; four-request admission/cancellation; exact generated-resource ownership, shared authored consumers and throwing HLOD adoption callbacks
- `scratch/scene-capture-probe/startup-result.json`: actual production canvas host and renderer initialization with controlled adapter/device promises and mocked managed exports; device initialization precedes ready dispatch and canceled acquisition creates no second renderer
- The final frozen source passes Rendering/WebGPU, Browser Release with the in-process WASM/native-Jolt link, and Editor Release builds with zero warnings/errors. The initial isolated Editor attempt omitted two pinned submodule inputs; restoring those exact existing OSC/OpenVR revisions produced the clean Editor result without changing engine source or dependency versions
- Independent compiled production witnesses admit four captures before pumping, retire their original scheduler, install its replacement and verify canceled tasks, no old factory execution, recovered semaphore and admission count. Actual capture-scene retirement preserves finalization validity while a later authored pipeline generation change still rejects publication. Material replacement, appended primitives and changed instance counts invalidate the source witness while preserving borrowed objects
- The final retained-source witness reports zero allocations across 1,000 unchanged checks. This is bounded source-validation evidence, not a whole-frame allocation or mobile-memory result

Controlled browser/physical evidence still needs Default, Advanced and an unrelated authored pipeline; CPU-direct, GPU-indirect and compute-meshlet modes; all 26 orientations/row conventions; frozen directional/point/spot shadows; cancellation/rebuild/session races; persisted reload; and final billboard pixels. No local browser, external publication or tracked test was added here.

`UIVideoComponent` remains separate: browser video GPU actions reject before the synchronous stream resolver, and FFmpeg is not registered. This work does not extend that media path.
