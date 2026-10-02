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
