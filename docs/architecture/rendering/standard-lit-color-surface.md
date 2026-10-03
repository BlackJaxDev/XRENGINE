# Standard lit-color surface binding

Opaque authored texture surfaces have a separate
[standard lit-texture contract](standard-lit-texture-surface.md); they do not
broaden the color-only material schemas below.

`XRMaterial` factory methods mark their built-in lit-color output with
`EngineMaterialSemanticIdentity.StandardLitColorV1`. The shared
`StandardLitColorSurfaceBinding` reads that explicit identity and validates the
material's numeric surface schema. Parameter names alone never grant admission.

The supported factory layouts are exact sets of names and runtime types. Order
does not matter; omissions, duplicates, mixed sets, different types, and extra
parameters fail admission.

| Source factory | Render pass | Parameters | Canonical defaults for absent inputs |
| --- | --- | --- | --- |
| `CreateColorMaterialDeferred` | `OpaqueDeferred` | `BaseColor: ShaderVector3`, `Opacity: ShaderFloat`, `Specular: ShaderFloat`, `Roughness: ShaderFloat`, `Metallic: ShaderFloat`, `Emission: ShaderFloat` | Index of refraction = 1 |
| `CreateLitColorMaterial(..., deferred: true)` | `OpaqueDeferred` | `BaseColor: ShaderVector3`, `Opacity: ShaderFloat`, `Specular: ShaderFloat`, `Roughness: ShaderFloat`, `Metallic: ShaderFloat`, `IndexOfRefraction: ShaderFloat` | Emission = 0 |
| `CreateLitColorMaterial(..., deferred: false)` | `OpaqueForward` | `MatColor: ShaderVector4`, `MatSpecularIntensity: ShaderFloat`, `MatShininess: ShaderFloat` | Roughness = 0.9, metallic = 0, emission = 0, index of refraction = 1 |

The forward values reflect `LitColoredForward.fs` and the
`ForwardLighting.glsl` uniform defaults. `MatShininess` is declared but is not
used by the current forward lighting function; it is validated as part of the
factory layout and is **not** converted to roughness. The canonical value is a
numeric input record, not a replacement lighting equation. A renderer must
retain the selected shader/pass lighting behavior.

The binding also rejects surface textures, modern emissive/transmission/normal
extensions, binding publishers or callbacks, non-opaque transparency and custom
pass/uber state. Shader-stage eligibility, fixed-function render state, and
device/pass support are separate renderer decisions. In particular, this
numeric reader can inspect a desktop GLSL factory material without selecting
or replacing its authored shader. A WebGPU variant resolver must independently
verify that a cooked variant is authorized for the material's stage sources.

`TryCreate` validates initial admission. `TryRead` returns current values into
the same typed record and rechecks material eligibility. It caches the schema
by `BindingLayoutVersion` and the numeric record by `BindingValueVersion`,
without per-read managed allocation. Because the public parameter array can be
edited in place without incrementing these revisions, the binding checks its
cached parameter references and names. After an unversioned layout edit, it
revalidates the schema and reads numeric values each call until the layout is
republished through `XRMaterial.Parameters`.

This shared reader does not change the desktop GLSL material path. Its
existence does not certify a lit WebGPU pass, shader artifact, or physical-device
render result.

## Explicit uniform-alpha coverage

`XRMaterial.CreateLitColorCoverageMaterial(color, transparencyMode, alphaCutoff)`
opts into `EngineMaterialSemanticIdentity.StandardLitColorV2`. Existing V1
factories and desktop shader files retain their original behavior. In
particular, V1's CPU-direct colored shaders do not consume alpha cutoff and do
not premultiply their output simply because a blend mode requests it. V2 is an
explicit new contract rather than an inferred reinterpretation of those shaders.

V2 has exactly seven parameters: `BaseColor: ShaderVector3` and the
`ShaderFloat` parameters `Opacity`, `Specular`, `Roughness`, `Metallic`,
`Emission`, and `AlphaCutoff`. The default surface uses specular 1, roughness
0.5, metallic 0, and emission 0. Opacity and cutoff must be finite values in
`[0,1]`; the cutoff parameter must match `XRMaterial.AlphaCutoff`. The existing
material property synchronizes that parameter. Texture alpha is not part of
this uniform-color contract.

Supported effective transparency modes are:

- `Opaque`: depth-writing forward color with blending disabled
- `Masked`: discard when `Opacity < AlphaCutoff`, with depth writes and no blending
- `AlphaBlend`: sorted straight-alpha color, depth testing, and no depth writes
- `PremultipliedAlpha`: sorted color with the complete lit, ambient, and emissive
  RGB multiplied by opacity before the authored `One/OneMinusSrcAlpha` blend
- `Additive`: sorted straight color with the authored `SrcAlpha/One` blend

Cutoff equality survives. Masking is uniform over the material, so it does not
claim textured foliage or spatial opacity coverage. OIT, stochastic coverage,
triangle sorting, alpha-to-coverage, transmission, surface textures and custom
binding/pass extensions require separate contracts and remain rejected.

`CreateAuthoredLitPbrColorCoverageMaterial` exposes the same color behavior as
`AuthoredLitV2` with a per-material generated WGSL companion. It preserves the
canonical desktop coverage GLSL, exact factor/coverage binding, render state,
sort priority and source-owned auxiliary passes. Its generated forward program
uses the complete bounded local-shadow receiver ABI; normal and shadow replay
require the matching canonical coverage programs and revalidate the generated
source companion. The [authored coverage record](../../work/progress/platform/browser-authored-color-coverage-2026-10-03.md)
details source verification, binary compatibility and pending device acceptance.

The factory uses the existing material-construction target to choose an additive
desktop GLSL shader or source-free cooked material. Both outputs use the same
numeric surface and mode-derived `StandardLitCoverage` control. Auxiliary
materials remain owned by the source material's normal/shadow caches and read
that live source. The new desktop shader provides exact depth-normal and
shadow-caster branches; original desktop GLSL files are unchanged.

### WebGPU programs and pass ordering

V2 color uses the `forward-coverage` artifact key, with the existing
`static-position-normal-v1` vertex profile and `linear-hdr-v1`,
`linear-hdr-directional-shadow-v1`, or `linear-hdr-local-shadows-v1` output. One immutable program ABI supports
all admitted modes; raster state remains keyed separately and material
`RenderPass` remains authoritative. The actual engine buckets are
`OpaqueForward`, `MaskedForward`, and `TransparentForward`. The latter retains
the shared far-to-near sorter and authored sort priorities. Transparent color
is blended into the linear HDR scene before bloom and tonemapping.

V2 CPU sort priority is captured at collection time alongside distance and
stable tie-breakers. Lower priorities draw first and higher priorities later;
within one priority, existing far-to-near distance and stable submission order
are unchanged. V1 and arbitrary shader commands retain neutral priority zero,
including mixed V1/V2 transparent lists. This does not alter V1-to-V1 ordering.

Coverage-preserving `depth-normal` and `depth` variants share the exact mask
predicate. The normal replay includes masked geometry in GTAO's depth/normal
producer; transparent geometry does not enter that producer. The existing
shadow command chain includes opaque and masked buckets and excludes sorted
transparency. The WebGPU caster uses a fragment stage for discard even on a
depth-only attachment. Missing coverage variants reject explicitly rather than
substituting a solid opaque caster or prepass.

The WebGPU coverage profile requires normal-Z, single-sample depth-tested
`Lequal` rendering and the matching canonical blend/depth-write state.
Incompatible raster overrides reject rather than being rewritten. Existing
directional-PCSS, light-count, output-resource, and effect restrictions still
apply. The four new Slang recipes add forward color with/without directional
shadows, depth-normal, and shadow depth; package manifests must explicitly
declare each required V2 variant.

Source implementation and artifact cooking do not establish physical-device
acceptance. Runtime/browser validation remains a separate milestone.

### Bounded local-light shadows

The `linear-hdr-local-shadows-v1` receiver retains the shared 4 directional,
8 point and 8 spot direct-light capacities. It admits at most one shadowed light
of each type: the existing standalone directional depth map, one spot projected
R16Float color map, and one point radial R16Float color cube. The local color
maps retain independent raster-depth attachments for nearest-surface selection.
No shadow map or light is silently dropped when these limits are exceeded.

Local shadow lights require depth encoding, authored R16Float storage, PCSS with
8 blocker and 8 filter taps, no contact shadows, an explicit per-light
`UseShadowAtlas = false`,
and map dimensions at most 2048. Point maps are square and require an explicitly
authored `Sequential` shadow render mode. The same light-owned perspective
cameras and shared shadow collection/swap/render paths produce the maps. The
cooked route honors that authored atlas opt-out without changing global atlas
switches or desktop eligibility. All six
point faces refresh together so filter taps crossing a seam cannot observe an
unrefreshed neighbor. Normal-Z is the admitted camera depth convention.
Both local profiles require a finite near distance of at least 0.001 and a
finite radius/range that exceeds it by at least 0.001, avoiding implicit camera
near/far adjustment. Spot outer angles must be strictly between zero and
90 degrees, with a nonnegative inner angle no larger than the outer angle and
a finite positive cone exponent. Invalid authored values reject at cook and
activation admission rather than being clamped by this profile.
The point vertex stage adjusts only framebuffer Y orientation for the shared
cube camera axes. Radial distance remains separate from projected depth.

`OpaquePointShadowDepthV1` uses
`point-shadow-depth/static-position-v1/radial-r16f-v1` and
`OpaqueSpotShadowDepthV1` uses
`spot-shadow-depth/static-position-v1/projected-r16f-v1`. V2 adds the same two
auxiliary profiles and preserves its exact uniform-alpha cutoff. V1's opaque
caster semantics remain unchanged. Sorted transparent surfaces do not cast
these shadows. The receiver ports the existing forward texel-relative normal,
constant/slope bias and PCSS calculations; the spot compares its authored
projected color encoding and the point compares radial distance.

Package preflight checks capacities, authored profiles and required variants;
activation and frame admission recheck the live lights. Required producer passes
must finish before a frame is submitted. Disabled bindings have explicitly
initialized small default textures, but a required unready map defers the frame
instead of substituting one. Each light owns its complete target generation and
retires its material, textures and per-face FBOs when replaced or deactivated.
These source and cooking contracts still require physical WebGPU validation of
shadow shape, moving lights/casters, six-face seams and coverage before runtime
qualification is claimed.
