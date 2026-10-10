# Authored uniform-alpha color coverage

`AuthoredLitV2` is an explicit engine color-coverage contract. It extends the
per-material WGSL cook to the existing opaque, masked, alpha-blended,
premultiplied-alpha and additive color behavior. `AuthoredLitV1`, its opaque
texture maps, and both existing typed texture payload revisions retain their
previous contracts.

## Source and material identity

`CreateAuthoredLitPbrColorCoverageMaterial` authors the existing
`StandardLitColorCoverageForward.fs` program and its seven typed parameters:
base color, opacity, specular, roughness, metallic, emission and alpha cutoff.
Opacity and cutoff must be finite in `[0,1]`; the cutoff parameter must equal
the material property. The effective mode must match the authored forward
render pass. Unsupported texture coverage, custom vertex callbacks, billboard,
pass extensions and Uber overrides are rejected.

The planner emits `opaque-coverage`, `masked`, `alpha-blend`,
`premultiplied-alpha` or `additive` in the per-material source. The generated
schema is `xrengine.engine.authored-lit-color-coverage.v2` with pass
`forward-coverage`. It compiles the exact canonical
`StandardLitColorCoverageLocalShadows.slang` frontend, pinning the directional,
local-light and sampling dependencies. Admission checks the complete physical
vertex, 48-byte material, lighting, AO and shadow ABI, including all fourteen
resource bindings. Arbitrary authored GLSL is not reclassified as generated PBR.

The publisher verifies the unchanged desktop fragment and a closed canonical
seven-snippet dependency graph. Its detached color material preserves the
authored semantic version, transparency, cutoff, render options, render pass
and sort priority through the existing generic binary contract. No new typed
texture carrier, authored-asset migration or raw image rewrite is introduced.
Coverage state setters run before the detached copy borrows source parameters
or render options, so cutoff synchronization cannot transiently modify the
live authored values.

## Color, normal and shadow passes

The shared coverage raster contract requires depth testing with `Lequal`,
depth writes for opaque/masked color and auxiliary passes, and the exact
authored blend factors without depth writes for sorted transparency.
Premultiplied-alpha changes the shaded RGB exactly as the existing canonical
shader does. Sorted modes retain their material sort priority.

Opaque and masked materials own the existing canonical color-coverage
depth-normal, directional depth, radial point-depth and projected spot-depth
variants. Before selecting any auxiliary program, WebGPU verifies the source's
exact generated companion. Every replay reads the same live opacity/cutoff;
numeric updates preserve stable auxiliary ownership. Sorted materials reject
normal and shadow replay. No opaque caster is substituted for masked coverage.

The generated forward program receives the existing bounded directional,
point and spot shadows. The same light capacities, atlas opt-out, storage,
sequential point faces, PCSS settings, output formats and producer readiness
checks remain in force. Cold package audit requires the relevant exact caster
programs when opaque/masked authored materials coexist with casting lights.
At this coverage milestone the opaque authored V1 profile still rejected shadowed worlds. The subsequent [opaque authored-shadow implementation](browser-authored-opaque-shadows-2026-10-03.md) supplies its exact receiver and caster companions; that later source evidence does not establish live shadow pixels.

The OpenGL change recognizes only the new authored identity when publishing
the existing `StandardLitCoverage` uniform for source-owned shadows. Desktop
GLSL, uniform names, layouts and old semantic behavior are unchanged.

The point-light YAML property now declares its actual `InstancedLayered`
default. Without that annotation the ordinary writer omitted enum-zero
`Sequential`, and a fresh load restored the constructor's different default.
The annotation preserves explicit sequential authoring without introducing a
format, reader or rendering change.

`DepthTest.UpdateDepth` likewise declares its actual `true` default. This keeps
an explicitly disabled depth write in newly saved YAML, so sorted materials do
not reload with the constructor's enabled depth write. Old explicit values and
the absent-property default remain readable with their existing meaning.
The same complete render-state comparison exposed omitted no-cull, clockwise
and disabled RGBA-write values. `RenderingParameters` declares the actual
`Back`, `CounterClockwise` and `true` constructor defaults on those six
properties. The YAML omission policy itself is unchanged; these annotations
preserve explicit zero/false values instead of changing them during a cook.

## Validation

- The Editor graph, ShaderCooker and Rendering/WebGPU graph built successfully.
  ShaderCooker and the leaf graph were warning-free. The final Editor run
  observed two nullable warnings in concurrent logical-asset work; that owner
  corrected them and rebuilt Runtime.Core with zero warnings/errors. The clean
  leaf graph also supersedes an earlier incomplete asset-constructor edit
- Five authored modes passed 48 factory/planner and actual YAML save/load checks,
  including exact desktop GLSL/coverage-uniform equality, explicit sequential
  point shadows, the unchanged point default, and every blend/depth-write mode
- All 98 selected canonical/generated recipes cooked; the five generated V2
  WGSL modules are byte-identical to the canonical local-shadow coverage module,
  SHA-256 `4e749f255ea4ad148c6037cd49737a735c9b364c3b008310d066ca51196a127b`
- The existing ignored carrier witness passed 208 checks against frozen baseline
  payloads. Both built-in v1 and authored-texture v2 payloads remain byte-identical
  for fully aliased, partially aliased and distinct image roles, with exact
  production reader roundtrips and image/settings identity
- Genuine `ExportAuthoredWorld` and cooked-world hydration passed 104 checks for
  all five modes with one directional, sequential point and spot shadow light.
  The checks cover all authored factors, opacity/cutoff, sort priority, depth
  usage/write/function, cull/winding, RGBA masks, blend usage/factors/equations,
  alpha-to-coverage, per-buffer state, source-owned normal and three shadow
  variants, identical coverage payloads, live opacity changes, and stable
  auxiliary ownership. Explicit no-cull, clockwise and disabled-color-write
  authoring survives the complete route
- Wrong-pass or incomplete-shadow companions, mismatched cutoff, nonopaque
  texture generation, and sorted normal/shadow replay reject explicitly
- Detached projection and its cleanup leave every source `BindingValueVersion`
  unchanged. The saved authored world remains byte-identical after export,
  SHA-256 `0b52e6d8babefcbeadcd87c5fcd3a3d325bc347c260155c6af931080fb6f2623`

Disposable source, recipes and logs are under
`Build/_AgentValidation/20261001-225000-lit-surface/scratch/authored-coverage/`
and the run's `logs/` directory. Final logs are
`authored-coverage-raster-editor-build.log`, `authored-coverage-raster-author.log`,
`authored-coverage-raster-export.log`, `authored-coverage-raster-compat.log`,
`generic-meshlet-lod-final-build.log`, and `runtime-asset-core-build.log`.
The leaf graph uses ordinary project references and the canonical output tree.

These checks do not qualify physical GPU output. Coverage silhouettes, sorted
blend order, changing cutoff, shadow darkening, point seams, resource retirement
and numerical desktop/browser comparison still need live-device acceptance.
The raster companion alone does not establish native Advanced surface reconstruction.
The separate [native generated PBR contract](browser-native-authored-lit-2026-10-03.md)
now records exact owner-resolved provenance, frozen surface publication and
opaque/masked source checks. Nonopaque texture/opacity-map semantics, arbitrary
GLSL or Uber lowering, and physical GPU acceptance remain open.
