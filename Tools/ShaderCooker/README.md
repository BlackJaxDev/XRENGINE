# Engine shader cooker

`ShaderCooker` is an isolated .NET 10 console project. It links only the shared
shader artifact/ABI types and the legacy fixture material generator; it does not load the engine
project graph or download NuGet packages. Use a provisioned .NET 10 SDK:

```sh
dotnet run --project Tools/ShaderCooker/ShaderCooker.csproj -- --recipe XREngine.Runtime.Rendering.WebGPU/Shaders/browser-unlit-v2.recipe.json --recipe XREngine.Runtime.Rendering.WebGPU/Shaders/browser-unlit-color.recipe.json --recipe XREngine.Runtime.Rendering.WebGPU/Shaders/browser-unlit-texture.recipe.json
```

The default invocation packages the original explicit `browser-unlit` WGSL
recipe as schema 1, byte compatible with the existing checked-in assets. Schema
1, schema 2, and schema 3 recipes must be cooked in separate invocations. `--recipe` can
be repeated up to 16 times; `--source-root` defaults to the WebGPU `Assets`
directory, and `--output` defaults to its `shaders` directory. The source root
and recipes must live under the same parent directory, so dependencies have
stable, normalized paths such as `Assets/mesh.wgsl` and
`Shaders/browser-unlit-color.recipe.json`. An explicit alternate source root
sets the default output to its `shaders` child.

Schema 2 retains the fixed mesh layout, pipeline, entry points and WebGPU
coordinate contract. It accepts explicit WGSL, `MaterialRecipe` JSON for
opaque unlit tint/texture variants, and Slang. Slang requires a separately
provisioned **2026.8** `slangc` (`XRE_SLANGC`, `VULKAN_SDK/Bin`, or `PATH`);
the cooker does not download it. Slang recipe includes are source-root-relative
directories, and defines are literal `NAME` or `NAME=VALUE` options. Unsupported
specialization, feature, material, ABI, or compiler requests fail with context.

The cooker validates all recipes before publishing files. Source and descriptor
files use SHA-256 names and cannot be replaced with different bytes; the
manifest is written last. It records the authored recipe and source/include
dependencies, compiler identity, and a generated/identity/unmapped source map.
Browser WGSL compilation remains authoritative for WGSL semantics.
Emitted WGSL normalizes line endings to LF; schema 2 records the original input
digest separately from the emitted digest while preserving line/column locations.
Slang reflection is checked as bounded JSON. A separate selected-profile WGSL
lexer checks the emitted resource bindings and physical uniform layouts; unknown
layouts fail explicitly. It is not a general WGSL compiler. The browser remains
the authority for WGSL semantics and pipeline compatibility.

The sample Slang input is under `XREngine.Runtime.Rendering.WebGPU/Shaders`.
Cook it with that directory as `--source-root`, the
`browser-unlit-slang.recipe.json` recipe, and an explicit `--output` pointing to
the module's `Assets/shaders` directory. That replaces the manifest with the
Slang artifact alone; select `?shader=browser-unlit-slang`. The default C# cook
does not invoke Slang. The engine implementation also preserves these recipes as real cooker compatibility
checks; successful cooking alone does not qualify browser rendering.

## Engine artifacts

Schema 3 adds an explicit engine whole-program ABI. Existing schema 1/2 recipes
remain compatibility fixtures and retain their descriptor shape. Cook each schema
in a separate invocation. An engine recipe must use authored `Slang` or explicit
`WGSL`; `MaterialRecipe` remains the frozen fixture generator and is rejected for
engine artifacts.

```sh
dotnet run --project Tools/ShaderCooker/ShaderCooker.csproj -- --recipe Build/CommonAssets/Shaders/WebGPU/engine-depth.recipe.json --source-root Build/CommonAssets/Shaders/WebGPU --output <artifact-output>
```

A schema 3 recipe names its pass and versioned semantic identity, explicit vertex
and optional fragment entries (or a compute entry), vertex stream slots, strides,
locations, formats and engine semantics. Packing is canonical: a renderer may use
equivalent engine stream slots/strides/offsets, provided it preserves the exact
location/format/semantic contract, keys pipelines by the resolved layout, and
checks the resolved layout against device limits. Every binding declares its stage
visibility, ownership/update frequency, physical resource name, byte size, dynamic
offset policy, and physical member/provider offsets. All currently admitted
engine uniform bindings use dynamic offsets so multiple objects and views cannot
accidentally consume the last draw's upload. Actual offsets must obey the selected
device's uniform alignment; structure size is independent of that alignment.

`pipeline` is an empty object: material/pass render state remains engine-owned.
The reader verifies required limits against the declared layout. The emitted WGSL
verifier checks the actual entry points, vertex locations/types, complete resource
set, physical names, uniform member offsets/sizes/types/matrix strides, and resource
visibility through reachable functions. Scalar/vector/mat4 members and fixed
structures are admitted; runtime-sized storage arrays, other vertex formats,
optional device features, and unlisted layouts fail explicitly. This is a bounded
ABI verifier, not a WGSL semantic compiler.

`ShaderProgramArtifactReader.Read` loads the descriptor and WGSL bytes, checks the
source SHA-256 and length, and returns a target-tagged `ShaderProgramArtifact`.
The engine asset loader can attach it to `XRRenderProgram.CookedArtifact`, or to
all of the program's `XRShader.CookedArtifact` companions with the same descriptor
identity. Attaching a module sets its serialized `CookedArtifactIdentity`; the
full module is runtime-only. This exact descriptor SHA-256 survives normal cooked
serialization without embedding source/metadata records into every shader.

A runtime content owner preloads descriptor and WGSL sidecars through its normal
asset source, calls `ShaderProgramArtifactReader.Read`, and constructs an immutable
`ShaderProgramArtifactCatalog`. Its renderer supplies that session-scoped resolver
to `TryGetCookedArtifact(target, resolver, out artifact)`. An explicit unresolved
program identity fails rather than falling back to stage companions. No global
registry or raw-source/path matching is used. Catalog replacement occurs at a
world/resource generation boundary; stale session completions must be rejected
by the content owner. Device recreation can reuse the same immutable CPU catalog.

`DescriptorBytes` retains the exact verified descriptor for repackaging. A
layout-only constructed artifact lacks those bytes and must fail packaging.
The catalog re-verifies descriptor/source bytes and identity before publication.
`TryGetCookedArtifact` never guesses a replacement for an unrecognized custom
vertex or fragment shader. Source, dependency, stage, or frontend changes
invalidate both companion and serialized reference; stage collection/source changes
invalidate a program's explicit module/reference. Cooked hydration restores the
shader source-change subscription without discarding its serialized identity.
Desktop shader source language and GLSL remain unchanged.

The first additive engine source, `WebGPU/Depth.vert.slang`, implements position
transformation for an opaque depth or single-view shadow caster. ViewProjection
and ModelMatrix are independent 64-byte dynamic bindings. This source does not
admit masked materials, deformation, depth-normal output, layered/cube shadow
rendering, or a production lit material. Those require separate explicit
counterparts and runtime evidence. The coordinate contract uses zero-to-one clip
depth; callers must supply an appropriately lowered engine projection. Known-value
rendering remains required before extending the pass group. The explicit
`engine-depth-probe` recipe additionally selects `depthProbeFragment`, which writes
`(rasterDepth, 0, 1 - rasterDepth, 1)` to a color attachment for diagnostic readback.
It is labeled `depth-probe` and is never a production material fallback.

On Slang 2026.8, `-o` precedes stage entries and WGSL emits one module without the
single-entry `-whole-program` flag, which otherwise can emit to stdout instead of
the requested file. Linux split `bin`/`lib` installations include compiler libraries
and standard modules in compiler identity. Library symlinks are only admitted when
their final target remains within the same installation; the alias and target
paths and content all contribute to identity.
