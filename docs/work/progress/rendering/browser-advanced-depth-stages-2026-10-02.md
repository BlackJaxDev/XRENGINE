# Browser Advanced depth-stage primitives

## Implemented scope

The WebGPU backend has exact cooked compute ports of the shared Advanced
`AO/Gtao.comp` and `Preparation/BuildDepthPyramid.comp` algorithms. They execute
in the existing atomic engine frame command stream, using the same program,
binding, uniform snapshot, resource generation, and retirement machinery as
other authored compute commands. No separate browser renderer or Default
pipeline substitution is involved.

- `advanced::gtao` uses the frozen canonical view, jittered inverse
  view-projection, unjittered projection scale, depth convention, and
  framebuffer Y convention. Its three slices, four samples per side,
  discontinuity-safe shortest-edge normal reconstruction, finite-value guards,
  2.2-unit radius, 0.06 bias, thickness rejection, and edge fade match Advanced.
  Disabling AO writes neutral visibility; it never leaves the target unwritten.
- `advanced::depth-pyramid` reduces each 64-by-64 depth region with 256
  invocations. Standard depth uses maximum reduction; reversed depth uses
  minimum reduction. Invalid depth and padded edge pixels become far depth,
  preserving the original conservative occlusion contract.
- The backend exposes complete AO stage enqueue through
  `IAdvancedVisibilityStageBackendCapability`. The depth primitive participates
  in the installed native family, whose conservative visibility producer places
  every frustum candidate in the early stream and has no deferred late list.
- The first adapter requires one explicit single-view, single-sample 2D depth
  and output family. Arrays and unresolved MSAA are rejected. Program preparation
  defers the entire frame rather than presenting only clears or partial effects.

## Physical resource encodings

WebGPU core storage formats differ from the desktop formats. Advanced resource
construction lowers only the WebGPU output's storage declarations:

| Logical resource | Desktop storage | Core WebGPU storage | Logical values |
|---|---|---|---|
| AO and reactive mask | R8 UNORM | R32F | Writers retain UNORM8 quantization |
| Motion | RG16F | RGBA16F | Original half-float XY; defined zero ZW |
| Visibility identity | RG32UI | RG32UI | Unchanged |
| Metadata, selection, diagnostics | R32UI | R32UI | Unchanged |

The normalized one-channel outputs increase from one to four bytes per pixel;
motion increases from four to eight. No extra logical precision is advertised.
Unsupported narrow storage formats used by optional diagnostic outputs retain
precise resource rejection. Desktop declarations and shaders are unchanged.
R32F sampling uses `textureLoad`/unfilterable-float bindings unless a separately
negotiated filterable encoding is selected.

Integer attachment support covers single-sample visibility. It does not imply
integer MSAA support or permission to replace authored sample-level visibility
with a color resolve.

## Cooked and runtime contracts

The recipes declare compute workgroup dimensions, exact resource names and
physical formats, dynamic uniform offsets, and per-stage resource limits.
The bounded WGSL verifier checks literal scalar/vector/atomic workgroup arrays
and accounts for statically used variables through the entry's call graph.
Each variable's size is rounded to 16 bytes as required by the
[WebGPU compute pipeline specification](https://www.w3.org/TR/webgpu/#dom-gpudevice-createcomputepipeline).
New workgroup declarations must publish a sufficient
`maxComputeWorkgroupStorageSize` requirement; the depth reducer requires 1024
bytes. Existing luminance descriptors keep their previously verified fixed
scratch contract.

Backend helper dispatch validates the full cooked ABI, exact texture dimensions,
single-sample depth, and R32F storage usage before binding. Canonical view data
is copied into dynamic per-dispatch uniform snapshots. No visibility, draw count,
depth, or material data is read back to the CPU.

The capability report exposes implemented integer targets, compute, storage
buffers, and ordered-pass synchronization. Canonical material indirection,
Advanced frame-slot retention, and installed native-family admission are now
integrated as described in [the admission record](browser-advanced-admission-2026-10-02.md).
The shared resolver reports the first actual missing contract. Generic
storage-image and integer attachment operations retain exact format, usage,
view, sample-count, and selected-device guards.

## Native-family admission and runtime evidence

The installed native family now connects retained canonical scene publication,
GPU visibility preparation, indexed/meshlet primitive-ID lowering, conservative
integer visibility, material classification, and native material/PBR/IBL shading.
Selected late/post features keep their individual admission checks. Live browser
execution and image evidence remain pending; a background clear, AO dispatch,
or successful shader cook is not full-family rendering evidence.

Core WebGPU needs an explicit primitive-ID path for the existing
`gl_PrimitiveID` visibility ABI. GPU vertex-pulled triangle lists can reconstruct
the authored indexed primitive identity while retaining GPU-written draw counts;
this must also preserve meshlet-local triangle identity and producer metadata.
Hardware task/mesh shader availability is not a prerequisite for that compute
lowering.

## Historical validation

- The combined targeted WebGPU Release compile included the depth dispatch adapter
- ShaderCooker Release built with zero warnings and errors; both exact scoped
  compute recipes packaged through the actual WGSL ABI verifier
- Ignored negative probes rejected storage `write` changed to `read`, `r32float`
  changed to `r32uint`, an insufficient 1023-byte workgroup limit, and a 257-float
  scratch array paired with the unchanged 1024-byte limit
- Live GPU semantic compilation, execution, and image parity had not run

These are historical results from before the October 3 workspace loss. The
earlier build/probe logs are no longer present. Reconstructed source requires a
fresh combined validation gate before publication.
