# Browser Advanced static integration recovery

The shared Default, Advanced, and authored modular pipeline sources were
reconstructed after the October 3 workspace loss. Fresh compilation, shader
cooking, and managed contract probes now pass. The recovered static Advanced
family still needs its first live browser GPU acceptance run; these results do
not establish image parity, gameplay, device recovery, or production readiness.

## Recovered execution contract

Advanced retains the canonical shared GPU scene and completion-owned frame
slots. Its WebGPU backend executes static visibility compaction/finalization,
vertex-pulled rasterization, depth reduction, GTAO, work classification, native
opaque/background shading, and the shared late/post/output graph. Raster,
compute, indirect compute, and GPU-count-driven submission use the same ordered
engine frame. Missing programs or unsupported producers fail explicitly; there
is no CPU visibility/count readback or automatic pipeline substitution.

Native static meshlets use compute expansion and zero-first-instance indirect
rasterization within an admitted Advanced output. Their capability is distinct
from generic indexed submission, the generic Default meshlet route, and hardware
task/mesh shaders. Exact program ABIs, negotiated device limits, output
reservations, resource generations, and queue-completion ownership remain
mandatory. See [native-family admission](browser-advanced-admission-2026-10-02.md)
and [indirect submission](../../../architecture/rendering/webgpu-indirect-submission.md).

The restored Advanced output profile is mono, one view, one integer visibility
sample, and SDR RGBA8 presentation with linear HDR intermediates. Supported
output AA selections are None, FXAA, and SMAA. Selected bloom, motion blur, depth
of field, color grading, all eleven tone mappers, depth fog, vignette, chromatic
aberration, outlines, and final lens distortion use exact cooked companions.
Linear-to-sRGB transfer occurs at canvas presentation. Explicit linear
framebuffer copies retain linear values.

Native writable AO/reactive targets use physical R32F while preserving UNORM8
quantization. Motion uses RGBA16F with exact half-float XY and zero ZW. Optional
effects allocate execution resources only when selected; one common neutral
post-process input satisfies inactive sampler slots. Graph metadata omits absent
targets and invalid small-resolution bloom mips while preserving pass indices.
The unused desktop BRDF precompute is omitted because the native WebGPU shader
uses the same analytic specular integration.

Default preserves its forward output contract: mono SDR, AA None, and its
declared scene/material/effect restrictions. Default and Advanced now share the
GPU-only automatic-exposure command. Automatic exposure declares a 1x1 R32F
history output and its exact compute producer; Default selects the corresponding
tonemap consumer. Manual exposure allocates no exposure history. Shader warmup
withholds and retries the atomic frame without CPU fallback or readback.

Composable bloom, FXAA, SMAA, vendor-fallback, exposure, and presentation commands
declare their own exact raster/compute requirements. Custom graphs can adopt
these operations without a pipeline-type registry. Vendor fallback is only an
explicit no-vendor presentation operation; it never substitutes for requested
DLSS, DLAA, XeSS, or vendor frame generation.

## Authored state and remaining boundaries

The saved camera pipeline asset, pipeline parameters, post-process values,
submission strategy, and selected output profile remain authoritative. Browser
defaults fill missing values through detached admission state; they do not
rewrite authored settings or replace a saved pipeline with Default. Program
catalogs remain immutable and scope-aware across output binding and recovery.

Native Advanced deformation/current-previous vertex production and
material-displacement companions remain unimplemented. Integer per-sample MSAA,
stereo/XR, Advanced offscreen export profiles, temporal AA/TSR, exact transparency,
active atmospheric scattering/volumetric fog, and native vendor reconstruction
retain explicit unsupported boundaries. The generic Default meshlet route and
hardware task/mesh stages also remain unavailable. A supported four-sample
generic color resolve does not imply native integer-visibility MSAA support.

## Fresh validation and pending acceptance

The following evidence was regenerated after reconstruction:

- Full Editor build: zero warnings and zero errors
- Dependency-enabled Rendering and WebGPU builds: zero warnings and zero errors
- Canonical shader cook: all 75 recipes passed
- Camera/output-state probe: all 35 assertions passed
- Native admission/reservation probe: all 25 assertions passed against freshly
  cooked descriptors
- Independent source-boundary review: no remaining findings in its reviewed
  output, ABI, resource, and admission scope
- Real Editor `BrowserBuildState.Prepare`/`ExportAuthoredWorld` export: produced
  a 61,137-byte `startup-world.bin`; the saved source world's SHA-256 remained
  unchanged
- Separate fresh-process cooked hydration: preserved the exact Advanced
  pipeline identity and post-process key, Artist enum/backing state,
  `AutoExposure=false`, `Exposure=1`, CenterWeighted metering, `Bloom=false`,
  all four texture-binding/sampler identities, and six vertices
- Hydrated `RuntimeWorld.BeginPlay`: reached Playing with the correct
  pawn-camera alias

Earlier successful builds and probes are historical context only. Their
pre-reset logs were lost and are not reused as evidence for the reconstructed
source. Managed probes do not make GPU calls, and offline cooking does not prove
browser-device shader compilation or execution.

Genuine saved-world export and fresh-process hydration are verified above. The
full WASM publisher, native Jolt simulation, and first live browser pixels for
this recovered static Advanced family remain pending in CI. Live acceptance
must cover rendered output, selected effects, resize, submission/completion,
device recovery, and relevant rejection diagnostics. No staging, commit, or
push is part of this recovery record.

## First published browser attempt

Commit `2ce09a0d770922cd1d6e422e54475d9d89567969` passed genuine Windows
Editor publication of RollingBall, RenderingParity, and the saved Advanced
project in [run 37088721123](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37088721123).
The Advanced browser bundle then failed at startup admission, before rendering.
Its camera explicitly selected AA None, while the runtime profile retained the
inactive configured MSAA count of four. The Advanced post-requirement check
incorrectly treated that dormant count as active multisampling.

The correction checks the sample count only when the selected AA mode is MSAA,
matching the existing stage request and resource declaration behavior. Mono and
stereo requirements remain unconditional, and this static profile still rejects
active four-sample MSAA. Browser acceptance remains pending a corrected run.
The focused managed requirement check passes None, FXAA and SMAA with dormant
count four and still rejects active four-sample MSAA. The dependency-enabled
Rendering/WebGPU build has zero warnings and errors. This does not replace the
pending browser run.

The follow-up `4d95b150` bundle passed the inactive-AA guard in
[run 37090668361](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37090668361),
then found a lifecycle ordering error: native device admission ran while the
renderer was still Pending. The page deliberately acquires its WebGPU device
after the managed startup returns a renderer session. Cold graph/artifact
validation remains in that startup, while runtime admission is now deferred
without caching success and is performed immediately after actual device
capabilities are installed. Existing rollback and recovery handle a rejection
before any frame is accepted. Independent source review passed; the changed
browser source still requires the corrected CI compile/runtime run.

The reviewed browser effect flags also move to disjoint profile bits 58–61.
Their old positions overlapped native visibility/reconstruction flags, which
could allocate disabled post effects. This changes only derived resource-profile
keys, not authored settings or desktop shader behavior. The existing Linux CI
lane now retains its pinned Jolt archives and license/provenance files so native
builds can reuse the verified compiler output after an execution workspace reset.
