# Browser shader cooker

`ShaderCooker` is an isolated .NET 10 console project. It links only the shared
browser material generator and shader artifact types; it does not load the engine
project graph or download NuGet packages. Use a provisioned .NET 10 SDK:

```sh
dotnet run --project Tools/ShaderCooker/ShaderCooker.csproj -- --recipe XREngine.Runtime.Rendering.WebGPU/Shaders/browser-unlit-v2.recipe.json --recipe XREngine.Runtime.Rendering.WebGPU/Shaders/browser-unlit-color.recipe.json --recipe XREngine.Runtime.Rendering.WebGPU/Shaders/browser-unlit-texture.recipe.json
```

The default invocation packages the original explicit `browser-unlit` WGSL
recipe as schema 1, byte compatible with the existing checked-in assets. Schema
1 and schema 2 recipes must be cooked in separate invocations. `--recipe` can
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
does not invoke Slang. No cook or compiler execution was performed for this source
delivery; schema compatibility and byte reproducibility still need qualification.
