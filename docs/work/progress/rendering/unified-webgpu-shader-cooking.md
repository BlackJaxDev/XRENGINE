# Engine WGSL cooking and explicit artifact integration

**Date:** 2026-10-01. **Status:** offline depth/probe compilation and ABI validation
implemented; renderer, coordinate, desktop-preservation, and physical-device gates
remain open.

Related: [shader inventory](unified-webgpu-shader-inventory.md),
[unified runtime work](../../todo/platform/unified-desktop-browser-runtime-todo.md),
[shader cooker](../../../../Tools/ShaderCooker/README.md).

## Delivered boundary

`ShaderCooker` schema 3 admits engine-authored Slang or explicit WGSL modules with
explicit stages, canonical vertex packing, physical resource layouts, semantic
uniform providers, ownership/frequency, visibility, dynamic offsets, device limits,
and versioned semantic/coordinate identities. The fixed browser fixture generator
is not used for engine artifacts. Its schema 1/2 formats remain supported.

A cooked module is loaded by `ShaderProgramArtifactReader.Read`, which validates
the source hash/length and explicit layout. `XRRenderProgram.CookedArtifact` is a
whole-program override installed by an engine asset loader. Alternatively, every
`XRShader` stage can carry a companion with the same descriptor identity. Missing
or mismatched stage companions do not infer replacements for custom shaders.
`CookedArtifactIdentity` is a serialized descriptor SHA-256 on shaders/programs;
the module itself stays runtime-only. A session-owned immutable
`ShaderProgramArtifactCatalog` resolves those exact identities after its sidecar
bytes have been preloaded through the normal runtime asset source. An explicit
program identity that cannot be resolved fails instead of trying alternate stages.
The reader retains immutable exact `DescriptorBytes` for repackaging, and the
catalog re-verifies identity and source before publication. No global registry,
raw shader text, or filename heuristic selects replacements.

Authored source/frontend/stage/dependency edits invalidate companions and their
serialized identities; program stage/source changes invalidate explicit program
overrides. The cooked hydration hook restores source-change subscriptions while
preserving the initial serialized reference. Sidecar manifest construction,
content lifetime/generation checks, and asset-closure enumeration belong to the
publishing and runtime content owner.

The descriptor's vertex packing is canonical. A renderer may map its exact
location/format/semantic contract to equivalent engine interleaved or separate
streams. The resolved slots, offsets, strides and formats belong in the pipeline
key and must independently fit device limits. No engine mesh conversion to browser
scene DTOs is part of this contract.

Desktop GLSL assets and source-language selection remain unchanged. This does not
automatically cook every engine material, implement lit material generation, admit
any optional shader feature, or wire the complete content-publishing closure.

## First source group

The additive source `Build/CommonAssets/Shaders/WebGPU/Depth.vert.slang` provides
an opaque, non-deformed depth/single-view shadow-caster vertex entry. It applies
model then view-projection matrices. The host supplies a zero-to-one projection;
no implicit desktop-depth conversion occurs in the shader.

| Resource | Binding | Bytes | Provider | Frequency | Dynamic |
| --- | --- | --- | --- | --- | --- |
| View | group 0, binding 0 | 64 | ViewProjection, mat4 at offset 0 | View | yes |
| Object | group 0, binding 1 | 64 | ModelMatrix, mat4 at offset 0 | Object | yes |

The canonical position input is slot 0, stride 12, location 0, offset 0,
`float32x3`, semantic `position`. All dynamic offsets must satisfy the selected
device's alignment; the 64-byte physical structure does not imply a 64-byte
allocation stride.

`engine-depth` selects only `depthVertex`. `engine-depth-probe` selects the same
vertex function and `depthProbeFragment`, writing `(depth, 0, 1-depth, 1)` to a
color attachment. The probe is explicitly labeled `depth-probe` and is not a
production unlit or lit surface fallback.

Masked/deformed surfaces, depth-normal outputs, layered/cube shadow draws, reverse
Z, lit materials, sky, post processing, and UI remain outside this initial source.
Later pass groups remain gated on the first group's known-value renderer evidence.

## Compiler and validation evidence

The existing Slang **2026.8** pin was retained. The official
[Linux x86-64 release](https://github.com/shader-slang/slang/releases/tag/v2026.8)
archive SHA-256 was verified against its release-asset digest:
`b23af8f2569e7961ca143c6ecd9abf6d7ac14a9bda739a1c6281694f774fa3eb`.
The external toolchain license is `Apache-2.0 WITH LLVM-exception`; compiler
binaries are neither added to the repository nor required for desktop GLSL.

Actual compiler invocation exposed two existing integration failures: a trailing
`-o` was associated with the last entry, and the explicit `-whole-program` flag
with a single entry emitted WGSL to stdout instead of the requested file. WGSL
module output now places `-o` before entries and omits that unnecessary flag.
Real Slang emits column-major std140 matrices through a four-`vec4` wrapper;
the physical ABI verifier recognizes its exact 64-byte/16-byte-stride shape.
Linux split `bin`/`lib` layouts now include compiler libraries and standard modules
in compiler identity; library symlinks cannot escape the installation.

Completed narrow checks:

- Isolated .NET 10 ShaderCooker build: zero warnings/errors
- Both engine depth recipes compiled with the pinned Slang toolchain, passed
  emitted-WGSL ABI checks, and loaded back through the runtime artifact reader
- Schema 1 browser fixture cooked with descriptor bytes identical to the existing
  checked-in artifact
- All three explicit/generated schema 2 fixtures cooked successfully
- Existing Slang schema 2 fixture compiled and passed its fixed-layout verifier
- No test files were added or modified by this workstream

Disposable output is under
`Build/_AgentValidation/20261001-165400-engine-shaders/`; required runtime behavior
does not depend on that evidence directory. Reproduce from the source recipes
with the commands in the cooker README.

## Required live evidence

Use the explicit depth probe through engine renderer objects, then verify:

1. Known view/model transforms, including translation and nonuniform scale, place
   asymmetric geometry at the expected corners without a hidden transpose
2. Zero-to-one near/far clipping and standard depth clear/compare behave correctly
3. Overlapping nearer/farther triangles select the nearer depth regardless of
   submission order; depth 0.25 produces linear `(0.25, 0, 0.75, 1)` before any
   display transfer
4. Culling and viewport Y match the engine's selected output convention
5. Separate objects/views retain their own dynamic uniform records

A screenshot of a clear alone, offline cook, or successful module compilation is
not evidence that these rendering checks pass. The probe does not qualify reverse
Z, alpha masking, shadows, OpenGL/Vulkan preservation, or physical-device support.

## Engine texture binding qualification

The admitted depth profile passed real Chromium/SwiftShader pixels in
[run 36936819487](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36936819487).
The next diagnostic, `TextureProbe.slang`, reuses the depth transform function
and adds the canonical `uv0` stream plus explicitly paired `Texture0` texture
and sampler declarations. The cooker validates the emitted physical bindings;
it is not a generated browser-only material or production fallback.

The RGBA8/sRGB texture, immutable binding-set and deferred-retirement source
compiles, and the browser publishes with the pinned native physics build.
Published .NET WASM executes three texture cases with 39 recorded mesh draws,
three texture uploads and three sampler creations through observed imports,
and releases the recorded handles on stop. GPU imports in that local probe are
mocked: this establishes managed/interop control flow only. The later
[run 36940542409](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36940542409)
qualified the real Chromium corner pixels, sRGB decoding and replacement/resource
retirement path at `bac42c57d017f5c674092539eb5f8345fabfa755`.

## Engine lit forward and HDR output

The next source slice adds `StandardLitColor.slang` and `Tonemap.slang`, cooked
by the same pinned compiler and strict schema-3 physical ABI verifier. The explicit
material key is `StandardLitColor` revision 1, pass `opaque-forward`, vertex profile
`static-position-normal-v1`, and output profile `linear-hdr-v1`. Selection uses the
package's exact descriptor hash, without examining desktop shader names.

The lit module consumes the engine factory's authored base color, opacity,
roughness, metallic, specular intensity and emission. Direct GGX/Fresnel lighting,
point/spot attenuation and the no-probe ambient term follow the desktop forward
equations. Opaque index of refraction remains inactive, as on desktop. Object
normals use the inverse-transpose transform. The bounded profile admits four
directional, eight point and eight spot lights; excess lights fail instead of being
dropped. Shadow-enabled lights remain unsupported by this initial receiver module.

The shared `DefaultRenderPipeline` web route declares one RGBA16F scene texture,
one depth32 attachment and an attachmentless fullscreen tonemap helper in its
transactional resource generation. Both opaque material buckets render forward
into that HDR target. The whole-program tonemap companion samples it and applies
Mobius plus explicit display gamma to the browser's non-sRGB presentation target.
Tonemap selection is an explicit `pipelineArtifacts` pass-to-descriptor mapping in
the cooked package. Hydrated camera pipelines accept that immutable verified
artifact without replacement of their authored settings.

This initial profile requires mono SDR presentation, one sample, AA None and
CpuDirect submission. GI/probes, active engine UI, nonopaque render buckets,
unsupported camera effects, automatic exposure and nonneutral color grading fail
diagnostically. It does not silently turn off those authored features. Desktop
GLSL and desktop command selection are unchanged.

Actual Slang compilation, physical ABI validation, exact catalog round trips and
narrow managed builds pass. A valid flat lighting reflection is 100,226 bytes;
the bounded compiler reflection budget is now 256 KiB, with an oversized 409,006-byte
reflection rejected before manifest publication. Engine HDR/light/tonemap pixel
qualification is added to the live browser diagnostic. Its exact-commit CI result
must be recorded before claiming rendered acceptance. The published .NET WASM
diagnostic now reaches real engine lit and tonemap draw imports by frame eight,
then changes fourteen material/light cases without replacing either shader module
or render pipeline. Recorded imports show the RGBA16F/depth32 scene pass and the
non-sRGB output pass. These local GPU imports are observation shims, so this is
control-flow/ABI evidence rather than GPU execution.

That same published module also completes five viewport resizes. In a separate
retirement-pressure run, three deliberately unresolved completion promises permit
exactly three replacement HDR/depth pairs; three additional resize requests allocate
no more textures. Releasing the completions allows the latest requested 608-pixel
extent to render both passes and stop cleanly. Fourteen in-place cases preserve the
two module/pipeline identities; resizing correctly replaces output-dependent
resource generations. These checks use actual managed code with observed imports,
not a software rendering substitute.

The integration also connects the shared framebuffer bind/unbind events to the
active WebGPU owner, validates scoped full-attachment viewport/crop state against
the eventual target, rejects incomplete recorded frames, and adds real
queue-completion receipts for generation retirement. Pending queue work defers
further generation preparation at the retired-generation cap instead of blocking
the browser event thread or allocating indefinitely. Physical destruction remains
deferred through the JavaScript executor's dependency and submitted-work lifetime.
Mandatory output resources/pass identities fail explicitly; the gamma-encoding
tonemap rejects sRGB output attachments to prevent double encoding. Disabled light
shadows no longer allocate their unused camera/viewport resources during activation.

Arrays, cubes, depth
comparison sampling, storage-resource binding and complete production/gameplay
rendering remain separate work.

## Lit profile live acceptance

Commit `047bb7f1126f9fa6272325ace84446b3c9f7e1b9` passed
[run 36947665282](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36947665282)
on 2026-10-02. Actual Chromium on Google SwiftShader executed all fourteen lit
cases and five resizes with no browser console errors. The combined-light center
sample was HDR `(0.505371, 0.214697, 0.084167, 0.700195)` and presented bytes
`(221, 172, 122, 255)`. Emission remained above one in the real HDR texture:
`(3.402344, 1.683594, 0.831543, 0.700195)`. Opacity changed its HDR alpha to
`0.350098` without replacing the shader or render pipeline.

Live engine GPU resources stayed at 30 throughout every numeric case and each
384, 256, 640, 320 and 512-pixel resize; retiring resources drained to zero.
Combined-light, HDR-emission and resized PNGs were inspected. The
[qualification artifact](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36947665282/artifacts/11203082261)
contains screenshots, exact samples, adapter metadata and console records.
Depth, texture replacement/sRGB, native Jolt, engine world play/stop, asset lifetime
and offline audio checks also passed. The bare host's authored-player autostart
remains explicitly skipped. This qualifies the admitted software-WebGPU lit/HDR
cohort, not hardware performance, shadows, the full production tier or browser
RollingBall play.

## Standalone directional shadow integration

The additive `StandardLitColorV1/linear-hdr-directional-shadow-v1` receiver and
`OpaqueShadowDepthV1/static-position-v1/depth-normal-v1` caster retain the existing
unshadowed material cohort. One registered directional light renders through its
shared `ShadowRenderPipeline`, followed by the ordinary HDR scene and tonemap.
The admitted profile is one normal-Z orthographic depth map, dimensions up to
2048, PCSS eight blocker/eight filter taps, and no atlas, cascades or contact
shadows. Other requested profiles fail by name. The shader follows the desktop
projection-derived bias, rotated Vogel sampling and contact-hardening formulas;
texel footprints use the actual map dimensions. Desktop GLSL remains unchanged.

Depth-only sampled views and exact less-equal comparison samplers are declared
in the cooked ABI, validated by the managed material binder and carried through
the real JavaScript resource/command executor. A required map must have recorded
its producer in the current frame. Unready producer/receiver work defers the
entire frame. Only a disabled shadow uses the renderer-owned, explicitly cleared
depth-one binding. Replacing/deactivating a light retires only its own material
and texture pair, preserving borrowed targets.

Live published-WASM import-boundary execution records two depth-caster draws
before two HDR receivers and tonemap. Caster/light movement, near/far geometry,
disable/re-enable and map resize all reach ready without presenting any pending
partial frame. This exposed and repaired two shared first-use issues: a shadow
camera queried post-processing before binding its own pipeline, and imported
framebuffers fired bind events before realizing their current-owner wrapper.
Framebuffer bind failures now roll back their logical and physical target state.
The diagnostic selects its explicit CpuDirect policy before creating shadow
resources. Local builds, exact package/ABI probes, and two canonical RollingBall
WASM headless plus two canvas-composed lifecycle cycles pass, with the same 35
post-stop registry objects. GPU pixels, PCSS edge widths, and resource counts still
require the exact-commit Chromium run; import observations are not GPU evidence.

The first live run of `7f79535a1fd426734538577f70b7d2c327453a21`,
[run 36952723468](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36952723468),
passed all seven shadow cases and both 256/512 map resizes before the diagnostic
restart waiter failed. Actual HDR shadow/lit samples were `0.04800415` and
`0.564453125`; presented red values were 97 and 227. The near/far PCSS edge
transition widened from 9 to 21 pixels. Baseline, near, far and disabled captures
were inspected; the shadow page logged no browser/GPU error. Existing lit,
depth, texture, world/asset, audio and Jolt checks also passed. The run remains
failed: stale completion text allowed the restart waiter to query a session
before asynchronous creation finished. The repair clears readiness synchronously
and requires a new session, matching epoch and completed frame. Repeated
disable/restore checks additionally verify stable queue-drained resource counts.
The [first qualification artifact](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36952723468/artifacts/11204788286)
retains the actual pixels and failure. A subsequent complete run is still required.

The restart repair `3d18468ebdf1115b431743c56ce0f345d8c3235d` then passed
[run 36954020671](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36954020671).
All shadow pixel cases, both map extents, three additional disable/restore cycles,
and a fresh stopped/restarted session passed. The near/far edge measurements
remain 9 and 21 pixels. Queue-drained live resources returned to 42 while disabled
and 61 after restoration in every repeated toggle, with zero retiring resources;
the intentionally retained disabled-depth binding is included. The device-local
descriptor cache grew from 14 to 17 entries across those toggles within its
128-entry bound and reset with the session. This is not a managed-heap or hardware
performance measurement. The complete
[qualification artifact](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36954020671/artifacts/11205471405)
contains the pixels, counters and restart result. The authored-player check still
skips the bare host because it has no project launch descriptor.

## Shared debug primitive overlays

The registered `DebugDrawComponent` path now has exact source-free
`DebugPointV1`, `DebugLineV1` and `DebugTriangleV1` cooked variants. Their
`debug-overlay` pass uses `instanced-debug-{point,line,triangle}-v1` input and
`display-rgba-v1` output profiles. Fixed indexed quad/triangle scaffolds expand
compressed 16/28/40-byte instance records in the vertex stage; packed RGBA bits
remain integer data, and no CPU geometry expansion is substituted. Each draw
admits at most 65,536 instances. Published OnTopForward callbacks owned by the
real component populate the shared visualizer after tonemapping. Other callback
types and depth-tested debug primitives are explicitly unsupported by this
bounded overlay route. Point size, line width and alpha semantics are retained.

Engine frame packet version two carries ordered storage snapshots and explicit
instance counts through the existing single rendering submission. Uploads are
validated before GPU mutation and copied before their consuming draw. Dirty
ranges survive deferred frames; storage growth is committed only with an
accepted frame and previous buffers retire after completion. Same-capacity
numeric/count changes reuse the prepared command identity.

Fresh published WASM under Node reached all three real engine draw paths for
zero/one, 256, 384, 32, 512, 768 and 1,024-instance cohorts, repeated zero-to-visible changes,
and same-count position/color mutation. All three storage uploads preceded
their corresponding draws; same-capacity command identities remained stable.
The recording import initially omitted the device storage-binding limit and
correctly triggered the named binding rejection; correcting that supplied
capability resolved the probe. This is managed/import-boundary evidence, not
GPU pixel evidence. Exact-commit Chromium qualification remains required.

That failure also exposed an independent canvas error-isolation hole: the
shared command container's offscreen-only rejection receipt did not prevent
partial canvas submission. WebGPU now explicitly requires atomic frame
authoring, so the original command exception aborts the complete frame.
Desktop renderers retain their existing command-failure isolation policy.
The fresh fault run confirms the original storage-limit exception surfaces
before any frame reaches the submission import. Opaque HDR passes explicitly
disable blending so a preceding display overlay cannot change an opaque
material that leaves blend state unchanged. All emitted opaque pipelines have
blending disabled in the repeated live-WASM cohort.
Local no-incremental leaf builds pass without warnings/errors, and the raw
storage ABI probe accepts six original and scalar-alias cases while rejecting fifteen
float, vector, fixed-array, writable, or struct-wrapped alternatives.

### Debug overlay live acceptance

The first Chromium run of `f21abae82684ede95bef4734c23f8352f3ecdae6` rendered
the real point, line and triangle correctly, including alternate positions and
colors, then stopped on an incorrect smoke counter expectation. Frame submission
has its own counter and does not add a control call. The renderer made exactly
one warmed frame submission, with unchanged control/upload counters, command
identities and resources. That assertion was corrected, and measured line-alpha
tolerance was tightened from 60 to 12 byte values.

Exact commit `a5762484b8c763fa59f8edb6000d0b61a455bf00` passed
[run 36961007160](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36961007160).
All fifteen case samples pass: blank baseline, one primitive of each type,
same-count mutation, growth through 1,024 instances, shrink-within-capacity and
repeated zero/visible changes. The fourteen samples after initial allocation
all retain 68 live GPU resources, zero retiring resources and 24 descriptor-cache
entries. A warmed frame advances only the frame-submission counter; no control
call, separate upload submission, arena growth or command rebuild occurs.
Non-square 640×320 output, return to 512×512, and a fresh stopped/restarted
session preserve the correct pixels. Red/blue interiors are exact; the green
line's 5×5 average is `(60.8, 194.2, 34.6)` over background `(145, 110, 82)`,
consistent with its authored 0.6 alpha and edge antialiasing.

The [qualification artifact](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36961007160/artifacts/11208145755)
contains captures and counters. All prior depth, texture/sRGB, lit/HDR, PCSS,
world/asset lifecycle, offline-audio and native Jolt checks also pass. Authored
player autostart remains skipped for this bare host. This qualifies the shared
debug primitive route on software WebGPU, not screen-space text/UI, full
RollingBall gameplay, managed-heap budgets or physical-device performance.

## Shared GTAO and bloom integration

The next source slice adds exact cooked depth-normal, GTAO generation/horizontal/
vertical blur, and bloom copy/downsample/upsample/combine programs. Fifteen
shader artifacts and nine explicit pipeline mappings pass cooking and hash/ABI
checks. The shared pipeline declares generation-owned RGBA16F normal/AO/bloom
targets and sampled depth32, applies final powered/multibounce AO only to ambient
lighting, and combines authored bloom before tonemapping. Disabled AO uses a
renderer-owned white binding. Debug-bloom output bypasses tonemapping as on
desktop; tiny bloom levels preserve logical weights and alias unavailable
levels to the last physical mip.

Actual published WASM records the full nineteen-draw effect chain, exact
resource bindings, enabled/disabled generations, numeric changes without shader
replacement, authored face coverage, and 1×1/odd-sized resource generations.
Review repaired synthetic-pass ID reservation, depth-clear metadata, and
depth-normal cull/winding preservation before that run. This remains recording
boundary evidence; actual effect pixels and retirement on Chromium are pending.
