# Standard lit-color surface binding

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

The factory uses the existing material-construction target to choose an additive
desktop GLSL shader or source-free cooked material. Both outputs use the same
numeric surface and mode-derived `StandardLitCoverage` control. Auxiliary
materials remain owned by the source material's normal/shadow caches and read
that live source. The new desktop shader provides exact depth-normal and
shadow-caster branches; original desktop GLSL files are unchanged.

### WebGPU programs and pass ordering

V2 color uses the `forward-coverage` artifact key, with the existing
`static-position-normal-v1` vertex profile and either `linear-hdr-v1` or
`linear-hdr-directional-shadow-v1` output. One immutable program ABI supports
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
