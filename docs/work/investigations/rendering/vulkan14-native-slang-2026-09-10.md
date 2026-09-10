# Vulkan 1.4 phase H: additive native Slang frontend

## Scope and implementation

Phase H adds native Slang as an opt-in Vulkan frontend. GLSL remains the default.
The older [Slang plan](../../design/scripting/slang-shader-cross-compile-plan.md)
now describes coexistence, without mandatory translation or retirement.

| Authored source | Vulkan | OpenGL |
| --- | --- | --- |
| GLSL | Existing preparation, Shaderc, SPIR-V | Existing GLSL driver compiler |
| Slang | Optional pinned Slang 2026.8, direct SPIR-V 1.6 | Explicit rejection; use the authored GLSL counterpart |
| HLSL tooling input | Existing Shaderc cross-compiler API | No native asset route introduced |

No package, submodule, or SDK upgrade was performed. This machine already has
`K:\VulkanSDK\1.4.350.0\Bin\slangc.exe`, reporting `2026.8`. The compiler is an
optional, externally provisioned tool, not an engine redistribution dependency.
Resolution checks `XRE_SLANGC`, then `VULKAN_SDK/Bin`, then `PATH`. An explicit
missing compiler or a different version fails visibly. GLSL does not resolve or
launch Slang.

`XRShader.SourceLanguage`, `EntryPoint`, and immutable `SlangOptions` select the
frontend. Imported Slang assets use stage suffixes such as `.comp.slang` and
`.frag.slang`; embedded assets set the stage explicitly. Language, entry point,
source, and semantic-policy changes invalidate `SourceRevision`. Vulkan branches
before GLSL preprocessing, regex reflection, automatic uniform rewriting, and
the existing GLSL artifact cache. Transform-feedback rewriting is unsupported;
native position shaders must use Vulkan clip depth and disable GLSL remapping.

The public request/result contract includes target, stage, entry point, ordered
include roots, definitions, capabilities, matrix policy, semantic identity,
SPIR-V, reflection JSON, dependency hashes, compiler identity, diagnostics, and
compile/cache timing. Each cold Slang job owns an external compiler process.
The two-process gate also covers version probes; jobs drain redirected output,
enforce a 45-second deadline, and kill their own process on cancellation.

## Binding and physical ABI

Explicit resource contracts join reflected set/binding/type and physical layout
to owner, frequency, and provider names. Engine/material uniform blocks feed the
existing typed uniform publication path; externally owned resources retain
explicit descriptors. Unsupported or ambiguous mappings fail before module use.

Slang JSON reflection is checked against SPIR-V decorations, including offsets,
array strides, matrix strides/order, structured-buffer stride, and physical
storage-buffer pointers. Combined texture samplers are initially limited to
single non-array 2D resources. Descriptor arrays and unsupported native layouts
are rejected. `ShaderAbiMarshalVerifier` checks blittability, managed size,
member offsets/sizes, and the UInt64 representation of GPU-address fields.

The ABI probe contains a float4, float4x4, two-element float4 array, an 8-byte typed
GPU pointer, a structured output buffer, and a combined texture sampler. Its
constant-buffer extent is 128 bytes with member offsets 0, 16, 80, and 112.
The emitted pointer is `PhysicalStorageBuffer<float4>`, not an assumed integer
field. The probe also requires explicit `Globals` descriptor lifetime metadata
for its set-0 resources; strict negative cases cover extent, matrix order, and
ambiguous ABI mappings.
Slang's matrix-major terminology is inverted relative to SPIR-V decorations:
the probe's Slang `column_major` emits SPIR-V `RowMajor`, stride 16. Validation
therefore uses emitted decorations as authority. Slang can also wrap std140
arrays in a structure; the physical reader handles that representation.

## Cache and reload

Slang artifacts have a separate namespace under the existing Vulkan shader-cache
root. Identity includes source/language/path, stage/entry/target, ordered search
roots/defines/capabilities, matrix/semantic policy, and compiler/library/standard
module hashes. SPIR-V bytes are hash-linked to metadata; temporary writes commit
metadata last. Temporary source files never become persistent dependencies.

The bounded pilot snapshots all candidate `.slang`, `.slangh`, `.slang-module`,
`.h`, `.hlsl`, and `.glslinc` files under the original source directory and
declared search roots. Candidate paths and content detect include shadowing;
pre/post snapshots reject a graph changed during compilation. Dependencies
outside that snapshot fail explicitly. Use narrow dedicated Slang search roots.
This conservative policy can invalidate more artifacts than strictly necessary.
Native search directories are registered with the engine dependency index so
new shadowing modules also trigger asset reload. Diagnostics retain original
authored paths and source spans, including Slang 2026.8's arrow-style messages.

## Pilots and validation in progress

`XRE_SLANG_PILOTS=1` opts into exactly two native counterparts when engine shaders
are loaded. It does not select the descriptor backend or change other shaders:

| Pilot | Native asset | Retained authored GLSL asset |
| --- | --- | --- |
| Three-counter copy | `FrontendPilots/CopyCount3.comp.slang`, `copyCounts` | `Compute/Indirect/GPURenderCopyCount3.comp` |
| Scene-copy material pass | `FrontendPilots/SceneCopy.frag.slang`, `copyScene` | `Scene3D/SceneCopy.fs` |

The switch is a Vulkan-only pilot selection. Leave it unset for OpenGL. Stereo
scene-copy remains on its existing authored GLSL route. No production default
or existing asset was replaced.

Validation evidence is under
`Build/_AgentValidation/20260910-060112-vulkan14-h/` and is disposable. Durable
results will be recorded here after runtime checks complete.

- Isolated Release Vulkan and RenderBench builds: zero warnings/errors.
- Public frontend harness: cold/warm artifact reuse, four concurrent jobs,
  original-source diagnostic spans, ABI/marshal checks, rejected wrong extent
  and matrix order, module dependency/reload identity checks, and GLSL
  compilation with deliberately unavailable Slang passed. The final run is
  recorded in `logs/frontend-final.log`; its result is
  `Frontend/ABI/cache/diagnostic validation passed.`
- Existing OpenGL evidence is separately recorded in `reports/gl-final-capture1.json`,
  `reports/gl-capture4.json`, and `reports/gl-reload-capture.json`. Viewed
  captures show the OpenGL renderer path; `reports/gl-reload.json` records
  `Invalidated 54 loaded renderer shader source(s).` This validates the authored
  GLSL reload/control path only.
- The OpenGL evidence does not validate Slang-generated GLSL, and no native
  Slang Monado stereo output is claimed.
- GPU pilot output/timing, module reload matrix, and OpenGL/Vulkan/stereo output:
  still in progress; phase H is not yet marked complete.

The optional API review broker failed before doing work because its account had
no remaining credits. Native agents and local runtime validation were used;
no broker result is claimed as evidence.

## Primary references

- [Slang command-line reference](https://docs.shader-slang.org/en/stable/external/slang/docs/command-line-slangc-reference.html)
- [SPIR-V target details](https://docs.shader-slang.org/en/latest/external/slang/docs/user-guide/a2-01-spirv-target-specific.html)
- [Slang reflection and matrix-layout conventions](https://shader-slang.org/slang/user-guide/reflection)
- [Vulkan shader modules and direct Slang compilation](https://docs.vulkan.org/tutorial/latest/03_Drawing_a_triangle/02_Graphics_pipeline_basics/01_Shader_modules.html)

The installed compiler's `-help`, emitted JSON, and SPIR-V were inspected directly;
older reflection documentation describing CLI reflection as unavailable does not
describe the installed 2026.8 tool.
