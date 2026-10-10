# Authored forward normal and specular texture families

The additive `AuthoredLitTexturedV1` surface represents six exact canonical
forward sources: `LitTexturedNormalForward`, `LitTexturedSpecForward`, and
`LitTexturedNormalSpecForward`, including each source's `Alpha` counterpart.
The existing two-image `AuthoredLitTextureAlphaV1` and opaque deferred material
carriers retain their own identities and serialized bytes.

## Source contract

All roles sample untransformed UV0. `Texture0` supplies diffuse RGBA. A normal
or height image, when present, occupies `Texture1`; a specular image follows
it, or occupies `Texture1` when no normal image is present. An opacity image
occupies the final slot. The red specular channel multiplies
`MatSpecularIntensity`; it is not a metallic or roughness channel. The forward
lighting factors are `Roughness`, `Metallic`, and `Emission`, with emission
multiplied by sampled diffuse RGB. `MatShininess` remains preserved and unused.

Without a separate opacity image, the source outputs diffuse alpha and never
clips. These families admit opaque or sorted alpha blend. With an opacity image,
coverage is diffuse alpha times opacity red and every admitted raster pass
clips strictly below `AlphaCutoff`, including sorted blended color. These
families admit masked or sorted alpha blend. Sorted materials use the existing
retained source-order gate and never enter native opaque shading or shadow/depth
replay.

The shared normal helper follows `SurfaceDetailNormalMapping.glsl`: RGB decode
flips tangent Y, clamps the normalized tangent Z outward, checks finite and
degenerate input/output, and uses the authored independently transformed tangent
basis or the source derivative fallback. Explicit `NormalMapMode=1` selects the
same eight-sample Sobel height conversion with authored `HeightMapScale`. The
canonical vertex generator derives local bitangent from local normal, tangent,
and tangent handedness before independently normalizing the normal-matrix
transforms. The new raster frontend preserves that order.

An absent tangent and a malformed authored tangent are distinct. The generic
runtime supplies immutable, renderer-owned constant tangent and presence streams
when needed; the shader's presence input selects the source's absent-tangent
zero sentinel. Existing mesh data and vertex profiles are unchanged. Constant
streams use legal zero-stride WebGPU vertex buffers, with their complete
attribute extent checked against the retained binding range before direct or
indirect commands are accepted. The rule follows the [WebGPU vertex buffer
layout contract](https://gpuweb.github.io/gpuweb/#abstract-opdef-validating-gpuvertexbufferlayout).

## Admission, serialization, and publication

Every family requires exact canonical source text/path, active snippet resolution,
pinned desktop and Slang dependency closures, complete reflected physical ABI,
persistent material identity, and matching texture-feature bits. Five companions
per feature combination cover depth-normal, directional/point/spot shadows, and
ordered color. The receiver retains the existing bounded PBR lighting and local
shadow helpers. Implicit sampling and derivative evaluation occur before
input-dependent fallback or discard.

`AuthoredTexturedMaterial` uses the dedicated `!xre-authored-textured-v1` YAML tag
and additive per-role settings; restoration can wait for metadata-first input.
Its shared restore registry also catches conflicts when images are aliased across
the existing textured-alpha subtype. `PublishedAuthoredTexturedMaterial` is a new
version-one cook-only carrier with unique images, exact semantic role references,
numeric controls, render state, sort priority, and retained cooked stages. It
reuses the existing texture settings record and unchanged raw texture codec.
Cook projection borrows authored data and owns its detached result.

The browser-only native engine surface companion is schema 5, 480 bytes. A sixth,
independent specular texture role follows the existing five roles; normal mode
and scale occupy the control vector after those roles. Six collision-free sampling
identities preserve each role's physical storage mip count, sampled base/count,
and relative min/max LOD clamps. These browser-only fields do not alter the
canonical sampler or texture records. Publication comparison includes the full
sampling tuple, even when different authored clamps fold to the same canonical
sampler. Shading bank keys, physical view/sampler caches, and masked visibility
all consume the frozen tuple. Sobel texel size comes from the selected bound view. Resource acquisition, release,
publication, and texture cohorts consume the role count. Masked mono and x4
visibility retain separate diffuse and opacity images. All eight native shading,
export, depth-comparison, and MSAA recipes require the new schema before binding.
Desktop material and texture-binding records remain unchanged.

## Browser source basis and GPU deformation

A browser-only immutable source image retains raw normal and tangent components,
handedness and tangent presence at the submitted mesh revision. It reads the
authoritative vertex buffers. A 48-byte SceneArena association binds that image
to the selected draw/geometry generation, mesh revision and submission versions.
Aliases may share packed geometry without conflating distinct source images;
selected LODs and stale associations are validated independently. Finite zero
or parallel authored tangents preserve the canonical normal fallback. Absent
tangents preserve derivative reconstruction. Nonfinite source data is rejected
precisely. Finite tiny or large source vectors remain raw until transformation
and shader validity checks.

Built-in morph/skin work carries a 32-byte basis output beside the existing
64-byte packed vertex output, sharing its aggregate owner, generation, dispatch,
current/previous history, cancellation and teardown. The raw source is an
additional browser input section; the thirteen canonical sections and seven
canonical geometry stream IDs remain unchanged. The same GPU influence loops
produce the basis with canonical cofactor skinning and post-deformation validity
checks, so morphs can rescue or cancel a basis before fallback selection. Browser
GeometryArena directory words identify the current/previous tails, with exact
range and source association checks. Previous basis history requires the same
submitted producer fence as previous packed vertices; aborted submissions do
not make history valid. External producers without the explicit basis contract
receive a specific diagnostic. No geometry/count readback or submission-mode
substitution discovers or repairs missing metadata.

The aggregate browser compute interface has three storage bindings (input,
packed output, basis output) and one uniform binding. The copy interface has five
storage bindings (current/previous packed vertices, geometry destination,
current/previous basis) and one uniform binding. Native shading adds no physical
binding for the basis; SceneArena and GeometryArena carry it. The exact producer
resource names and native authored-basis schema define reject older companions.

## Existing opaque normal family

`StandardLitTextureV1` and `AuthoredLitV1` describe the opaque deferred source,
which has a different parameter and emission contract from the forward sources.
Their existing admission remains RGB-only (`NormalMapMode=0`, `HeightMapScale=0`)
and requires mesh tangents. Their Y flip, outward clamp, and independent basis
were already implemented. The sampled-normal and mapped-normal finite guards
now match the canonical snippet as well. This shared sampling-source hash change
requires recooking old authored opaque textured artifacts, including non-normal
artifacts whose emitted WGSL is otherwise unchanged. It does not change existing
material or raw texture bytes or broaden the old family's admission.

The new native basis companion is consumed by all six forward
`AuthoredLitTexturedV1` profiles, including vertex-normal specular surfaces. Existing packed-only material
consumers retain their prior aggregate deformation arithmetic. In particular,
the old aggregate path's direct matrix transform does not establish the
canonical prepass cofactor behavior under nonuniform bone scale. The new
companion does not constitute numerical deformation acceptance for those older
consumers.

## Validation boundary

Narrow Editor, WebGPU, ShaderCooker, and BrowserContentCooker builds and ignored
production-method probes are recorded under the existing lit-surface validation
run, in `scratch/authored-textured` and `logs`. The shader probe cooks all six
receivers and thirty exact companions, plus native schema and old-family
reconciliation witnesses. These checks establish source, schema, cooking, and
runtime-selection behavior only. Physical browser pixels, derivatives on actual
hardware, deformation, MSAA, and full Default/Advanced/custom pipeline acceptance
remain separately controlled rendered gates.
