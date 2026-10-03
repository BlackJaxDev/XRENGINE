# Native generated PBR surfaces

Advanced WebGPU admits the engine-generated `AuthoredLitV1` color, independent
texture and RGB-normal surfaces, plus `AuthoredLitV2` opaque and masked
uniform-alpha color. The selected material remains the authored material. Its
per-material raster program and semantic version are not replaced with a
built-in material type. Sorted transparency remains a late raster operation and
is rejected explicitly by native opaque admission.

The modeled Uber outline source remains a different contract. It owns additional
parameters and images that the exact generated PBR readers do not admit. An
enabled outline on that same shared source is not evidence of standard-lit
native equivalence; no extra Uber state is silently ignored. Separately owned
raster materials remain separate authored pass submissions.

## Cooked provenance and ownership

`RuntimeEngineMaterialArtifactServices` supplies the actual content owner's
shader resolver to shared material publication. Browser startup installs the
catalog after loading verified shader artifacts and keeps it until scene and
asset publications have retired. Startup rollback and teardown dispose that
lease before dropping the catalog and asset source. Synchronous editor audits
use a thread scope, so inspecting a browser cook does not change another
renderer thread's catalog. A missing thread catalog masks the application
catalog instead of borrowing an unrelated session's programs.

Native admission resolves every retained stage directly through that owner.
An `XRShader.CookedArtifact` object cannot bypass the loaded catalog. All stage
identities must name one complete vertex/fragment program; nonempty source
text, unresolved source paths, custom vertex callbacks and billboard behavior
are rejected. The verified descriptor must identify this material's persistent
ID, its supported semantic version, the exact generated schema/pass, and the
existing complete physical vertex, factors, lighting and sampled-resource ABI.
That ABI check includes exact uniform and sampled binding types, stage visibility,
ownership and update frequency, uniform member sizes and matrix packing, and the
canonical vertex slot, stride and attribute offsets. A 2D AO image cannot be
redeclared as a cube, nor its filtering sampler as a comparison sampler; moving
forward-lighting visibility to the vertex stage also rejects.

The shared cooker/runtime provenance check requires the generated frontend's
complete pinned Slang input graph, one material JSON source and one recipe.
The cooker still verifies all compiler-reported watched files for changes, but
its generated descriptor records the generator's actual active source graph,
rather than unrelated Slang files beside those inputs. All real conditional
includes remain covered: color has no active texture include, textured profiles
include `StandardLitTextureSampling.slang`, and V2 includes the existing local
and directional shadow frontends and local sampling helper.

This is the engine's trusted offline-cooker contract. The descriptor and module
are content-verified, and the cooker checks actual canonical inputs before and
after compilation. Runtime checks do not independently prove arbitrary WGSL
semantics or authenticate a deliberately forged compiler transcript. No signing
system or compiler-output digest whitelist is introduced. Old generated
descriptors with conservative staging-tree dependencies remain valid raster
artifacts; native admission rejects them with a recook diagnostic.

Descriptor/module verification and provenance parsing are cached per loaded
artifact, using weak ownership. Subsequent publication reads use the exact
typed surface readers and bounded stage/role checks without allocating. Live
parameter, role, identity and source changes are checked again; cached proof
does not exempt the live material from those checks.

`ShaderArtifact` defensively copies constructor input into privately owned storage.
Its `Bytes` accessor exposes an allocation-free `ReadOnlySpan<byte>`, with no
writable array returned through ordinary payload access. The legacy Vulkan
`SpirV` array accessor returns an independent copy for its cold compile/cache
callers. The catalog revalidates each descriptor against this owned payload on
ingestion; changing input arrays or an explicitly requested output copy cannot
invalidate an already cached native proof. This changes in-memory ownership,
not artifact formats or shader bytes, and adds no hashing or cloning to warmed
material admission.

## Frozen native execution

A successfully verified source publishes `EngineGeneratedSurface` in the
canonical material header and a generation-checked engine-surface companion.
The companion retains raw base color, opacity, roughness, metallic, specular and
emission, together with independent base-color, normal, metallic and roughness
bindings. Existing UV0, identity transform, red scalar channel and RGB-normal
requirements remain unchanged. No packed metallic/roughness alias replaces the
independent authored maps.

Opaque and masked color use the same companion. The exact cutoff and uniform
opacity are retained in the existing canonical constant row used by visibility;
the typed V2 reader requires its cutoff parameter and material property to agree.
The existing built-in `StandardLitColorV2` masked surface now uses that same
native path. Sorted built-in and authored color modes both reject native opaque
selection.

Visibility and shading validate the frozen source and companion generation.
Reconstruction, normal mapping, direct/indirect PBR and surface exports select
the existing engine evaluator for the verified generated source. No backend
fallback is introduced. The engine-surface shader contract is now schema 2;
the 304-byte row layout is unchanged. All eight native shading/export recipes
declare that version, and old native shader caches fail with an explicit recook
diagnostic before binding.

Native raster admission now checks counter-clockwise winding, enabled Lequal
depth testing with writes, all RGBA writes, disabled blending, inactive stencil,
disabled alpha-to-coverage, and no/back-face culling. Unsupported state gets a
property-specific cold diagnostic. Each draw retains the corresponding admission
bits in the existing render-state record's high flags byte; the low 24 GPU draw
bits and 32-byte record layout are unchanged. Existing desktop shader accessors
only declare/load that record, and no desktop shader consumes its flags. The
captured flags and cull mode participate in structural publication identity,
including edits made directly to a command's render-options override. Native
visibility checks the retained draw/state relation before selecting a bucket.
A cull override that disagrees with the prepared visibility payload rejects as
`RasterCullMismatch`; it is never replaced with the material's cull mode.
The global blend option takes precedence over per-buffer options, matching the
render-options contract: explicit global disable admits even when shadowed
per-buffer values enable blending. Global enabled or unchanged state still
rejects; per-buffer options are inspected only when no global option exists.

The existing generic material serialization and both typed texture carrier
versions are unchanged. No authored data migration, raw texture rewrite,
desktop GLSL change, or staged-index change is part of this work.

## Validation boundary

The source group passed these checks before the separate authored-decal shader
extension was integrated:

- ShaderCooker, the Editor graph, and the WebGPU graph built with zero warnings
  and errors. The Browser `Compile` target also passed with zero warnings/errors;
  this checks managed browser compilation, not final native linking
- Sixteen focused recipes cooked: all eight native/export/depth/MSAA programs
  and eight generated authored programs. The subsequent complete fixture cook
  packaged 105 recipes. Every generated module remained byte-identical to the
  corresponding existing canonical raster module
- The ignored production-method probe passed 305 checks: real detached material
  projection, exact factors/maps/normal roles, old descriptor raster readability
  with native recook rejection, missing/wrong closure/schema/name/pass/physical
  ABI rejection, ordinary module/hash mismatch rejection, and direct-artifact
  attempts to bypass the loaded owner
- Actual material binary hydration preserved all eight generated source
  identities, including the unchanged authored texture carrier. Both existing
  texture-carrier writers/readers were left untouched. Frozen
  publication retained factors and cutoff after live edits and rejected missing
  or stale generation companions. One thousand warmed native reads allocated
  zero bytes
- Raster cases cover winding, front culling, disabled/changed depth tests and
  depth writes, every disabled color channel, alpha-to-coverage, stencil, and
  global/per-buffer blending. A real `GPUScene` publication retained a command
  override over the material default; changing that override updated the next
  structural publication while a leased previous publication stayed unchanged
- A separate 284-check run called the real `ExportAuthoredWorld`, then hydrated
  its saved Advanced world. All eight original materials and the exact authored
  `AdvancedRenderPipeline` survived: five opaque/masked native surfaces and
  three sorted late-raster materials. The authored world bytes stayed unchanged
  during export
- `git diff --check` passed. The staged index remained 59 files with tree
  `eb0ef0069abfd6c4859027d302de7db6289081cb`

Disposable evidence is under
`Build/_AgentValidation/20261001-225000-lit-surface/scratch/native-authored/`.
The run's `logs/` directory contains `native-authored-cooker-build.log`,
`native-authored-cook.log`, `native-authored-full-cook.log`,
`native-authored-editor-build.log`, `native-authored-webgpu-build.log`,
`native-authored-browser-compile.log`, `native-authored-probe.log` and
`native-authored-export-probe.log`. An initial broad Browser build used an
unsupported `WasmBuildNative=false` combination; the successful targeted
`Compile` run used the installed native prerequisites and did not link a bundle.

The subsequent independent review found and closed three source defects: loose
resource type/visibility matching, a writable module array exposed after catalog
verification, and rejection of shadowed per-buffer blend settings. The expanded
ignored production probe passed 718 checks, including every canonical resource's
visibility and sampled type, vertex slot/stride/offset negatives, constructor
input mutation, mutation of a requested byte copy, and global blend precedence
through cold admission and retained publication. The review's original indexed
module mutation now fails compilation with `CS8331`. Both 1,000 warmed native
reads and 1,000 byte-span accesses allocate zero bytes.

After those fixes, ShaderCooker, the Editor graph and the WebGPU graph again built with zero
warnings/errors. Fresh focused and full cooks packaged 16 and 105 recipes,
including the separately owned decal shader schema; the eight generated raster
modules still match their canonical companions byte for byte. The expanded real
world-export/hydration run passed 706 checks. Its artifacts, probe source and
mutation-compile evidence are retained under the same scratch directory, and
the build/cook/probe logs use the `native-authored-review-` prefix. The earlier
Browser managed compilation is not final-snapshot evidence for these later
ownership changes.

These checks do not establish physical GPU acceptance. Device execution, masked
silhouettes, numerical lighting parity, dynamic map edits, resource retirement
and arbitrary authored shader families remain separate acceptance work.
The subsequent [generated opaque shadow companion
route](browser-authored-opaque-shadows-2026-10-03.md) enables exact V1 receiving
and casting without changing this native material proof or either carrier.
A combined native PBR/Uber-outline scene has not received GPU acceptance.
