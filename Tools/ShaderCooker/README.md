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
be repeated up to 24 times; `--source-root` defaults to the WebGPU `Assets`
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
Generated engine-PBR descriptors retain the generator's complete pinned active
Slang source graph, together with the material JSON and recipe. Unrelated files
watched by the compiler are still checked for changes during cooking but do not
enter that native surface provenance closure. Older generated descriptors remain
readable by raster consumers; native generated-surface admission requires a
recook with the exact closure. This is a trusted offline-cooker contract, not
runtime proof of arbitrary WGSL semantics.
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

The `AuthoredLitTexturedV1` family lowers the six exact forward normal/specular
sources with `baseColor: "authored-textured"` and `textureFlags` equal to 1, 2, 3,
5, 6, or 7 (normal = 1, specular = 2, opacity = 4). The material recipe also
declares `normal: "texture"` when bit 1 is present, otherwise `"vertex"`.
Opacity families accept `"masked"` and `"alpha-blend"`; other families accept
`"opaque"` and `"alpha-blend"`. Only opacity families clip, always using strict
diffuse alpha times opacity red against the authored cutoff.

Use `engine-authored-textured-local-shadows.recipe.json` as the authored receiver
layout and stage the selected closure from
`EngineAuthoredTexturedShaderGenerator.DesktopSources(textureFlags)` under
`Desktop`. The emitted descriptor records `authoredTextureFlags`. Companion
recipes carry explicit `-f1`, `-f2`, `-f3`, `-f5`, `-f6`, or `-f7` identities for
depth-normal, directional depth, point/spot depth, and sorted-order receivers.
Selectors for another feature mask are rejected even when physical layouts match.

The role binding pairs are fixed: diffuse at group 1 bindings 1/2, normal at 3/4,
specular at 5/6, and opacity at 7/8. Binding 9 supplies the 16-byte authored flags,
cutoff, normal-map mode, and height-scale block. The receiver retains the shared
32-byte material factor block at binding 0. Auxiliary programs omit unused
factors and texture pairs while preserving role slots. The normal-capable vertex
layout adds an optional float4 tangent and explicit scalar tangent-presence
attribute; the renderer supplies constant zero/one presence streams, so absent
tangents remain distinct from invalid authored tangent values.

Normal mapping preserves the source RGB y flip, outward z clamp, independent
transformation of tangent/bitangent, finite guards, derivative basis fallback,
and explicit `NormalMapMode = 1` Sobel height interpretation. Existing opaque
deferred normal recipes retain their prior layout and RGB/tangent admission;
their sampled and final mapped normal guards now reject nonfinite/degenerate
values consistently. Neither their height mode nor missing-tangent profile is
implicitly admitted.

Native consumers of all six authored textured families additionally require
`XR_ADV_AUTHORED_BASIS_SCHEMA_VERSION=1`. The browser-only SceneArena directory
row 33 links each dense draw to its generation-checked geometry and selected
mesh revision, with immutable raw normal/tangent data captured from authoritative
source streams. Absent tangents remain distinct from authored zero/parallel
tangents. Defined normal-map guards preserve the source normal or +Z result;
specular-only sources with undefined zero normals receive a named rejection.
The cache refreshes at geometry revision boundaries and uses no GPU readback.
Built-in deformation supplies the same 32-byte raw basis through its browser
producer/output companion; native shading reads the current basis from the
geometry arena's deformation-directory extension. Custom producers must supply
an equivalent retained source/output contract. Canonical desktop and persisted
geometry, vertex, and material records retain their existing layouts.

The explicit `AuthoredLitTextureAlphaV1` family uses a schema-three
`MaterialRecipe` with `baseColor: "texture-alpha"`, `normal: "vertex"`, and
`surface: "masked"` or `"alpha-blend"`. Use the physical layout from
`engine-textured-alpha-local-shadows.recipe.json`, a per-material `mat-<id>` name,
and no ordinary `materialVariant` selector. The frontend requires the pinned
Slang files plus the canonical authored source/snippet graph under
`Desktop/Common` and `Desktop/Snippets` inside the source root. The shared
`EngineTexturedAlphaShaderGenerator.RequiredDesktopSources` lists that closed
graph. Missing or modified dependencies fail before artifacts are published.

Cook `engine-textured-alpha-depth-normal`, `engine-textured-alpha-depth`,
`engine-textured-alpha-point-depth`, `engine-textured-alpha-spot-depth`, and
`engine-textured-alpha-local-shadows-authored-order` recipes for exact normal,
shadow and sorted-order companions. This family preserves diffuse alpha times a
required red opacity mask; existing opaque texture recipes keep their previous
meaning. Advanced browser consumers additionally require the updated schema-4
engine-surface and schema-1 authored-basis native recipes, including the separate
x4 variants.

Schema 3 adds an explicit engine whole-program ABI. Existing schema 1/2 recipes
remain compatibility fixtures and retain their descriptor shape. Cook each schema
in a separate invocation. An engine recipe uses authored `Slang`, explicit
`WGSL`, or the bounded engine-PBR `MaterialRecipe` frontend described below.

Engine recipes may declare an exact cooked material selector with an optional
`materialVariant` object containing `semantic`, `semanticVersion`,
`vertexProfile`, and `outputProfile`. The recipe's `target` and `pass` complete
the key. Supported engine variants use explicit versioned semantics and physical profiles.
The declaration is copied into the hash-owned shader descriptor and emitted as
an explicit `materialVariants` reference in the cooker manifest. Recipes without
the declaration emit no variant reference. Browser publishing carries these
references only when the selected world includes the exact descriptor hash;
unknown or custom materials remain unsupported without an explicit companion.

A schema-three whole-program recipe can also declare
`"pipelineArtifact": { "pass": "tonemap" }` or
`"pipelineArtifact": { "scope": "advanced", "pass": "tonemap" }`. This explicitly binds the
verified vertex/fragment program to an authored pass in the manifest's
`pipelineArtifacts` array using the descriptor's SHA-256 identity. The cooker
does not infer pipeline ownership from a shader name or source path. Unscoped
entries retain their original pass binding key. Scoped entries use `scope::pass`,
while the descriptor still declares the original pass. Scope and pass are
bounded lowercase identifiers without colons, so distinct authored pipelines
may use the same pass name without collisions. Duplicate binding keys and a
mismatched recipe pass fail before manifest publication. Missing required
programs are reported by the selected pipeline.

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

Schema-3 `MaterialRecipe` is the target-aware engine lit frontend. Its source is
a UTF-8 `.material.json` object with `schemaVersion: 2`, a name matching the
shader recipe, `shadingModel: "lit"`, `surface: "opaque"`, `baseColor: "tint"`
or `"texture"`, and optional `normal: "vertex"` or `"texture"`. The latter
requires textured base color. The shader recipe retains the physical layout,
pass `opaque-forward`, and `standardLitVertex`/`standardLitFragment` entries
from the matching canonical engine lit-color, lit-texture, or normal-texture
recipe, but omits `materialVariant` and declares respectively
`xrengine.engine.authored-lit-color.v1`,
`xrengine.engine.authored-lit-texture.v1`, or
`xrengine.engine.authored-lit-texture-normal.v1`. The cooker selects the
canonical PBR Slang frontend, compiles it to WGSL, records the authored recipe
and compiler dependencies, and verifies the emitted physical ABI. The material
recipe and selected engine Slang sources must be staged together beneath one
`--source-root`; author assets need not be moved or changed. The versioned
frontend pins canonical Slang input hashes so a same-named staged source
cannot silently replace the engine PBR lowering during the cook.

The engine's `CreateAuthoredLitPbrColorMaterial` and
`CreateAuthoredLitPbrTextureMaterial` factories produce an ordinary
`XRMaterial` with canonical desktop GLSL and explicit `AuthoredLitV1` intent.
`EngineLitMaterialShaderGenerator.TryPlan` reads its actual PBR parameters,
surface texture roles, and Uber authored state; `MaterialRecipeJson` emits the
corresponding source named `mat-<persistent-material-guid>`. Each matching
schema-3 shader recipe uses that same name. The project shader manifest must
contain the generated material's cooked descriptor before world export; the
editor does not run the standalone pinned Slang cooker implicitly. During
export the publisher resolves that descriptor by persistent material identity
and profile, then attaches its exact identity to a detached browser-target
stage. It never edits the original material's YAML or desktop GLSL.

The current modeled surface is
six deferred PBR factors: BaseColor, Opacity, Specular, Roughness, Metallic,
and Emission; opaque opacity is one. A texture surface additionally uses the
existing base-color, optional normal, metallic, and roughness surface bindings.
These same modeled inputs are used by the desktop GLSL material. The publisher
requires the canonical desktop fragment source; arbitrary custom GLSL is
rejected by name rather than inferred as PBR. Other Uber features, nonopaque
textured surfaces, and material shadow casting remain unsupported by this V1
profile and fail explicitly during publication.

`CreateAuthoredLitPbrColorCoverageMaterial` adds the explicit `AuthoredLitV2`
uniform-alpha color contract without changing V1. Its seven typed parameters
retain the same six PBR factors plus `AlphaCutoff`, which must match the material
property. The material source selects `surface` as `opaque-coverage`, `masked`,
`alpha-blend`, `premultiplied-alpha`, or `additive`; `baseColor` must be `tint`
and `normal` must be `vertex`. Its schema-3 recipe uses `forward-coverage`,
`xrengine.engine.authored-lit-color-coverage.v2`, and the complete physical
layout of `engine-standard-lit-color-coverage-local-shadows.recipe.json`, with
`materialVariant` omitted and `sourceLanguage` set to `MaterialRecipe`.
The generated program uses the pinned canonical coverage/local-shadow Slang
frontend and all fourteen resource bindings, including the directional, point,
and spot receivers. Existing light capacities, storage, PCSS, and output-profile
limits remain authoritative.

Mixed GPU and CPU-direct transparent replay uses explicit cooked
`static-position-normal-order-gate-v1` color variants. The three
`engine-standard-lit-color-coverage*-authored-order.recipe.json` recipes retain
the original plain, directional-shadow, or local-shadow fragment entry and
replace only the completed vertex position for an inactive source rank. They
reserve group 0 bindings 2 and 3 for `AuthoredSourceRanks` and the 16-byte
`AuthoredOrderGate` object uniform; shadow receiver bindings remain in group 3.
The descriptor declares the extra vertex storage binding and dynamic uniform.
`EngineAuthoredOrderGateContract` verifies the pinned original and wrapper
source closures, unchanged original resources and vertex inputs, and the exact
gate ABI before retaining both descriptor identities. Canonical generated
`AuthoredLitV2` color programs use the proven local-shadow companion. Ordinary
material programs remain the default, and no depth or shadow-caster variant is
selected for this transparent replay.

The detached color carrier retains transparency, cutoff, render pass, blend/depth
state and sort priority through the existing generic binary contract; no new
texture payload is introduced. Opaque and masked V2 surfaces require the
canonical `StandardLitColorV2` depth-normal program. Casting lights additionally
require the corresponding coverage depth, radial point, or projected spot
programs. These auxiliary programs revalidate the source's exact generated
companion and read the same live opacity/cutoff. Sorted surfaces do not enter
normal or shadow replay. Desktop authoring retains the existing coverage GLSL
and its closed canonical snippet graph. Texture-alpha, opacity-map, arbitrary
GLSL/Uber lowering and native Advanced surface reconstruction are not inferred
from this raster companion.

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

## Authored native vertex functions

An opaque color schema-2 material JSON may add `"nativeVertex": { "body":
"<Slang statements>" }`. This is an additive target cook input; it does not
change the authored material, desktop GLSL, or serialized material format. The
material and raster recipe name must be `mat-<persistent-guid-without-dashes>`.
The raster recipe declares
`xrengine.engine.authored-lit-color-native-vertex.v1`, `opaque-forward`, and
`standardLitVertex`/`standardLitFragment`. Its physical layout is the canonical
color PBR layout plus the `NativeVertexInputs` material uniform at group 1,
binding 1: 64 bytes, dynamic, vertex-only, four `vec4<f32>` members named
`input0_0` through `input3_0`, with providers `NativeVertexInput0` through
`NativeVertexInput3` at byte offsets 0, 16, 32, and 48. It declares at least three
bind groups and five dynamic uniform bindings. Texture and coverage extensions
are excluded from this profile. Shadow receivers retain their existing group 3
bindings; the generated material supplies exact auxiliary programs.

The body runs in an isolated generated Slang module with this fixed interface:

```slang
public void applyNativeVertex(inout float3 position, inout float3 normal,
    float4 input0, float4 input1, float4 input2, float4 input3)
```

The body may perform arbitrary local arithmetic and control flow. It cannot
escape that function, introduce imports or preprocessing, access textures or
other resources, or use stage-specific operations. Position and normal are
the only geometry inputs. Tangents, UVs, colors, topology, view state, and
invocation IDs are unavailable. The imported module cannot see the raster
wrapper's globals. Inputs and results must obey the engine's finite position
and finite nonzero normal domain; this frontend does not prove numerical
stability of arbitrary authored equations. No deformation equation is built in.

The cooker stages the exact generated module, pinned engine wrappers and their
shared sources, and pinned auxiliary recipe layouts. `NativeVertexRasterInputs.slang`
and `NativeVertexNormal.slang` apply the same canonical octahedral normal
quantization before and after the isolated function in every raster wrapper.
This includes the packed codec’s UnitY fallback for zero, near-zero, or nonfinite
input normals. The raster wrapper calls the function before the canonical
object/view transformation. The compute wrapper calls the same
function on current and previous local geometry after morph/skin. It copies
all 16 canonical packed vertex words, replaces position and the octahedral
normal, and preserves tangent and all remaining words. Normal decoding and
encoding retain the canonical packed-normal precision boundary.

Each raster recipe emits eight hash-owned artifacts: its base color raster,
packed compute, depth-normal prepass, three light caster programs, and two
shadow-receiving color programs. The compute name appends
`-native-vertex`; its schema is `xrengine.engine.native-material-vertex.v1`,
pass is `native-material-vertex`, entry is `nativeMaterialVertex`, and workgroup
is 64×1×1. Group 0 contains read-only source arena storage at binding 0,
writable output arena storage at binding 1, and a 160-byte dynamic uniform at
binding 2. This uniform contains four current and four previous float4 inputs,
then eight uint32 values: source current stream/offset, source previous
stream/offset, current output offset, previous output offset, vertex count,
and previous-valid flag. Offsets are bytes within their directory-selected
streams and must be 64-byte aligned. Source streams are 0, 2, or 3; destination
streams are 2 and 3. Both arenas contain the seven-entry four-word directory.
The wrapper verifies declared extents before writing. A missing previous
sample re-evaluates current source and inputs for previous output; the runtime
owns the corresponding temporal reset reason and accepted-frame history.

All eight descriptors carry the exact generated module source and its hash in
the optional `nativeVertexCompanion` object. The base raster names the exact
compute descriptor and six auxiliary descriptor identities. Every auxiliary
names the same compute descriptor. Native admission validates the pinned engine
wrappers, closed dependency set, shared authored function, compiler identity,
and compute physical ABI. Auxiliary resolution additionally checks its exact
name, stage entries, pass, and complete pinned physical resource/vertex layout. A descriptor link or semantic tag alone is
insufficient. This remains the trusted offline cooker contract, rather than
proof of arbitrary substituted WGSL semantics. Slang's `-preserve-params`
option is used only for this extension so constant bodies keep the declared
input ABI without artificial arithmetic. Recipes without `nativeVertex` keep
their previous compiler options and descriptor bytes.

Auxiliary names append `-native-depth-normal`, `-native-directional-shadow`,
`-native-point-shadow`, `-native-spot-shadow`, `-native-directional-receiver`,
or `-native-local-receiver`. Their semantic schemas use
`xrengine.engine.authored-lit-color` followed by that suffix and `.v1`.
All consume both position and normal, since authored positions may depend on
the input normal. Depth-normal preserves oct-encoded world normals; directional
casting preserves projected hardware depth; point casting preserves cube-face
projection and radial R16Float distance; spot casting preserves projected
R16Float depth. Receiver wrappers reuse existing directional/local shadow and
PBR equations after applying the same authored local function. Missing or
mismatched exact auxiliaries fail resolution rather than replaying static geometry.

Runtime admission currently supports these raster auxiliaries for static geometry.
The native Advanced producer composes the function after canonical morph/skin,
but the generic raster morph/skin producer has a different normal domain. That
generic combination, including shadow casting, rejects until an equivalent
canonical source is available. See the
[native material contract](../../docs/work/progress/rendering/browser-native-material-vertices-2026-10-03.md)
for accepted-frame history, conservative bounds and current acceptance limits.
