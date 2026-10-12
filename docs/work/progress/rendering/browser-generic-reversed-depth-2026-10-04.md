# Browser generic reversed depth

The WebGPU authored raster path now lowers logical depth comparisons using the
selected immutable camera view. CPU-direct, resident indexed-indirect, and
compute-meshlet submission use the same lowering; reversed scene cameras no
longer fail the generic view admission check. Existing output, shader, geometry,
submission, deformation, stereo, and material limits still apply.

## Camera and auxiliary contract

- Authored `Less`/`Lequal` and `Greater`/`Gequal` comparisons retain their logical
  values in materials and map only at the WebGPU backend. Equal, NotEqual,
  Always, and Never retain their meaning. Draw cache identities already include
  the complete physical raster state.
- Browser depth-state commands and default clears prefer the backend's explicit
  frozen view, then the matching captured execution view, including a scoped
  shadow view. This also covers callbacks for a custom resident indexed request
  whose selected camera differs from the ambient scene camera. Explicit
  pass-local clear values remain authoritative. Desktop depth-state behavior
  and shader bindings are unchanged.
- Existing camera projection policy already emits reversed zero-to-one depth.
  CPU browser frustum tests and generic GPU indirect/meshlet clip tests cover
  `0 <= z <= w`; reversing the depth axis does not change that volume. LOD
  projection uses the unchanged horizontal and vertical scale.
- All five WebGPU sky companions reconstruct their ray and emit their raster
  position at the frozen far depth. Their view uniform is 144 bytes with
  `CameraDepthRange` at byte 128, and their semantic schema is
  `xrengine.engine.skybox.v2`. Runtime validation rejects the previous ABI.
- Default GTAO matrices/depth mode and the same-sample x4 depth/normal resolve
  use the captured scene view. The resolve preserves the winning sample's
  encoded normal and chooses the nearer depth in either direction.
- Advanced depth of field linearizes reversed depth and preserves near-blur
  selection. `DepthMode` occupies byte 76 in the existing 80-byte block;
  `xrengine.engine.depth-of-field.v2` distinguishes it from stale cooks. Focus
  thresholds, near/far distances, and fog thresholds use the captured camera.
  Existing desktop settings calls retain their previous behavior. Fog retains
  authored virtual overrides; the base implementation receives its captured
  view through a synchronous scope that restores on return or exception.
- Generic/native decals reconstruct world-space surfaces. Motion blur uses
  absolute depth differences. The authored Uber outline offset remains a raw
  clip-space offset, matching its canonical desktop source.

Scene reversal does not reverse shadow maps. The existing directional, spot,
and point receiver profiles require their normal-depth shadow cameras, with
their existing comparisons, bias direction, and neutral clears. Their
publication receipts already include projection and depth-convention identity.
No CPU visibility/count readback or strategy fallback was added. The separate
focused browser diagnostic Hi-Z pipeline retains its own normal-depth profile.

## Evidence and remaining acceptance

The implementation was checked against the production camera projection,
frustum/cull, shader-ABI, auxiliary-pass, shadow-publication, and raster-cache
sources. All six changed sky/depth-of-field recipes cooked successfully with
Slang 2026.8, including reflected layout validation: sky has the 144-byte view
block and depth of field has its depth-mode member at byte 76. The generated
WGSL retains the frozen far-depth and depth-direction branches.

The coordinated source tree `390a21595accad2b5fee4dc785880e692f00c9c5`
passed Rendering/WebGPU compilation with zero warnings or errors (49.20 s) and
Editor compilation with zero warnings or errors (21.11 s). The Editor build's
180 reused dependency assemblies were admitted only after verifying unchanged
project-tree identities. Native Browser compilation also passed with zero
warnings or errors (1 min 51.77 s) on the same exact source tree.

A bounded production witness called the actual compiled managed methods and
passed 33 checks. It covers both comparison conventions; preservation of
authored logical comparisons; an explicit frozen view overriding a different
ambient camera for callback depth state and clear; nested normal-depth shadow
and outer-view restoration; and custom fog virtual dispatch, base thresholds,
nested scopes, ordinary callback restoration, and exception restoration. Its
loaded Rendering and WebGPU assemblies were checked against the exact compiled
image. The witness performed no GPU execution or pixel comparisons.

Disposable evidence is under the existing validation run's
`scratch/generic-reversed-depth-0656/`: `shader-cook.log`, `cooked/`,
`probe-build.log`, `probe-run.log`, and `evidence.sha256`. No committed tests or
desktop GLSL changes are part of this change.

These source checks do not establish physical browser parity. Normal/reversed
known-value color, depth, sky occlusion/ray direction, GTAO, depth-of-field/fog,
shadow independence, one/four samples, all three submission strategies, and
camera/resource-generation transitions still require rendered acceptance.
