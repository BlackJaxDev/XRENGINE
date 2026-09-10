# Vulkan 1.4 phase G: address-based opaque shading parameters

## Scope and decision

Phase G evaluates one parameter block in `ShadeNativeOpaque.comp`. It leaves
geometry fetch, GPU-generated indirect dispatch arguments/counts, material
publication, and image/scene descriptor bindings unchanged. G1–G3 are complete
as a bounded pilot and evaluation. `Immediate` remains the default: the measured
full-frame costs do not justify promotion. The address variant remains opt-in.

Select the experimental variant before Vulkan initialization with
`XRE_VK_NATIVE_SHADING_ROOT=BufferDeviceAddress`; use `Immediate` or omit the
variable for the existing GLSL path. The pilot also requires
`XRE_VK_DESCRIPTOR_BACKEND=DescriptorIndexing` (the current default). Selection is process-stable because it
determines device features, arena buffer usage, shader source, and pipeline ABI.
Invalid values fail explicitly. This pilot requires fresh recording of an exact
presentationless Advanced output; it does not admit desktop/XR or reusable
primary command buffers. Existing indirect-secondary replay remains independent
of the fresh native shading operation. Queue selection remains independent of root selection.
Heap/set-only selection rejects at device bootstrap with the required backend.
The pre-existing Advanced scene resource runtime reports
`DescriptorHeapUnsupported`; changing the parameter root does not replace its
externally owned descriptor sets 1–3. Both immediate and address heap controls
failed before rendering, confirming that limitation is independent of this pilot.

## G1: measured target and version 1 ABI

The opaque operation records 128 GPU-generated indirect kernel dispatches plus
one repair dispatch. The original path pushes the same 64-byte structure each
time, changing only kernel index and flags. Its remaining publication cost is
8,256 pushed bytes per view. The pilot pushes 2,064 bytes and writes one 64-byte
frame allocation: a 75% reduction in push payload, with an additional dependent
shader read and allocation/write/pin work. It removes no descriptor writes.
The other five native compute programs retain their 64-byte immediate ABI.

The immutable compute pipeline carries ABI version 0 for immediate parameters
and version 1 for the address variant. CPU admission and recording check version
1; the shader preamble includes the version and therefore changes artifact/cache
identity. The CPU root uses explicit field offsets and a fixed 16-byte size.

| Root offset | Size | Field | Representation |
| --- | --- | --- | --- |
| 0 | 8 | ParameterAddress | GPU buffer address; typed GLSL buffer reference |
| 8 | 4 | KernelIndex | Unsigned kernel bucket; repair uses the existing sentinel |
| 12 | 4 | Flags | Existing unsigned shading flag bits |

The address must be nonzero and 16-byte aligned, and reference exactly one valid
64-byte allocation. It is never a host pointer or a texture/sampler address.
The referenced block has `std430` layout and a 64-byte extent. It is a single
block, not an indexed array; arena allocations may have alignment gaps and must
not be traversed using an assumed inter-allocation stride.

| Offset | Size | Fields |
| --- | --- | --- |
| 0 | 8 | Width, Height |
| 8 | 8 | TilesX, TilesY |
| 16 | 8 | ViewIndex, ViewCount |
| 24 | 8 | KernelIndex, Flags (overridden by the immediate root) |
| 32 | 8 | DepthSlices, MaxLightIndices |
| 40 | 8 | LightCount, MaxKernelTiles |
| 48 | 16 | BackgroundColor (four 32-bit floats) |

Dimensions, view indices, tile counts, light limits and repair semantics retain
the existing native operation's admitted ranges. The CPU block is the existing
packed 64-byte `VulkanAdvancedNativeShadingPushConstants` structure. No arrays,
matrices, booleans, or host-dependent integer widths enter this ABI.

SPIR-V disassembly confirms root offsets 0/8/12 and parameter offsets
0/8/16/20/24/28/32/36/40/44/48. The optimized variant uses one pointer load at
entry and seven aligned scalar/vector parameter loads; unused fields are removed.
It declares `PhysicalStorageBufferAddresses`, with no `Int64` capability.
Private invocation state prevents repeated source-level pointer loads in helpers.

## G2: ownership and submission

The pilot allocates from the existing storage lane of `VulkanFrameDataArena`
during native closure preparation, before command-buffer recording begins.
Address selection adds `ShaderDeviceAddress` buffer usage; the existing arena
backend supplies the matching device-address memory allocation flag. Vulkan
device creation explicitly requests the feature and unsupported devices fail.

The immutable closure freezes the slice, GPU address, exact native buffer
lifetime generation, and frame-slot reset epoch. Recording checks slice validity,
64-byte extent, alignment, current slot, reset epoch, resource generation and
writability before copying the parameters. The existing submission preparation
flushes dirty mapped ranges before submission. No additional GPU barrier is
needed for the existing host-write/pre-submit visibility contract.

The final shading command buffer pins the exact buffer generation after the
split executor selects that command buffer. Prefix submissions do not consume
the parameter range. The final joined receipt owns completion; frame-slot
recycling cannot reuse the range until completion. Rejection follows the existing
prefix completion and prepared-slot cancellation path. Reusable-primary admission
is explicitly excluded, so no cached command can silently keep an old address.

## Validation and measurements

Local evidence is under `Build/_AgentValidation/20260910-014509-vulkan14-g/`.
Validation uses `RenderBenchProductionScene` with `useAdvancedPipeline: true`,
normal depth, a directional light, six opaque colored meshes, a 1280x720 RGBA8
physical output and three frame slots. Each `SubmitStep(1.0 / 60.0,
backgroundCapture: true)` uses the real collect/swap/render path. The moving
scene changes one mesh every frame; the material scene adds a textured fixture
before submission and changes its `BaseColor` every 40 frames; resize changes
the logical viewport to 800x450 and restores 1280x720 while retaining the physical
target. Requested accelerated mesh submission remains
`GpuIndirectZeroReadback` with `BindlessMaterialTable`.

Controls and variants use separate processes and shader/pipeline cache directories.
Standard and synchronization validation are enabled for correctness runs. Timing
runs use `ShippingFast` with both validation layers disabled, 80 warm-up frames
and 400 measured submissions; GPU timing accepts only nonzero completed samples
with a unique source frame inside the measured receipt range. Initial/final
readbacks are outside the measured interval. Optional extra pre-measure
submissions capture a moved/material-edited or resized image and are excluded
from performance runs. Images are compared byte for byte and viewed as PNGs.

### Measurement correction

The presentationless production path did not publish a `VulkanFrameTrace`.
The raw explicit-target path did, but it is a different executor. Global
`Stats.Vulkan` last-frame counters also require a `Stats.BeginFrame` snapshot
which this harness does not drive. Consequently the first fixture's zero
recording/preparation observations are unavailable, not zero-cost work.

Production execution now publishes the existing allocation-free frame trace and
exposes `TryGetProductionFrameTelemetry(receipt, out publication)` on its host.
The read is nonblocking and accepts only matching owner, explicit frame, engine
frame, slot and target generation. It does not depend on an installed editor
statistics service. The final comparison uses this receipt-correlated CPU data.
`ResourcePrepare` covers pre-acquire slot/resource maintenance and native-binding
validation. `CommandRecord` covers native primary preparation plus recording,
including the parameter allocation/write/pin; it is not a shader-only CPU timer.
The full `SubmitStep` timer includes collection, plan construction, acquisition,
preparation, recording, submission and frame settlement. GPU time is the full
recorded frame interval, not an isolated opaque-shader or hardware-counter result.

An early ShippingFast Immediate resize run failed before authoring with a matching
800x450 active generation and no pending generation. It has no completed report
and is excluded from performance results. The failure diagnostic now also includes
Advanced family admission. This does not establish a BDA failure: the immediate
control contains no address-based shader. Short paired validation-enabled resize
runs completed successfully. Longer final reruns failed in both variants with
validation both enabled and disabled, after 80 warm-up frames at the 800x450
transition. Family admission reports `Admitted/Ready`, so the remaining issue is
in later history/package/offscreen authoring admission. It is not specific to
ShippingFast. This existing control-path failure remains open; the next work is
to propagate the exact viewport decline reason and inspect package/history
readiness at that transition. No resize performance number is accepted.

### Final correctness evidence

- Release RenderBench production build: zero warnings and errors.
- SPIR-V 1.6 validation targeting Vulkan 1.4 passes for the address shader;
  optimized layout/physical-pointer inspection matches the ABI above.
- Final static address smoke: 32 frames, zero standard/synchronization errors.
- Material edits, short resize/restore, and moving split-queue execution:
  93 frames per variant per scenario, zero standard/synchronization errors.
  Each run has the same four pre-existing loader warnings. All nine paired
  initial/intermediate/final image comparisons are byte-identical. Intermediate
  images were inspected; resized pixels differ from the initial image and the
  restored image matches it. The moving mesh and edited material change visibly.
- The moving split runs report four native submissions per frame; graphics-only
  material/resize runs report one. Three slots are repeatedly recycled.
- Invalid root selection rejects with the supported values. Address + heap
  rejects with the required `DescriptorIndexing` backend. The latter exposed and
  fixed partial-bootstrap cleanup: a selected physical device is now cleared
  even when no logical device was created, before destroying the instance.
  Repeating the rejection reports only the intended `NotSupportedException`.
- No new tests or dependencies were added. This is production-path runtime
  validation; unsupported physical hardware was not emulated.

### G3: paired costs and selection

RTX 3090, driver 610.88; ten final runs, each with 400/400 matching CPU telemetry
publications and 397 completed GPU samples. Static values below are the median
of three run medians, with pair order reversed in the middle pair. Moving and
material results are one pair each. Every successful performance pair has
byte-identical final output. Earlier runs without correlated CPU telemetry are
excluded from this table.

| Scene | Variant | Submit CPU ms | GPU frame ms | Pre-acquire resource preparation ms | Native preparation + recording ms | Managed bytes/frame |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| Static | Immediate | 6.5315 | 0.776064 | 0.0162 | 3.41815 | 321,080 |
| Static | Address | 6.3379 | 0.783968 | 0.0157 | 3.07815 | 320,696 |
| Moving | Immediate | 6.52435 | 0.776960 | 0.0309 | 3.36575 | 331,268 |
| Moving | Address | 8.64075 | 0.763680 | 0.0294 | 4.77410 | 331,268 |
| Material edits | Immediate | 6.15600 | 0.776608 | 0.0154 | 3.50290 | 323,160 |
| Material edits | Address | 7.18045 | 0.770880 | 0.0163 | 3.80545 | 323,640 |

Static run spread (max minus min, relative to median) is 14.97%/12.76% for
Immediate/address CPU, 1.03%/2.76% for GPU, and 18.27%/14.10% for native
preparation/recording. The apparent static CPU reduction is within that spread;
it is not a demonstrated speedup. Moving/material CPU costs increased in their
single pairs, while GPU changes are small. More runs would be required to
attribute those CPU changes to the parameter mechanism. BDA diagnostics also
perform two extra atomic counter updates per opaque operation, a small
measurement bias when interpreting differences near noise.

Each measured run records 51,600 opaque dispatches (`129 * 400`). Immediate
pushes 3,302,400 bytes. Address pushes 825,600 bytes, uploads 25,600 bytes, and
uses 400 parameter blocks. The root itself uses no descriptor and does not
change native scene/image descriptor publication or binding calls. The sampled
material-table counters show no publication during either measured material-edit
interval; they do not measure all Advanced-scene descriptor work and are not
reported as zero descriptor cost. The unchanged descriptor path remains included
in the total and native preparation/recording timers.

**Decision:** retain the version 1 implementation for explicit experiments;
reject default promotion. A 75% push-byte reduction does not establish lower
CPU/GPU cost. Resolve the long-resize control failure and repeat on representative
content and additional target GPUs before broader admission or promotion.
Geometry fetch/vertex pulling is a separate future experiment, not part of G.

## Primary references

- [Khronos buffer device address guide](https://docs.vulkan.org/guide/latest/buffer_device_address.html): feature, usage and allocation requirements; typed physical pointers.
- [Khronos buffer device address sample](https://docs.vulkan.org/samples/latest/samples/extensions/buffer_device_address/README.html): typed push-constant references, alignment and application-owned lifetime/bounds.

The Vulkan requirements above come from these references. The cost estimate,
chosen ABI, lifetime integration and promotion decision are engine-specific.
