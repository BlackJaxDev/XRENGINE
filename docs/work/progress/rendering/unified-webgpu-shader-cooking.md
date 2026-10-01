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
