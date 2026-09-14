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
to supplier, descriptor lifetime, frequency, and provider names. Engine/material uniform blocks feed the
existing typed uniform publication path; externally owned resources retain
explicit descriptors. Unsupported or ambiguous mappings fail before module use.
The counter-copy buffers use external suppliers, `Globals` descriptor lifetime
and pass-frequency data; the scene-copy sampler has material ownership and lifetime.

Each reflected binding retains a stage-independent resource ABI identity covering
its complete physical layout and semantic contract. Both monolithic and separable
program linking reject different identities at a shared set/binding, including
a native resource shared with an uncontracted GLSL stage. Identities also enter
frequency publication signatures. Vulkan itself only checks descriptor topology;
it cannot prove matching host layouts or semantic providers across stages.

Slang JSON reflection is checked against SPIR-V decorations, including offsets,
array strides, matrix strides/order, structured-buffer stride, and physical
storage-buffer pointers. Combined texture samplers are initially limited to
single non-array 2D resources. Descriptor arrays and unsupported native layouts
are rejected. `ShaderAbiMarshalVerifier` checks blittability, managed size,
member offsets/sizes, and the UInt64 representation of GPU-address fields.
CPU and automatic `Matrix4x4` publication require physical `RowMajor`, stride 16;
no implicit transpose is performed. Specialization-controlled array lengths are
rejected because their default values cannot establish a fixed host ABI.

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

## Pilots and measured decision

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
`Build/_AgentValidation/20260910-060112-vulkan14-h/` and is disposable.

The actual authored counter-copy shaders copied `[7,19,43]` from the source SSBO
to the destination SSBO in both languages. A source-buffer change propagated
`[7,19,71]`; recompiling the native shader with an increment of `third` produced
`[7,19,44]`. Strict reflection verified the two set-0 descriptors and 12-byte
element layout. The actual authored scene-copy fragment shaders sampled the same
2x2 texture into byte-identical 256x256 images. Four quadrant colors and sample
positions were checked, and the output PNGs were viewed. A native source edit
inverted the sampled colors and produced the expected changed image. Both native
assets also generated modules through production `XRShader`/`VkShader` wrappers.

One RTX 3090 run produced these bounded pilot measurements (milliseconds except
GPU microseconds):

| Pilot/frontend | Cold compile | Warm request | CPU preparation median | GPU median |
| --- | ---: | ---: | ---: | ---: |
| Counter-copy Slang | 681.38 | 225.30 | 0.171 | 3.264 us |
| Counter-copy GLSL | 71.09 | 69.90 | 0.165 | 3.200 us |
| Scene-copy Slang | 721.03 | 223.81 | 0.642 | 5.104 us |
| Scene-copy GLSL | 66.21 | 67.54 | 0.623 | 5.136 us |

Cold requests use a unique harmless source comment and record cache misses;
the corresponding Slang warm requests hit the artifact cache. These are public
frontend request timings: GLSL Shaderc requests do not use the outer production
Vulkan artifact cache, so this table is not an engine-cache comparison. Slang's
warm requests still fingerprint the external toolchain and source graph. CPU
preparation includes the fixture's resource/module/pipeline setup; GPU timestamps
bracket the actual commands, with waits measured separately. Scene-copy variants
alternate in the paired samples. These tiny workloads do not establish a general
frame-time improvement.

**Decision: retain the two opt-in pilots; do not expand or switch defaults from
this evidence.** Direct compilation, strict ABI admission and reload work, while
compiler overhead is higher and GPU costs are close enough to warrant workload-
specific investigation before wider adoption.

## Backend validation and remaining coverage

- Isolated Release Vulkan and RenderBench builds: zero warnings/errors.
- The final native-resource merge probe used actual reflected vertex/fragment
  resource contracts. Matching identities merged and retained both stage bits;
  a changed provider contract and a native/uncontracted binding mix both failed
  admission. The final GPU fixture rerun still generated both production modules
  and passed the output/reload checks after this guard.
- Public frontend harness: cold/warm artifact reuse, four concurrent jobs,
  original-source diagnostic spans, ABI/marshal checks, rejected wrong extent
  and matrix order, module dependency/reload identity checks, and GLSL
  compilation with deliberately unavailable Slang passed. The final run is
  recorded in `logs/frontend-final.log`; its result is
  `Frontend/ABI/cache/diagnostic validation passed.`
- Existing OpenGL evidence is separately recorded in `reports/gl-final-capture1.json`,
  `reports/gl-final-capture2.json`, and `reports/gl-reload-capture.json`. Viewed
  captures show the OpenGL renderer path; `reports/gl-reload.json` records
  `Invalidated 54 loaded renderer shader source(s).` This validates the authored
  GLSL reload/control path only.
- The OpenGL evidence does not validate Slang-generated GLSL. Monado validation
  below exercises the retained authored GLSL stereo route, not native Slang stereo.
- Vulkan desktop with pilot selection enabled rendered the box cohort and UI
  from two camera positions, then after shader reload. Frame 4057 completed
  with zero Vulkan validation messages/errors and zero dropped frame/draw/compute
  operations (`reports/vk-validation-summary.json`). This uses conventional
  descriptor indexing; loaded pilot assets alone are not counted as executed
  shader comparisons, which are established by the separate GPU fixture above.
- The additional authored-GLSL OpenGL water/point-light cohort loaded the real
  `.tesc`/`.tese` water stages and responded to camera/reload changes, but its
  viewed output was heavily overexposed. This is not accepted as a clean water
  visual result or proof that every loaded shader executed. The final H6
  qualification below supersedes this early visual result.
- Vulkan tessellation stage mapping exists, but the inspected baseline lacks
  device-feature enablement and graphics pipeline tessellation state. No Vulkan
  runtime tessellation support is claimed. The bounded corpus/stereo coverage
  and its explicit supported-stage limits are recorded in H6's final matrix.
- The final Monado Vulkan run requested conventional descriptor indexing and
  single-pass stereo with Slang deliberately unavailable. Runtime telemetry
  reported `TrueSinglePassStereo`, 34 draws in each eye and zero validation
  messages/errors at completed frame 8877. Both 896x1007 eye textures were
  captured with `capture_openxr_eye_preview_texture` and viewed; their different
  perspectives show the box cohort. The generic `capture_viewport_screenshot`
  eye request timed out after 60 seconds, while the dedicated preview capture
  succeeded. The editor session and the task-owned Monado service are stopped.

The [2026-09-13 code closeout](vulkan14-code-closeout-2026-09-13.md) supersedes the
generic XR timeout above: selected-eye routing, successful copy provenance,
bounded failure propagation and cancellation are implemented, and both generic
Monado eye captures passed and were viewed. Bounded GL-issued/Vulkan-recorded
shader counters now provide authored source/stage attribution, including actual
OpenGL compute and tessellation calls in the control.

The [2026-09-14 final validation](vulkan14-final-validation-2026-09-14.md) closes
H6's selected representative matrix. Manual exposure exposed a separate missing
water surface: nested framebuffer attachment work restored native FBO 0 instead
of the forward target. Corrected general/read/write scope restoration and scaled
grab resizing restore the authored water in two viewed camera positions. Final
GL source-edit, syntax-error last-good recovery, geometry and tessellation
controls pass. Both final Monado Vulkan true-SPS and explicitly selected OpenGL
sequential eye captures were viewed; unsupported OpenGL true-SPS fails visibly.
The [matrix](vulkan14-h6-qualification-2026-09-14.md) does not qualify every
source/backend/stage combination, generated Slang OpenGL, or Vulkan runtime
tessellation. Issued/recorded counters remain distinct from GPU completion and
viewed image evidence.

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
