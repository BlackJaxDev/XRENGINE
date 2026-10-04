# Ordinary engine unlit materials in WebGPU

The five canonical forward fragments now have distinct cooked WebGPU receivers: color, texture, forced-opaque texture, alpha-cutoff texture, and array slice zero. Desktop GLSL sources and desktop factory selection are unchanged. Browser projection retains the exact per-material `mat-{ID}` companion for an authored canonical fragment. A source-free `WebGpuCooked` factory material instead selects one declared, content-verified built-in variant by unlit semantic version and forward input/output profile. The raster path and Advanced native admission use that same catalog contract; neither compiles a source or substitutes lit shading at runtime. The installed artifact and variant views remain paired while a streamed catalog publishes new immutable snapshots.

The browser target carrier retains borrowed ShaderVar and image references through the shared reflection graph and a versioned texture profile. It restores omitted sampler/import and array-owned mip/LOD settings after hydration, then verifies the decoded material. Projection release destroys only the per-material shader that it owns. Source-free factory carriers retain zero shaders. Native encoding reuses a weakly retained per-material inferred Texture0 role when no explicit surface binding exists and refreshes it when the texture, delegated wrap, or sampled format changes. The five fragment operations preserve raw finite RGBA/alpha, array layer zero, and the authored linear/sRGB interpretation. A V4 material is native-opaque eligible only when explicitly masked; the factory's default transparent-forward pass uses late raster. Masked V4 supplies normal and directional/point/spot shadow companions; sorted transparency uses the existing order-gate variant. Unsupported weighted OIT, PPLL, and depth-peel modes still reject by name.

The focused five-recipe cook packaged five artifacts with five declared forward variants. It was composed with the previously qualified companion/pipeline catalog, producing 152 artifacts, 54 material variants, 68 pipelines, and four compute artifacts with no duplicate variant key. On the exact `default-unlit-integrated-0654` source image, the narrow probe passed 81 checks on actual `WebGpuCooked` factory objects. It invoked `WebGpuMaterial.ResolveUnlitArtifact` for all five, checked native admission and qualifying native source encoding, rejected wrong variant keys and an absent variant catalog, proved admission appears and retires with one streamed shader/variant catalog publication, and checked profile-bearing zero-shader projection/hydration with direct and array-layer texture aliases. A real `ProjectBuilder.Prepare`/`ExportAuthoredWorld` pass then preserved source YAML and exported five zero-shader carriers; a separate Published process installed the export's original hash-verified AOT metadata before loading the cooked startup world, passing 12 fresh-hydration checks. These logs and the checked evidence manifest are under `Build/_AgentValidation/20261001-225000-lit-surface/scratch/unlit-builtin-fixture/`. The unchanged authored per-GUID carrier has its earlier genuine projection, export, and original-metadata fresh-hydration proof, including shared parameter aliases, under `scratch/unlit-production-fixture/` in the same run.

The exact integrated Rendering, WebGPU, and Editor managed builds and the native Browser build passed with zero warnings and errors. The native engine-surface companion schema is version 6; native shader artifacts with the preceding version-5 witness require recooking. The general shader-artifact descriptor remains schema 3. Old unpublished custom Unlit payloads also require recooking. The target-only carrier does not change authored YAML or global texture storage. Existing YAML reload can lose constructor-time import color-space metadata and alter source alias topology; projection preserves the actual reloaded graph. The nested array image codec retains its existing reference-scope limit. Generic/Default x4 color and normal targets use matching color/depth sample counts, while unlit shadow replays remain single-sample. Managed/native compilation and cooking establish source and ABI compatibility, not rendered GPU pixel parity. Device-level color, texture sampling, cutoff, shadow, ordering, and x4 output parity remain separate acceptance work.

## Package and browser catalog admission

The canonical catalog now contains 93 material variants, including 17 Unlit
keys. Its separate package-builder and JavaScript manifest allowlists previously
rejected every Unlit key, so acceptance of a selected older material catalog did
not establish acceptance of the complete cook. Both consumers now admit the
same bounded Unlit versions and profiles as `EngineMaterialVariantKey`: color
uses the static position/normal layout, texture versions use position/normal/UV,
all five versions have forward and depth-normal variants, forced-opaque V3 has
no authored-order variant, and only alpha-cutoff V4 has coverage-aware shadow
casters. Unknown semantics, unsupported versions and profiles, and existing
descriptor identity/declaration checks remain enforced. No wire schema,
serialization, payload bytes, or output format changes are introduced.

All five canonical sky declarations and runtime sky identities remain semantic
version 1. Newer sky descriptor or uniform contracts do not imply semantic
version 2; both consumers continue to reject that unsupported semantic revision.

A bounded witness invoked the actual JavaScript manifest validator using all
93 selector declarations extracted from the 172 canonical source recipes. The
old consumer admitted 76 selectors and rejected the full catalog at Unlit; the
updated consumer admitted all 93 and returned the same validated result for the
older 76-selector selection. A cross-product of Unlit versions, passes, vertex
profiles and outputs admitted exactly the 17 declared keys and rejected 1,963
nearby combinations. Twelve additional unknown-semantic, target, descriptor
identity/hash, duplicate, malformed-profile, and sky-version cases rejected.
JavaScript syntax and diff whitespace checks passed. The isolated pre-change
package-builder build passed with zero warnings and errors, but an executor
reset removed the completed cook output and approved local SDK before the
updated C# consumer and cooked-byte package/browser witness could run. Those
checks remain pending on the exact updated source. This source-selector
admission evidence is not a cooked-payload integration or rendered GPU result.
The disposable witness and report are under the existing investigation run's
`scratch/catalog-admission/` directory.
