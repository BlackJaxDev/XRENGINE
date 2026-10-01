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
