# Browser shader cooking and material generation

**Date:** 2026-09-28. **Status:** Source implemented; cooker execution, builds,
browser rendering and toolchain qualification remain explicitly deferred.

The standalone `Tools/ShaderCooker` .NET 10 console project replaces the Python
shader-packaging workflow. It links the shared material generator and target-tagged
shader artifact contracts without referencing the desktop engine graph. It has no
package references, compiler downloads or Python prerequisite. The existing
checked-in schema 1 shader package remains usable without running the cooker.

## Implemented routes

| Input | Offline output | Scope |
| --- | --- | --- |
| Explicit WGSL | Normalized WGSL and a hashed descriptor | Existing schema 1 compatibility or schema 2 provenance |
| Typed material recipe | Generated WGSL and a schema 2 descriptor | Opaque unlit tint or sampled color; fixed mesh ABI |
| Slang | WGSL from an externally provisioned Slang 2026.8 and a schema 2 descriptor | Fixed vertex/fragment entry points, bounded includes and definitions; qualification pending |

`BrowserMaterialShaderGenerator.Generate(definition, target)` is a target-aware
engine API. `BrowserMaterialRecipe.GenerateShader(target)` connects the existing
explicit `XRMaterial` browser representation to it. Generation selects typed
material semantics rather than translating desktop GLSL. Unsupported targets,
lighting, masked/translucent surfaces, skinning, storage and bindless requests
fail with material/pass/target context. Generated opaque shaders write alpha 1.
This boundary does not automatically translate arbitrary desktop shader graphs.

The existing Slang version pin is reused; neither Vulkan's compiler nor its
dependency supply changes. The tool resolves `XRE_SLANGC`, `VULKAN_SDK/Bin`, then
`PATH`. A missing or different compiler is an error. The identity includes compiler,
library and standard-module hashes. Candidate source/include files are captured
before and after compilation, and the compiler dependency file must stay within
that graph. Processes use argument lists, bounded output, a 45-second deadline,
Ctrl+C cancellation and cleanup. No compiler process is reachable from browser
runtime initialization.

Slang reflection is currently retained by the adapter as bounded JSON. A separate
bounded lexer checks the emitted WGSL itself: exactly four resource bindings,
uniform address spaces, matrix/tint offsets and extents, matrix stride, and
texture/sampler types. It resolves the selected scalar/vector/matrix types,
aliases and one-member structure wrappers, including literal alignment/size
attributes. Unsupported arrays, storage, computed layout attributes and other
unestablished layouts fail before publication. This is a selected-profile ABI
check, not a full WGSL compiler or reflection engine. Browser shader/pipeline
validation and known-value rendering still need qualification; previous Vulkan
evidence does not qualify WGSL.

## Cooking and selection

Run these commands later with a provisioned .NET 10 SDK; they were not executed
for this delivery. From the repository root, retain the default artifact and add
both generated variants with one manifest publication:

```sh
dotnet run --project Tools/ShaderCooker/ShaderCooker.csproj -- --recipe XREngine.Runtime.Rendering.WebGPU/Shaders/browser-unlit-v2.recipe.json --recipe XREngine.Runtime.Rendering.WebGPU/Shaders/browser-unlit-color.recipe.json --recipe XREngine.Runtime.Rendering.WebGPU/Shaders/browser-unlit-texture.recipe.json
```

Select `?shader=browser-unlit-color` or `?shader=browser-unlit-texture` after
publishing the cooked files. Missing names fail; the runtime does not substitute
another shader. Only the selected artifact is fetched, compiled and warmed.

The Slang example is a separate source root and package invocation:

```sh
dotnet run --project Tools/ShaderCooker/ShaderCooker.csproj -- --source-root XREngine.Runtime.Rendering.WebGPU/Shaders --recipe XREngine.Runtime.Rendering.WebGPU/Shaders/browser-unlit-slang.recipe.json --output XREngine.Runtime.Rendering.WebGPU/Assets/shaders
```

That invocation publishes a manifest containing `browser-unlit-slang`; open
`?shader=browser-unlit-slang`. Each invocation replaces the manifest with exactly
its supplied recipes. Immutable old artifacts are retained. Do not run the Slang
invocation expecting it to append to the preceding manifest. For a larger package,
author its recipes under one source/dependency root and pass every desired recipe.

Schema 1 and schema 2 cannot share a manifest; the v2 seed recipe preserves the
`browser-unlit` name for a schema 2 package. The no-argument cooker continues to
target the existing schema 1 recipe. The [tool README](../../../../Tools/ShaderCooker/README.md)
describes input and output roots.

## Artifact and diagnostic contract

Schema 2 adds ordered include roots, a coordinate-contract identity and source
origin metadata to the previous descriptor. Its digest covers language, target,
entry points, definitions, explicitly empty unsupported specialization, compiler
identity, requirements, dependencies, matrix convention, semantic schema, layout
and pipeline state. Recipe and input files are dependencies. Emitted WGSL uses LF;
original-byte dependency digests and emitted-byte digests remain distinct.

The cooker prepares the entire package before writing immutable hash-named files
and atomically replacing the manifest last. Inputs reject duplicate JSON fields,
unsupported stages/options/layouts, root escapes, links/reparse points and oversized
data. Limits are 16 artifacts, 1 MiB WGSL, 64 KiB JSON and 512 published dependencies.
The browser checks schema agreement, compiler/language pairing, dependency shape,
source origins, hashes, fixed binding/layout/pipeline requirements and device limits.

Authored WGSL preserves exact line/column locations. Generated material and Slang
diagnostics name the generated WGSL location and its authored origin explicitly;
they do not invent original line mappings. Slang's own compilation failures retain
its source diagnostics. Full original-source maps for Slang output remain future
work rather than being inferred from generated line numbers.

## Physical layout and coordinates

`BrowserShaderAbi` is used by frame matrix and upload tint writers. It defines
32-bit little-endian float components, the vertex offsets and uniform extents.
No CLR struct packing or assumed GLSL layout is used to write the wire bytes.

| Data | Layout / binding |
| --- | --- |
| Position and UV vertex | Stride 20; float3 at byte 0 / location 0; float2 at byte 12 / location 1 |
| Combined object/view/projection matrix | 64 bytes, alignment 16, column stride 16; group 0 binding 0, vertex uniform, dynamic offset |
| Linear tint | 16 bytes, alignment 16; group 1 binding 0, fragment uniform |
| Sampled color | group 1 binding 1, float 2D texture; binding 2, filtering sampler |
| Color output and depth | fragment location 0; one sample, opaque canvas color; depth24plus, clear 1, compare less |

There are no admitted storage buffers or uniform arrays in this profile. Dynamic
uniform offsets use the device's `minUniformBufferOffsetAlignment`, independently
of the WGSL structure's 16-byte alignment. Each draw supplies one combined matrix;
there is no separately padded frame uniform block hidden in the ABI.

The coordinate identity is `xrengine.webgpu.coordinates.v1`: engine -Z forward,
+X right and +Y up; System.Numerics row vectors use model * view * projection.
M11 through M44 are written in row order; WGSL reads those bytes as matrix columns
and multiplies matrix * column vector. Camera projection supplies zero-to-one
clip depth. The executor uses positive viewport dimensions and explicitly selects
CCW front faces with culling disabled. UVs pass through unchanged; mesh UVs and
cooked image row order must agree, with no implicit upload or sample Y flip.
Reversed Z and render-to-texture sampling variants are not admitted by this contract.

Deferred known-value checks include an identity transform, translation (1,2,3)
whose bytes 48/52/56 become WGSL's translation column, near/far depths 0/1,
asymmetric geometry and a labeled corner texture. These are expected values for
later visual/readback qualification, not test results from this delivery.

## Readiness budgets

Shader diagnostics and asynchronous pipeline creation each have a monotonic
45-second startup deadline and stop/cancellation handling. Validation and
out-of-memory scopes surround the operations. A timed-out operation may finish
inside the browser later, but it cannot publish a pipeline into an abandoned
session. Only the selected pipeline is warmed; there is no synchronous frame-loop
wait or bulk variant compilation.

Explicit counter snapshots include fetch/hash, shader compilation, pipeline
creation and total startup durations, configured budgets and the current startup
stage. These are failure deadlines and instrumentation, not mobile latency targets
or measured performance claims.

## Remaining acceptance

The new cooker, generator and Slang example have not been executed. No build,
tests, browser run, shader compilation, benchmark, SDK install or Python execution
was performed. Main TODO acceptance stays open for physical layout/reflection
qualification, known-value rendering/readback, unsupported-input and cancellation
cases, artifact corruption/version mixing, startup measurements and Vulkan
regressions. Broader material/pipeline features remain separate renderer work.

Primary references: [Slang command-line options](https://shader-slang.org/slang/command-line-slangc-reference.html),
[Slang WGSL target restrictions](https://shader-slang.org/slang/user-guide/wgsl-target-specific),
[WGSL alignment and size](https://www.w3.org/TR/WGSL/#alignment-and-size).
