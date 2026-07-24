# Poiyomi Toon 9.3 To Uber Shader Full Conversion And Authoring-Parity TODO

- Last Updated: 2026-07-23
- Owner: Assets / Rendering / Animation / Editor
- Status: In Progress - Phase 1 complete
- Target: Poiyomi Toon 9.3.64 free Toon shader, Unity Built-In Render Pipeline

Primary source:

- [Poiyomi Toon 9.3 shader](https://raw.githubusercontent.com/poiyomi/PoiyomiToonShader/refs/heads/master/_PoiyomiShaders/Shaders/9.3/Toon/Poiyomi%20Toon.shader)
- [Poiyomi-embedded ThryEditor snapshot](https://github.com/poiyomi/PoiyomiToonShader/tree/master/_PoiyomiShaders/Scripts/ThryEditor)
- [Upstream ThryEditor](https://github.com/Thryrallo/ThryEditor)
- [ThryEditor feature overview](https://thryeditor.thryrallo.de/)
- [Poiyomi feature overview](https://www.poiyomi.com/)
- [Poiyomi 9.3 locking and animation](https://www.poiyomi.com/9.3/general/locking)
- [Poiyomi 9.3 rendering](https://www.poiyomi.com/9.3/rendering/)
- [Poiyomi 9.3 outlines](https://www.poiyomi.com/9.3/outlines/)

Related XRENGINE implementation:

- [UnityMaterialImporter.cs](../../../../XRENGINE/Scene/Importers/UnityMaterialImporter.cs)
- [UberShader.frag](../../../../Build/CommonAssets/Shaders/Uber/UberShader.frag)
- [UberShader.vert](../../../../Build/CommonAssets/Shaders/Uber/UberShader.vert)
- [Uber shader manifest](../../../../Build/CommonAssets/Shaders/Uber/uniforms.glsl)
- [UberShaderVariantBuilder.cs](../../../../XREngine.Runtime.Rendering/Resources/Shaders/UberShaderVariantBuilder.cs)
- [ShaderUiManifest.cs](../../../../XREngine.Runtime.Rendering/Resources/Shaders/ShaderUiManifest.cs)
- [XRMaterial.Uber.cs](../../../../XREngine.Runtime.Rendering/Objects/Materials/XRMaterial.Uber.cs)
- [XRMaterialInspector.Uber.cs](../../../../XREngine.Editor/AssetEditors/XRMaterialInspector.Uber.cs)
- [UnityAnimImporter.cs](../../../../XREngine.Animation/Importers/UnityAnimImporter.cs)
- [Unity conversion integrations](../../../developer-guides/assets/unity-conversion-integrations.md)
- [Uber shader materials](../../../developer-guides/rendering/uber-shader-materials.md)
- [Uber shader varianting](../../../architecture/rendering/uber-shader-varianting.md)
- [Uber shader variant builder remaining TODOs](../uber-shader-variant-builder-todo.md)

## Goal

Convert Unity materials authored for the Poiyomi Toon 9.3.64 free Toon shader
into XRENGINE uber materials without silently losing visible material behavior,
render state, pass participation, texture interpretation, or animation.

The completed converter must:

- Produce a visually equivalent XRENGINE material where the engine has an
  equivalent rendering concept.
- Produce a documented native equivalent where Unity, VRChat, or a third-party
  integration cannot be reproduced literally.
- Preserve unsupported source values for inspection and future reconversion.
- Emit specific conversion diagnostics for every material-visible feature that
  cannot be reproduced.
- Generate only the shader features and passes required by the source material.
- Work on OpenGL and Vulkan, with correct mono, OpenVR, and OpenXR permutations.
- Preserve material animation bindings across unlocked, locked, and renamed-
  animated Poiyomi materials.
- Provide native ImGui authoring equivalents for every ThryEditor behavior,
  drawer, decorator, metadata option, and workflow exercised by the pinned
  Poiyomi Toon shader.

## Definition Of Full Conversion

"Full conversion" applies to runtime-visible behavior and material-authoring
behavior exposed by the targeted Poiyomi shader. It does not require running
Poiyomi's ShaderLab/HLSL or ThryEditor's Unity C# assemblies directly; it does
require native XRENGINE equivalents for the ThryEditor functionality on which
the shader's authoring experience depends.

Every source property or feature must end in one of these explicit states:

1. **Exact** - equivalent math, textures, state, pass behavior, and animation.
2. **Native equivalent** - mapped to an XRENGINE facility with documented
   semantic differences.
3. **Preserved but inactive** - retained in conversion metadata and reported as
   unsupported because the required engine service or integration is absent.
4. **Editor-only/internal** - intentionally ignored because it does not affect
   runtime output; the catalog records why.

Completion requires no unclassified source properties and no silent fallback
from an explicitly authored GPU feature to unrelated CPU behavior.

Every active ThryEditor annotation and reachable authoring workflow used by the
pinned shader must likewise be classified as exact, native equivalent,
preserved inactive with a reason, or demonstrably unreachable. Merely drawing
an annotated value with the generic property editor is not parity when the
annotation defines coupled values, validation, generation, actions, hierarchy,
or a specialized editing workflow.

## Non-Goals

- Poiyomi Pro features not present in the requested free Toon shader.
- Automatic compatibility with Poiyomi releases before or after 9.3.64 until a
  version catalog and migration entry are added for them.
- Pixel-identical reproduction of ThryEditor's Unity IMGUI layout or execution
  of arbitrary Unity editor code. XRENGINE will provide native ImGui workflows
  with equivalent capabilities and semantics.
- ThryEditor integrations not exercised by the pinned shader and unrelated to
  editing converted materials, such as VRChat SDK installation and Discord
  Rich Presence. The usage inventory must still distinguish these from missing
  required functionality.
- Running VRChat SDK services inside XRENGINE.
- Byte-identical pixels where Unity lighting, reflection probes, lightmaps, or
  platform services have no identical XRENGINE representation.
- Copying the monolithic Unity shader architecture into one XRENGINE shader.
- Hiding missing integrations behind unconditional approximations.

## Existing Baseline

The repository already has useful foundations:

- [x] Unity YAML material parsing for names, shader references, custom render
  queue, texture environments, floats, integers, and colors.
- [x] Poiyomi Toon 9.3 detection and a best-effort uber material converter.
- [x] Annotation-driven uber features, static/animated property modes, variant
  hashing, source caching, and compile-time dead-feature removal.
- [x] Basic base color, normal, alpha-mask, color-adjustment, stylized-lighting,
  AO, shadow-mask, emission, matcap, rim, detail, specular, backface, glitter,
  flipbook, subsurface, dissolve, and parallax parameters.
- [x] Forward and Forward+ lighting, engine shadow passes, SSAO, weighted OIT,
  PPLL, depth peeling, OpenGL/Vulkan backends, and VR vertex permutations.
- [x] Runtime texture types for 2D arrays and cubemaps.
- [x] Material state primitives for blending, depth, culling, color masks, and
  stencil.
- [x] A manifest-driven ImGui uber inspector with categories, feature toggles,
  dependency/conflict feedback, variant telemetry, tooltips, basic sampler and
  scalar/vector/color/range/enum controls, static/animated mode switching, and
  basic property copy, paste, reset, and animation-path actions.

The baseline is not parity. Known correctness failures include:

- [ ] Import `_ToonRamp`; the shader samples it but the converter never binds
  the source texture.
- [ ] Forward real UV1, UV2, and UV3 varyings instead of copying UV0 into all
  four fragment UV slots.
- [ ] Implement the outline feature as an actual inverse-hull draw pass; the
  current feature only exposes parameters.
- [ ] Import Poiyomi `_FlipbookTexArray` as a texture array instead of looking
  for a 2D `_FlipbookTexture` sprite sheet.
- [ ] Translate dissolve mode values rather than copying incompatible enum
  values directly.
- [ ] Stop collapsing distinct shade, rim, metallic, smoothness, and dissolve
  textures into semantically different uber samplers.
- [ ] Preserve texture color space, normal/data classification, wrap, filter,
  mip, anisotropy, and alpha import settings from Unity metadata.
- [ ] Preserve complete blend, depth, stencil, queue, offset, fog, and pass
  state instead of reducing the material to cull/cutoff/transparency mode.
- [ ] Parse shader keywords, disabled passes, string tags, lock metadata, and
  material animation bindings.
- [ ] Replace silent unsupported-property drops with a structured report.
- [ ] Replace the flat/basic uber inspector model with a complete data-driven
  ThryEditor-equivalent hierarchy, condition/action model, specialized control
  set, and material-authoring workflow. The current inspector is a useful
  baseline but is not ThryEditor parity.

## ThryEditor Parity Gap Summary

This is the current architectural comparison. Phase 0 must replace preliminary
source observations with a generated report from the pinned commit.

| Capability | Current uber ImGui state | Required parity state |
| --- | --- | --- |
| Hierarchy | Flat manifest categories and property rows | Ordered nested master/subsections, decorators, references, persistent/default expansion, and simple/advanced modes |
| Basic values | Scalar, vector/color, range, enum, toggle, texture, and generic controls | Complete built-in annotations plus consistent mixed/default/animation behavior |
| Visibility and enablement | Feature dependencies/conflicts | Parsed `condition_showS`, legacy show, enable, enable-children, expression dependencies, and diagnostics |
| Declarative actions | Basic local context commands | Typed on-value/click/alt-click action graphs for coupled properties, tags, shaders, render presets, URLs, and allowlisted tools |
| Specialized drawers | No complete Thry/Poiyomi registry | Wide enums, multi-sliders, vector variants, button vectors, multi-float buttons, masks, texture layouts, gradients, curves, arrays, and all other active annotations |
| Discovery and help | Category layout and basic tooltips | Localized/raw search, ancestor reveal, rich labels, warnings/help boxes, author/help actions, and exact-property navigation |
| Texture authoring | Texture assignment and sampler editing | RGBA packer, gradients, curves/ramps, array construction, previews, recipes, deterministic generation, and texture-use lookup |
| Presets and clipboard | Property copy/paste/reset | Material/section/property presets, preview/revert, Paste Special, recursive references, semantic cross-shader application, and versioned payloads |
| Multi-material workflows | No Thry-equivalent cross editor or linker | Mixed-value multi-edit, heterogeneous semantic editing, batch reports, and persistent cycle-safe material links |
| Decal authoring | Parameters only | Scene raycast, viewport gizmos, slot-aware transforms, preview, cancel, and undo |
| Localization and notes | No Thry-equivalent locale/note system | Imported locale catalogs/fallback, translated search, local overrides, and searchable property notes |
| Locking and animation | Variant status, static/animated switching, and animation-path copy | Native optimize/prepare manager, batch/prewarm state, automatic animation marking, renamed bindings, keyframe actions, and do-not-* policy |
| Utilities | Limited bulk feature actions | Material cleanup, texture-use finder, translation preview, unprepared-material manager, and safe registered external tools |
| Safety and persistence | Existing material mutations, to be audited | Unified atomic undo/redo, dirty/save, generated assets, local override/reimport, schema migration, path/URL/command policy, and bounded caches |

## Target Architecture

Do not continue expanding `UnityMaterialImporter` into a shader-specific
monolith. Split responsibilities into focused types, each in its own file:

- `UnityMaterialDocument` - lossless Unity material representation.
- `UnityTextureImportDocument` - relevant `.meta` texture settings.
- `PoiyomiShaderVersion` - recognized source version and compatibility range.
- `PoiyomiPropertyCatalog` - source property definitions, types, defaults,
  aliases, animation rules, and runtime/editor classification.
- `PoiyomiMaterialDescriptor` - normalized, versioned Poiyomi semantics.
- `PoiyomiMaterialConverter` - descriptor-to-uber conversion coordinator.
- `PoiyomiRenderStateConverter` - pass and fixed-function state translation.
- `PoiyomiAnimationBindingConverter` - Unity curve-to-uber binding translation.
- `PoiyomiInspectorSchemaImporter` - converts active Thry/Poiyomi annotations
  into a versioned, engine-native authoring schema.
- `ShaderAuthoringSchema` - backend-independent hierarchy, labels, conditions,
  actions, references, widgets, defaults, and localization identities consumed
  by ImGui rather than hard-coded Poiyomi inspector branches.
- `MaterialInspectorConditionGraph` - compiled, dependency-tracked condition
  expressions with cycle detection and cached evaluation.
- `MaterialInspectorActionGraph` - typed, allowlisted, transactional actions
  for coupled property, tag, render-state, shader, URL, and editor operations.
- `MaterialConversionReport` - deterministic diagnostics and preserved values.
- Focused feature mappers organized by responsibility rather than one mapper
  containing thousands of property aliases.

The intended data flow is:

```text
Unity .mat + shader identity + texture .meta + animation clips
    -> lossless Unity documents
    -> versioned Poiyomi descriptor
    -> uber authored state + pass set + animation remap
    -> compiled variants + conversion report
```

The Poiyomi descriptor must remain independent of GLSL uniform names. This
keeps source parsing, semantic conversion, and shader binding independently
testable.

## Cross-Cutting Implementation Rules

- [ ] Keep every optional feature compile-time removable through the existing
  uber variant system.
- [ ] Keep per-frame material binding and render-submission paths allocation-
  free after initial preparation.
- [ ] Share authored state across base, outline, shadow, depth, velocity, and
  other generated passes; do not clone independent drifting materials.
- [ ] Preserve source values even when a runtime adapter is unavailable.
- [ ] Make fallback behavior explicit in the report and editor inspector.
- [ ] Use semantic texture defaults: white, black, flat-normal, neutral data, or
  identity lookup as required by each source slot.
- [ ] Treat sRGB color textures, linear data textures, normals, height fields,
  masks, arrays, and cubemaps as distinct roles.
- [ ] Avoid combinatorial source duplication for repeated slots. Generate or
  parameterize decal, matcap, emission, and rim families from shared contracts.
- [ ] Audit shader sampler/descriptor limits on every supported backend before
  selecting a binding strategy.
- [ ] Preserve deterministic property order, variant hashes, reports, and test
  fixture output.
- [ ] Keep inspector expression parsing, dependency discovery, label parsing,
  and schema construction out of per-frame ImGui drawing; cache compiled data
  and invalidate it only when relevant authored values change.
- [ ] Route every inspector mutation, preset, generated texture, link update,
  and multi-material operation through undo/redo, dirty-state, asset-save, and
  deterministic reimport contracts.
- [ ] Treat shader-supplied URLs, editor commands, output paths, and remote
  content as untrusted data; allowlist commands, validate paths, and require an
  explicit policy for network access.
- [ ] Add XML documentation to new C# public types and non-obvious members.
- [ ] Preserve the Poiyomi MIT license and attribution for any adapted source
  code, algorithms, or vendored fixtures.

## Delivery Gates And Sequencing

Use the phases as dependency gates, not as permission to defer tests until the
end:

1. **Conversion foundation** - Phases 0-4 establish the pinned source catalog,
   lossless input model, corrected baseline, pass architecture, and binding
   limits. Do not expand feature aliases before these contracts are stable.
2. **Portable visual parity** - Phases 5-8 implement material-visible behavior
   that can operate from ordinary mesh, texture, camera, and lighting inputs.
3. **Runtime and authoring parity** - Phases 9-12 add engine services, material
   animations, locking semantics, ThryEditor-equivalent ImGui authoring,
   reporting, and reimport behavior.
4. **Proof and maintenance** - Phases 13-14 close the corpus, cross-backend,
   performance, documentation, licensing, and upstream-version gates.

Add focused tests after each feature becomes functionally correct. Keep phase
evidence in `docs/work/progress/rendering/` when implementation begins; do not
mark a task complete without a source change, test, capture, report, or explicit
design decision that proves it.

## Phase 0 - Pin The Target And Establish The Parity Contract

- [x] Record the exact Poiyomi repository commit containing 9.3.64 rather than
  relying on the mutable `master` URL.
- [x] Pin the exact Poiyomi-embedded ThryEditor source snapshot used by that
  commit; do not define parity against a drifting upstream ThryEditor branch.
- [x] Save the shader version, source commit, source URL, and license identity
  in a machine-readable conversion catalog.
- [x] Decide whether locked/generated Poiyomi shader variants are identified by
  embedded source markers, GUID aliases, property signatures, or all three.
- [x] Build a deterministic source-inventory report for properties, declared
  textures, keywords, passes, blend/depth/stencil state, and shader features.
- [x] Parse the active ShaderLab property block and generate an exact inventory
  of every Thry/Poiyomi drawer, decorator, display-string option, condition,
  action, property reference, section marker, and localization key.
- [x] Distinguish active annotations from commented examples, dead properties,
  and annotations reachable only in other Poiyomi shaders; raw text-match
  counts are not sufficient evidence.
- [x] Inventory the embedded ThryEditor implementation behind each used
  annotation and every material-level workflow reachable from the Poiyomi
  inspector, including context menus and auxiliary windows.
- [x] Record a coverage row for each active annotation and reachable workflow:
  source location, semantics, current XRENGINE support, target native behavior,
  owner, tests, and exact/native/inactive/unreachable classification.
- [x] Classify every property as runtime, render state, animation/locking,
  integration, inspector-only, compatibility alias, or internal data.
- [x] Give every runtime property an initial parity state: exact, native
  equivalent, preserved inactive, or missing.
- [x] Define version matching rules that reject or warn on unknown Poiyomi
  versions rather than applying a possibly incorrect mapping.
- [x] Define conversion diagnostic codes and severities.
- [x] Define deterministic naming for generated materials, pass variants,
  preserved source metadata, and animation bindings.
- [x] Select redistributable material and texture fixtures and record their
  licenses before adding them to the repository.

Acceptance criteria:

- [x] A generated catalog accounts for every 9.3.64 shader property and pass.
- [x] A generated UI-usage catalog accounts for every active Thry/Poiyomi
  annotation and every reachable ThryEditor workflow used by this shader.
- [x] Re-running the inventory against unchanged source produces no diff.
- [x] Unknown versions and unclassified runtime values produce actionable
  diagnostics.

## Phase 1 - Lossless Unity Material And Asset Ingestion

- [x] Move general Unity material parsing out of the Poiyomi conversion path
  where necessary so other Unity shader converters can reuse it.
- [x] Parse and preserve `m_Shader`, `m_CustomRenderQueue`, all saved texture,
  float, integer, color, and vector values.
- [x] Parse valid and invalid shader-keyword collections used by applicable
  Unity material serialization versions.
- [x] Parse disabled shader passes and override tags.
- [x] Parse string properties and other serialized values required by Poiyomi
  locking or generated shaders.
- [x] Resolve the shader GUID to source/path metadata before choosing a shader-
  specific converter.
- [x] Recognize unlocked Poiyomi Toon 9.3 materials.
- [x] Recognize locked Poiyomi materials without depending only on the original
  shader path.
- [x] Recognize renamed-animated properties and retain their original binding
  identity.
- [x] Parse Unity texture `.meta` settings required for faithful sampling:
  - [x] sRGB versus linear data.
  - [x] normal-map type and channel interpretation.
  - [x] alpha source and alpha-is-transparency.
  - [x] wrap U/V/W.
  - [x] point, bilinear, and trilinear filtering.
  - [x] mip generation and mip bias where supported.
  - [x] anisotropy.
  - [x] texture shape: 2D, 2D array, cube, or cube array.
- [x] Preserve scale/offset for every texture property, including repeated
  feature slots.
- [x] Resolve `Texture2DArray` and cubemap assets without flattening them.
- [x] Preserve missing references and unsupported asset types in the report.
- [x] Store unknown properties losslessly in source metadata.
- [x] Add parser fixtures for old/new Unity YAML layouts used by real Poiyomi
  materials.

Acceptance criteria:

- [x] A parse/serialize diagnostic round trip retains all material values and
  references used by the source fixture corpus.
- [x] Texture semantic and sampler metadata reach the normalized descriptor.
- [x] Locked and unlocked versions of the same material normalize to equivalent
  semantic descriptors.

## Phase 2 - Correct The Existing Uber Conversion Baseline

- [x] Add the `_ToonRamp` sampler to the Poiyomi mapper with correct transform,
  color space, default, and feature activation.
- [x] Add UV1/UV2/UV3 varyings to all applicable standard, OpenVR, OpenXR, and
  Vulkan vertex permutations.
- [x] Audit mesh attribute and lightmap-channel ownership so exposing UV1-UV3
  does not overwrite engine lightmap or backend-specific vertex semantics.
- [x] Define behavior and diagnostics when a mesh does not contain a requested
  UV channel.
- [x] Replace ambiguous texture aliases with property-specific conversions.
- [x] Map first and second shade textures independently.
- [x] Map metallic and smoothness data independently, respecting packed-channel
  layouts.
- [x] Distinguish rim color textures from rim masks.
- [x] Distinguish dissolve mask, base noise, detail noise, gradient, and edge
  data.
- [x] Translate every enum through named source-to-destination tables.
- [x] Add out-of-range enum diagnostics.
- [x] Implement semantic sampler defaults instead of treating every non-normal
  texture as white.
- [x] Propagate texture sampling and color-space metadata into `XRTexture`.
- [x] Correct feature enable detection using source section toggles, keywords,
  textures, and non-default authored values.
- [x] Ensure unused source sections do not enable unnecessary variants.
- [x] Replace path-fragile Poiyomi detection with versioned signatures.
- [x] Temporarily report outline and integration features as unsupported until
  their actual runtime paths land; do not claim support from uniforms alone.
- [x] Add exact tests for each corrected mapping and regression.

Acceptance criteria:

- [x] Basic opaque, cutout, fade, transparent, ramped-toon, normal-mapped, and
  emissive fixture materials bind the expected textures and values.
- [ ] UV0-UV3 produce distinguishable reference output on a four-UV test mesh.
- [x] The conversion report contains no false claims for features that do not
  execute.

## Phase 3 - Render-State And Multi-Pass Material Architecture

- [ ] Design an uber material pass-set representation that shares one authored
  state while allowing pass-specific shaders and fixed-function state.
- [ ] Represent source pass enable/disable state explicitly.
- [ ] Map Poiyomi render presets individually:
  - [ ] Opaque.
  - [ ] Cutout.
  - [ ] Fade.
  - [ ] Transparent.
  - [ ] TransClipping.
  - [ ] Additive.
  - [ ] Soft additive.
  - [ ] Multiplicative.
  - [ ] Multiplicative 2x.
- [ ] Preserve render queue and queue offset/priority without assuming queue
  alone identifies transparency.
- [ ] Map source and destination RGB/alpha blend factors separately.
- [ ] Map RGB and alpha blend operations.
- [ ] Map ZWrite, ZTest, culling, color mask, polygon offset, alpha-to-coverage,
  and fog behavior.
- [ ] Map front-face, back-face, and outline stencil reference, comparison,
  masks, and operations.
- [ ] Implement an EarlyZ/depth prepass when the selected preset requires it.
- [ ] Determine whether Poiyomi's ForwardAdd pass maps to one Forward+ base
  pass or needs a compatibility pass for authored additive-light behavior.
- [ ] Ensure shadow casting observes alpha, dissolve, UV discard, vertex
  deformation, culling, and source shadow-pass enable state.
- [ ] Ensure depth-normal, transform-ID, velocity, picking, and reflection
  passes observe the same opacity and vertex-position rules as the color pass.
- [ ] Implement an inverse-hull outline companion pass with independent render,
  blend, depth, stencil, and cull state.
- [ ] Define deterministic pass order for base, outline, transparent, depth,
  and shadow participation.
- [ ] Prewarm all required pass variants as one material operation.
- [ ] Make pass setup allocation-free during steady-state render submission.

Acceptance criteria:

- [ ] Every Poiyomi render preset has a dedicated state-mapping test.
- [ ] Base, depth, shadow, velocity, picking, and outline silhouettes agree for
  alpha, dissolve, discard, and vertex deformation.
- [ ] Pass ordering and state are correct in OpenGL and Vulkan captures.

## Phase 4 - Uber Module And Binding Infrastructure

- [ ] Audit every existing uber helper file and classify it as active, partial,
  dormant, obsolete, or reusable.
- [ ] Integrate useful dormant helpers through the canonical uber shader or
  replace them; remove obsolete duplicates only after their behavior is covered.
- [ ] Split the shader manifest into focused include files if doing so improves
  ownership without breaking annotation parsing or variant identity.
- [ ] Define reusable slot schemas for decals, matcaps, emissions, and rims.
- [ ] Generate repeated uniform/property declarations from one authoritative
  slot contract or enforce equivalent consistency tests.
- [ ] Keep slot counts and per-slot subfeatures statically specializeable.
- [ ] Add dependency rules so enabling a subfeature brings in its required
  parent data and nothing else.
- [ ] Audit maximum sampled textures, image resources, uniform storage, push
  constants, and descriptors for OpenGL and Vulkan.
- [ ] Choose and document a binding ladder for high-slot materials:
  - [ ] Direct samplers while within guaranteed backend limits.
  - [ ] Texture arrays where dimensions/formats/sampler behavior are compatible.
  - [ ] Engine material texture tables or bindless descriptors when available.
  - [ ] Explicit conversion failure when faithful binding is impossible.
- [ ] Add sampler-role-aware fallback resources.
- [ ] Include pass identity and all position/opacity-affecting static values in
  variant and prewarm keys.
- [ ] Add generated-source size, sampler count, feature count, and compile-time
  diagnostics to conversion/prewarm reports.
- [ ] Ensure maximal supported variants compile without warnings.

Acceptance criteria:

- [ ] No declared feature is counted as supported unless its code is reachable
  from an engine render pass.
- [ ] Minimal materials do not retain unrelated feature code or samplers.
- [ ] Maximal supported materials stay within documented backend limits or fail
  conversion with a precise reason.

## Phase 5 - Color, Normals, Alpha, UV, And Mask Parity

### Main Color And Normal Surface

- [ ] Match main texture, tint, UV transform, vertex color, alpha, and theme
  behavior.
- [ ] Match hue, saturation, value/brightness, grayscale, contrast, color-space,
  replacement, and animated hue controls.
- [ ] Match main/detail normal scale, blending, UV selection, panning, and masks.
- [ ] Implement detail texture blending and its source blend modes.
- [ ] Implement normal correction options used by the source shader.
- [ ] Match backface color, texture, alpha, emission, normals, and blend modes.

### Alpha

- [ ] Match alpha source/map modes, channels, inversion, modulation, and cutoff.
- [ ] Match alpha-to-coverage behavior.
- [ ] Match screen/object-space dithering and dither animation where applicable.
- [ ] Match distance alpha/fade.
- [ ] Match fresnel alpha.
- [ ] Match angular alpha.
- [ ] Match premultiplied-alpha semantics.
- [ ] Match AudioLink-driven alpha after the AudioLink adapter is available.

### UV And Sampling

- [ ] Support real UV0, UV1, UV2, and UV3 selection for every applicable slot.
- [ ] Match local/world/object projection modes.
- [ ] Match panosphere mapping.
- [ ] Match polar mapping.
- [ ] Match UV panning, rotation, and transforms in source order.
- [ ] Implement UV distortion with mask and AudioLink hooks.
- [ ] Implement Deliot-Heitz stochastic sampling.
- [ ] Implement hex-tile stochastic sampling.
- [ ] Audit derivatives, mip selection, and tangent-space correctness for each
  sampling mode.

### Masks And Themes

- [ ] Implement RGBA color masking with per-channel texture, color, normal, PBR,
  emission, and blend controls.
- [ ] Implement four global mask textures.
- [ ] Expose all sixteen global RGBA mask channels.
- [ ] Implement global mask modifiers for vertex, backface, mirror, camera, and
  distance behavior.
- [ ] Implement global themes 0-3 and per-feature theme-index consumption.
- [ ] Validate mask channel, inversion, min/max/remap, and UV semantics.

Acceptance criteria:

- [ ] The color/normal/alpha/UV/mask reference corpus matches Unity captures
  within the approved visual tolerance.
- [ ] Linear masks and normal data never receive unintended sRGB conversion.

## Phase 6 - Lighting, Toon Modes, PBR, And Surface Response

### Light Data

- [ ] Match direct/indirect light color and direction selection.
- [ ] Match forced light color/direction controls.
- [ ] Match minimum brightness, monochromatic, cap, and ambient-ignore controls.
- [ ] Match material AO channels and strengths.
- [ ] Match shadow masks and channel strengths.
- [ ] Match detail-shadow behavior.
- [ ] Implement or explicitly classify world AO blocker behavior.
- [ ] Match source additive-light behavior or document the Forward+ equivalent.
- [ ] Map Unity lightmaps, probes, and ambient behavior to documented native
  equivalents where literal data is unavailable.

### Nine Shading Modes

- [ ] Texture Ramp: ramp texture, lighting coordinates, shadow interaction, and
  authored modifiers.
- [ ] Multilayer Math: every shade layer, color, border, blur, mask, and blend.
- [ ] Wrapped: wrap and normalization behavior.
- [ ] Skin: source skin response, tinting, scattering, and shadow behavior.
- [ ] ShadeMap: first/second shade maps, colors, steps, masks, and blend order.
- [ ] Flat: unlit/flat response and light-data controls.
- [ ] Realistic: source PBR response using engine lighting and IBL inputs.
- [ ] Cloth: cloth/sheen response and source controls.
- [ ] SDF: source SDF texture/data, direction, thresholds, and shadow response;
  remove the current positional approximation when a real map is provided.

### Reflection And Specular Families

- [ ] Match metallic and smoothness maps, channels, multipliers, and inversion.
- [ ] Match Mochie-style PBR reflection/specular behavior where present.
- [ ] Support independent first and second specular lobes.
- [ ] Implement anisotropic highlights and their tangent/direction controls.
- [ ] Implement clear coat.
- [ ] Implement per-material cubemap sampling and native reflection-probe
  mapping.
- [ ] Implement environmental rim.
- [ ] Implement UnityChan/lilToon-style stylized reflections.
- [ ] Implement backlight.
- [ ] Expand subsurface scattering to the source controls and masks.

### Matcaps And Rims

- [ ] Implement matcap slots 0-3.
- [ ] Match per-slot color, texture, mask, normal source, intensity, blend,
  replace/multiply/add, lighting, emission, UV, and theme controls.
- [ ] Implement rim slots 0-1.
- [ ] Implement depth rim.
- [ ] Match rim mask, width, sharpness, bias, light/shadow, blend, emission,
  normal, texture, and theme behavior.

Acceptance criteria:

- [ ] Every shading mode has a dedicated material fixture and reference scene.
- [ ] Every lighting family behaves consistently under directional, point,
  spot, ambient-only, shadowed, and reflection-probe scenes.
- [ ] OpenGL and Vulkan stay within the approved image-difference tolerance.

## Phase 7 - Repeated Layer Families

### Decals 0-3

- [ ] Implement four independent decal slots from one shared slot contract.
- [ ] Match texture, color, mask, UV set, transform, panning, rotation, scale,
  blend mode, alpha, and depth behavior.
- [ ] Match emission, hue shift, chromatic aberration, video, TPS, theme, and
  AudioLink hooks per slot.
- [ ] Match mirror/camera visibility and global-mask interaction per slot.
- [ ] Preserve slot ordering and cross-slot blend behavior.

### Emissions 0-3

- [ ] Implement four independent emission slots.
- [ ] Match texture, color, mask, UV, panning, blink, pulse, hue, center-out,
  blend, base-color replacement, and theme behavior per slot.
- [ ] Match light-data and global-mask interaction.
- [ ] Add AudioLink modulation per slot.
- [ ] Preserve HDR values and choose appropriate engine texture formats.

### Flipbook

- [ ] Import and bind the source `Texture2DArray` flipbook.
- [ ] Match frame selection, frame rate, manual frame, crossfade, blend, color,
  alpha, emission, mask, UV, and AudioLink behavior.
- [ ] Preserve array-layer order and texture sampling metadata.
- [ ] Add a documented 2D-sprite-sheet conversion only as an explicit optional
  asset conversion, not an implicit fallback.

Acceptance criteria:

- [ ] All slots can be enabled independently and together.
- [ ] Disabling unused slots removes their static code and bindings.
- [ ] Slot order, masks, animation, and theme/AudioLink hooks match reference
  captures.

## Phase 8 - Outlines And Special Effects

### Outlines

- [ ] Implement inverse-hull expansion using source object/world/screen sizing
  semantics.
- [ ] Implement Basic, Rim, Directional, and Drop Shadow outline modes.
- [ ] Match width, mask, texture, color, emission, hue, color-adjustment,
  distance-alpha, fixed-screen-width, lighting, and Z-offset controls.
- [ ] Match vertex-color normal/width/mask controls.
- [ ] Match cull, depth, stencil, blend, queue, and AudioLink controls.
- [ ] Handle skinned meshes, morph targets, mirrored transforms, non-uniform
  scale, stereo views, and reversed winding.

### Dissolve

- [ ] Implement source texture/basic dissolve.
- [ ] Implement point-to-point dissolve.
- [ ] Implement spherical dissolve.
- [ ] Implement center-out dissolve.
- [ ] Implement UV-tile-aware dissolve.
- [ ] Match base noise, detail noise, gradient, mask, edge, emission, hue,
  continuous mode, inversion, and source coordinate spaces.
- [ ] Match animated/locked dissolve properties and AudioLink controls.

### Other Surface And Procedural Effects

- [ ] Implement UV tile/face discard.
- [ ] Implement depth bulge.
- [ ] Complete Poiyomi glitter/sparkle behavior and masks.
- [ ] Implement pathing.
- [ ] Implement proximity color.
- [ ] Implement Depth FX/touch glow.
- [ ] Implement internal parallax effects separately from standard height-map
  parallax/POM.
- [ ] Implement mirror/camera visibility controls after view-context plumbing.
- [ ] Implement stats overlay features where meaningful in XRENGINE.
- [ ] Implement video effects, CRT, and Gameboy effects.
- [ ] Implement Voronoi effects.
- [ ] Implement Truchet effects.
- [ ] Implement material post-processing controls that can be expressed safely
  in the engine pass model.
- [ ] Classify any Unity/VRChat-only procedural input as an adapter dependency
  rather than inventing constant data.

Acceptance criteria:

- [ ] Position/discard effects agree in color, depth, shadow, velocity, picking,
  and outline passes.
- [ ] Effects remain stable in mono and stereo and do not allocate per frame.

## Phase 9 - Vertex Features And Runtime Integration Adapters

### Vertex Features

- [ ] Implement basic/fun vertex manipulation controls.
- [ ] Implement vertex-color-driven deformation and masks.
- [ ] Implement look-at deformation with defined object/view/world semantics.
- [ ] Implement texture-driven vertex glitching.
- [ ] Implement Uzumore/view-clip prevention behavior or document the native
  equivalent.
- [ ] Ensure all vertex displacement participates in bounds expansion/culling.
- [ ] Define conservative bounds or authored bounds hints for animated vertex
  effects.
- [ ] Ensure deformed geometry writes correct motion vectors.

### AudioLink

- [ ] Define an engine-native audio spectrum/AudioLink data provider.
- [ ] Define timing, band, history, chronotensity, and global/local input
  semantics needed by Poiyomi features.
- [ ] Bind the provider through a stable GPU buffer or texture contract.
- [ ] Implement master AudioLink controls and overrides.
- [ ] Implement decal spectrum and volume-color behavior.
- [ ] Connect AudioLink hooks for alpha, hue, emission, decals, dissolve,
  outlines, UV distortion, flipbooks, vertex effects, and other consumers.
- [ ] Emit one clear missing-provider diagnostic and preserve authored values
  when no AudioLink source exists.

### Lighting And Environment Integrations

- [ ] Define an LTCGI adapter or documented mapping to native engine GI.
- [ ] Define a Light Volumes adapter or documented mapping to engine probe/
  volume lighting.
- [ ] Implement blacklight masking with a native view/light context.
- [ ] Define Beat Saber-specific input adapters only if corresponding source
  features remain in the pinned free shader.
- [ ] Define mirror and camera-view classification in render-view state.
- [ ] Keep all external adapters optional and compile their shader consumers out
  when unavailable.

Acceptance criteria:

- [ ] Missing services produce explicit diagnostics, never stale or
  uninitialized sampling.
- [ ] Native adapters have documented update cadence, coordinate spaces,
  resource lifetime, and multi-view behavior.
- [ ] Vertex effects have correct culling and motion-vector behavior.

## Phase 10 - Material Animation And Shader Locking

- [ ] Extend Unity animation import to recognize renderer material-property
  curve bindings and material-slot indices.
- [ ] Import animated float, integer-compatible, vector, color, and texture/
  object-reference curves where Unity data supports them.
- [ ] Normalize unlocked property names to semantic Poiyomi descriptor fields.
- [ ] Decode Poiyomi renamed-animated property suffixes from locked shaders.
- [ ] Preserve source curve tangents, interpolation, wrap, and timing.
- [ ] Map every animatable semantic field to its uber property binding.
- [ ] Promote curve-bound uber properties to `Animated` mode before variant
  preparation.
- [ ] Keep non-animated values static so specialization remains effective.
- [ ] Handle animation of feature weights without requiring unsupported runtime
  mutation of compile-time section toggles.
- [ ] Define behavior for clips that animate a feature absent from the material:
  include/prewarm it or report an invalid binding.
- [ ] Rebind curves when repeated slots or property packing changes destination
  layout.
- [ ] Preserve original bindings in conversion metadata for reconversion and
  diagnostics.
- [ ] Add animation-driven variant prewarming.
- [ ] Add locked-versus-unlocked animation equivalence tests.

Acceptance criteria:

- [ ] Converted material animations produce the same semantic values over time
  for locked and unlocked fixtures.
- [ ] Static properties are still specialized and animated properties remain
  runtime uniforms/bindings.
- [ ] Missing or ambiguous bindings are reported with clip, path, material slot,
  and property identity.

## Phase 11 - ThryEditor-Parity ImGui Material Authoring

The parity target is the ThryEditor snapshot embedded in the pinned Poiyomi
commit, limited to annotations and workflows that the pinned Toon shader
actually exposes. The implementation must remain engine-native and reusable by
other uber shaders; do not hard-code one 3,000-property inspector or execute
Unity editor assemblies. The existing flat manifest/category inspector is the
starting point, not the completion state.

### Schema, Hierarchy, And Discoverability

- [ ] Evolve `ShaderUiManifest` into or alongside a typed authoring-schema tree
  that can represent material roots, sections, subsections, properties,
  decorators, actions, referenced children, and tool launchers.
- [ ] Give every schema node a stable semantic ID independent of display text,
  localization, imported Unity property name, and generated GLSL uniform name.
- [ ] Preserve exact source declaration order and the nested `m_start`/
  `m_end` and `s_start`/`s_end` structure used throughout Poiyomi Toon.
- [ ] Validate balanced section markers and report malformed or overlapping
  hierarchy instead of flattening it silently.
- [ ] Implement collapsible nested groups with `default_expand`,
  `persistent_expand`, and `ref_float_toggles_expand` semantics.
- [ ] Persist expansion state with a stable shader/schema/user identity and
  invalidate it safely when the schema changes.
- [ ] Implement headers, `ThryHeaderLabel`, separators, `Space`/`ThrySpace`,
  indentation, borders, `margin_top`, and intentional grouping whitespace.
- [ ] Parse rich display labels separately from raw property names and support
  the active `ThryRichLabel` markup without allowing arbitrary executable UI.
- [ ] Implement label alternatives from `alts` and define when compact,
  advanced, and translated labels select each alternative.
- [ ] Model every active embedded `PropertyOptions` field, including `offset`,
  `tooltip`, `altClick`, `onClick`, conditions, action collections,
  `button_help`, `button_author`, `texture`, `reference_property`,
  `reference_properties`, `fps_property`, `force_texture_options`,
  `is_visible_simple`, `file_name`, `remote_version_url`, `generic_string`,
  `never_lock`, `margin_top`, `alts`, `persistent_expand`, `default_expand`,
  `ref_float_toggles_expand`, and `draw_border`; classify fields unused by the
  pinned shader rather than implementing them speculatively.
- [ ] Implement `is_visible_simple` and an inspector-level simple/advanced mode
  without hiding authored non-default values from status or reset workflows.
- [ ] Add search across localized display text, alternative labels, raw Unity
  property names, semantic IDs, tooltips, and section names.
- [ ] Automatically reveal and expand ancestors of search matches, highlight
  the matching text, and restore prior expansion state when search is cleared.
- [ ] Add deterministic previous/next search navigation and an exact-property
  lookup entry point for animation, diagnostics, and texture-use results.
- [ ] Preserve scroll focus and open state across static/animated changes,
  variant rebuilds, reimports, and localization changes.
- [ ] Virtualize or clip large property trees so the full Poiyomi schema does
  not submit thousands of hidden ImGui controls per frame.
- [ ] Render `Helpbox`, `IMPORTANT`, `sRGBWarning`, tooltip, `button_help`, and
  `button_author` content with severity, accessible text, and safe link actions.
- [ ] Show a visible diagnostic placeholder for an active but unsupported
  schema node; never degrade a specialized annotation to an unlabeled generic
  numeric field without reporting the loss.

### Conditions, References, And Declarative Actions

- [ ] Implement and cache the expression grammar used by `condition_showS`,
  legacy `condition_show`, `condition_enable`, and
  `condition_enable_children`.
- [ ] Support equality/inequality, ordered comparisons, `&&`/`||`, legacy
  single `&`/`|`, inversion, grouping parentheses, and numeric/string/boolean
  literals with deterministic coercion rules.
- [ ] Support property-state operands used by the pinned schema, including
  texture presence/name, render queue, static/animated state, and relevant
  capability or version tokens.
- [ ] Map Unity- or VRChat-specific condition operands to documented engine
  capability providers or classify them preserved inactive; never fabricate a
  successful condition.
- [ ] Support the arithmetic expression subset used by active drawers,
  including unary signs, `+`, `-`, `*`, `/`, `%`, `^`, comparisons, and logical
  operations where the embedded implementation accepts them.
- [ ] Compile expressions once into typed ASTs, record property dependencies,
  and re-evaluate only nodes affected by a changed dependency.
- [ ] Detect reference and condition cycles, unknown operands, type errors,
  divide-by-zero, and invalid expressions at schema-load time with node/source
  diagnostics.
- [ ] Implement `reference_property` and ordered `reference_properties` as
  explicit graph edges used by controls, copy/reset, animation, presets, and
  action propagation.
- [ ] Preserve reference semantics when a source property maps to packed or
  split uber properties; do not fall back to raw uniform-name coupling.
- [ ] Implement typed `on_value`, `actions`, `on_value_actions`, `onClick`, and
  `altClick` definitions.
- [ ] Support native equivalents for Thry action kinds `SET_PROPERTY`,
  `SET_TAG`, `SET_SHADER`, `URL`, and `OPEN_EDITOR`.
- [ ] Translate `SET_TAG` and render-queue actions to explicit XRENGINE
  material/render-state fields while retaining imported Unity tags in source
  metadata.
- [ ] Translate `SET_SHADER` through the semantic shader/converter registry and
  provide a compatibility preview before losing incompatible authored values.
- [ ] Map `OPEN_EDITOR` to allowlisted registered engine editor command IDs;
  never reflect or instantiate a type name supplied by shader metadata.
- [ ] Gate external URLs through the editor's safe-link confirmation policy and
  never perform a network request merely because a material inspector opened.
- [ ] Execute each action list as one validated transaction with one undo step,
  one dirty/save update, and at most one variant invalidation/rebuild.
- [ ] Preflight an action transaction and roll it back completely if any
  required property, type conversion, asset, or render state is invalid.
- [ ] Reproduce the `_Mode` render-preset action graph, including all coupled
  blend, depth, queue, cull, pass, tag, and related state changes.
- [ ] Surface action side effects before destructive shader/preset transitions
  and include every changed field in undo and conversion diagnostics.

### Built-In, Thry, And Poiyomi Property Controls

- [ ] Build a typed widget registry keyed by imported annotation plus semantic
  value type; keep Poiyomi-specific controls in focused registrations rather
  than branches in the main inspector loop.
- [ ] Implement mixed-value, multi-selection, default-state, reset, keyboard
  navigation, drag editing, and static/animated indication consistently across
  every registered control.
- [ ] Match Unity built-in annotations used by the shader: `HideInInspector`,
  `Enum`, `KeywordEnum`, `Toggle`, `ToggleUI`, `PowerSlider`, `IntRange`, `HDR`,
  `Gamma`, `Normal`, `NoScaleOffset`, `MaterialToggle`, `TextureArray`, and
  `NonModifiableTextureData`.
- [ ] Implement `ThryWideEnum` with labels, authored numeric values, mixed
  values, unknown-value preservation, and readable wrapping at narrow widths.
- [ ] Implement `ThryToggle` and `ThryToggleUI`, including reference values,
  keyword/action side effects, animation state, and child expansion behavior.
- [ ] Implement `MultiSlider` with independently labeled components, per-
  component ranges, exact source packing, mixed values, and reset behavior.
- [ ] Implement `Vector2`, `Vector3`, `Vector31`, `Vector4Toggles`,
  `VectorLabel`, and `VectorToSliders` variants that the generated usage catalog
  marks active.
- [ ] Implement Poiyomi `ButtonVector` labeled component buttons, including the
  `NA` disabled-component convention and correct numeric packing.
- [ ] Implement Poiyomi `ThryMultiFloatButtons` as coupled labeled property
  toggles with shared mixed, animation, and default-state feedback.
- [ ] Implement `ThryMultiFloats` and every active multi-property arrangement
  with atomic editing across referenced scalar fields.
- [ ] Implement `ThryMask` as a semantic channel-mask control with correct bit/
  vector representation and accessible channel labels.
- [ ] Implement `ThryTexture`, the active large/small/stylized texture layouts,
  `force_texture_options`, scale/offset policy, preview, drag/drop, and linked
  reference fields.
- [ ] Implement `TextureKeyword` so texture assignment and clearing update the
  intended feature/keyword state transactionally rather than leaving stale
  variants.
- [ ] Implement active `Gradient`, `Curve`, `FourFloatCurve`/`Curve4`, and
  `Ramp4` controls as dictated by the pinned usage catalog.
- [ ] Implement `ThryDecalPositioning` launch/status controls and bind them to
  the matching decal slot's semantic property set.
- [ ] Support `ThryCustomGUI` and `ThryExternalTextureTool` only through an
  allowlisted engine widget/tool registry; active unknown IDs must be reported
  as unsupported rather than executed dynamically.
- [ ] Verify the generated inventory covers all active occurrences of
  `ThryRGBAPacker`, `ThryTexture`, `Gradient`, `Curve`, `TextureArray`, and every
  other custom annotation before declaring the generic fallback unreachable.
- [ ] Preserve `DoNotAnimate`, `DoNotLock`, and any active do-not-rename metadata
  as enforceable editor capabilities, not decorative labels.
- [ ] Add an annotation-inspection developer view showing parsed arguments,
  resolved widget, dependencies, source property, semantic destination, and
  warnings for a selected control.
- [ ] Inventory and implement every Thry user preference that changes the
  Poiyomi inspector's behavior, such as display complexity, tooltip/help
  visibility, animation indicators, texture presentation, search, language,
  and optimize/lock prompts, using XRENGINE editor preference storage.
- [ ] Expose render queue, source render preset, tags, pass participation, and
  custom-state status in a focused rendering section with a safe route back to
  a known preset.

### Texture, Gradient, Curve, And Array Authoring

- [ ] Implement a native `ThryRGBAPacker` launcher and compact inline status for
  every packed-texture field that uses it.
- [ ] Support a source texture, constant color/value, or authored gradient per
  output channel with selectable input channel, invert, fallback, and remap/
  range controls.
- [ ] Provide a live packed preview with individual RGBA inspection and clear
  distinction between sRGB color and linear data semantics.
- [ ] Reproduce the advanced packer's node/workspace functionality used by
  Poiyomi authors, including reusable source nodes and explicit channel wiring.
- [ ] Support the embedded packer's active image operations: brightness, hue,
  saturation, grayscale/channel selection, rotation, scale, offset, edge/
  kernel processing, and blend operations identified by the pinned audit.
- [ ] Define output resolution, aspect/resampling policy, filter, alpha-
  transparency treatment, mip behavior, compression, quality, and color space.
- [ ] Support the source tool's required PNG, JPEG, and EXR outputs where the
  engine asset pipeline has a lossless semantic equivalent; diagnose an
  unavailable encoding instead of silently changing formats.
- [ ] Validate output paths inside approved project asset roots, confirm
  overwrite, import the result, assign it to the material, and wrap asset plus
  material changes in a coherent undoable operation.
- [ ] Store a non-destructive, versioned packing recipe beside generated output
  so sources can be reopened, relinked, and deterministically rebuilt.
- [ ] Track source-asset dependencies and surface stale/missing inputs without
  rebuilding textures during ordinary inspector drawing.
- [ ] Implement a gradient editor with color/alpha keys, precise numeric entry,
  interpolation modes used by the source, drag/drop, preview, orientation,
  resolution, color space, save, reopen, and deterministic rebake.
- [ ] Implement curve and four-channel curve editors with source-equivalent
  ranges, key/tangent behavior, preview, numeric editing, and deterministic
  texture or `Vector4` sample baking as required by each property.
- [ ] Implement the four-stop/ramp control with draggable and numeric positions,
  stable ordering rules, color/value editing, and exact packed representation.
- [ ] Implement `TextureArray` authoring from an existing array asset or ordered
  multi-image drop, with layer reordering, preview, deletion, and insertion.
- [ ] Validate texture-array layer size, format, mip, color-space, and semantic
  compatibility; any resample/conversion policy must be explicit and
  reproducible.
- [ ] Update referenced frame/count properties atomically when a generated
  texture array changes.
- [ ] Add resource cleanup and cancellation for previews/generators so repeated
  inspector use does not leak CPU images, GPU textures, or file handles.

### Decal Positioning And Viewport Tools

- [ ] Implement a native decal-positioning mode reachable from each active
  `ThryDecalPositioning` control.
- [ ] Integrate scene/viewport raycast placement, selected renderer/material
  slot resolution, and clear feedback when no compatible surface is hit.
- [ ] Provide gizmos and numeric controls for position, rotation, scale, side/
  depth offset, UV selection, and the corresponding texture transform fields.
- [ ] Support the source tool's relevant projection modes and mirrored-side
  behavior without assuming Unity object or tangent coordinate conventions.
- [ ] Make drag operations preview live but commit as bounded undo steps; cancel
  must restore the complete pre-tool material state.
- [ ] Validate skinned meshes, non-uniform/mirrored transforms, multi-material
  renderers, stereo views, and selection changes while the tool is active.
- [ ] Ensure viewport tool state cannot retain or mutate a disposed/reimported
  material and exits cleanly on scene or asset unload.

### Presets, Clipboard, And Property Workflows

- [ ] Implement versioned material preset assets with named collections,
  metadata, thumbnails/previews where available, and deterministic ordering.
- [ ] Support full-material, section, subsection, and selected-property presets.
- [ ] Store semantic property IDs, values, asset references, static/animated
  modes, feature states, referenced properties, and applicable render state.
- [ ] Support explicit per-property inclusion/exclusion when authoring a preset.
- [ ] Implement preset search, collection filtering, recently used entries, and
  quick apply from the material inspector.
- [ ] Implement non-destructive preset preview with Apply, Revert, and Dismiss;
  closing or changing selection during preview must restore state unless the
  preview was committed.
- [ ] Define deterministic multi-preset sequencing and show conflicts or later
  overrides before commit.
- [ ] Apply presets across compatible shaders through semantic IDs and report
  every skipped, converted, or incompatible value.
- [ ] Import or translate redistributable Poiyomi/Thry preset data without
  binding runtime authoring to Unity GUIDs or raw property names.
- [ ] Implement property, subsection, section, and whole-material copy, paste,
  and reset recursively through declared references.
- [ ] Implement Paste Special with a hierarchical preview and explicit child/
  referenced-property selection.
- [ ] Version clipboard payloads, validate types and asset references, and
  retain a readable fallback when copied between schemas or engine versions.
- [ ] Provide context actions for raw source name, semantic ID, animation path,
  keyframe insertion, static/animated mode, renamed-animation identity, preset
  inclusion, note editing, and source/default inspection as applicable.
- [ ] Show a reliable non-default indicator at property and ancestor-section
  levels, including values changed only through referenced fields or modes.
- [ ] Model imported state, preset/variant state, and user-authored local
  overrides as explicit layers with Apply/Revert operations; do not copy Unity
  material-variant assumptions into opaque material mutation.
- [ ] Ensure reset chooses the correct schema/source/preset default layer and
  clearly previews which child properties will change.

### Multi-Material Editing, Linking, And Utility Workflows

- [ ] Support ordinary multi-selection in the material inspector with mixed
  values and atomic edits for every control, tool, action, and reset operation.
- [ ] Implement a Cross-Shader Editor equivalent that can add/remove materials,
  collect compatible material slots from selected scene renderers, and batch
  edit without changing the active asset selection.
- [ ] Build the cross-editor property union/intersection by semantic ID, retain
  source order where possible, and show how many selected materials accept each
  edit.
- [ ] Permit heterogeneous shaders only when value type and semantic contract
  are compatible; provide an explicit per-material skipped/incompatible report.
- [ ] Commit a batch edit as one undoable transaction without recompiling the
  same resulting variant once per material.
- [ ] Implement persistent material-link groups for propagating a selected
  semantic property across member materials.
- [ ] Support drag/drop membership, source/member inspection, unlink one,
  unlink all, and safe behavior for deleted, moved, or reimported materials.
- [ ] Validate type/asset compatibility and prevent cycles or feedback loops in
  linked groups; propagation must be one deterministic undo transaction.
- [ ] Reuse the conversion semantic mapping as the native Shader Translator
  equivalent and expose a preview/report instead of duplicating a second raw-
  property translation system.
- [ ] Implement a material cleanup report for unbound/unused textures, values,
  tags, and imported keywords with per-item selection and undoable removal.
- [ ] Preserve unknown imported metadata by default and require explicit user
  confirmation before cleanup removes reconversion data.
- [ ] Implement a texture-use finder that lists material/property references,
  navigates to the material inspector, expands the ancestors, and focuses the
  exact semantic property.
- [ ] Implement the relevant unlocked/unoptimized-material list and material
  manager workflows for finding, filtering, and batch preparing materials.

### Optimization, Locking, Animation, And Variant Feedback

- [ ] Present Poiyomi/Thry "lock" as an engine-native Optimize/Prepare Variant
  workflow while preserving familiar imported lock status and semantics.
- [ ] Map the active `ThryShaderOptimizerLockButton` control to that native
  workflow with source-lock status, confirmation, progress, and diagnostics.
- [ ] Show authored, prepared, compiling, ready, failed, stale, and fallback
  states plus variant key, feature count, pass set, and actionable diagnostics.
- [ ] Provide per-material and batch prepare/unprepare/rebuild/prewarm actions
  with progress, cancellation, failure isolation, and summary results.
- [ ] Keep `DoNotLock` fields dynamic and `DoNotAnimate` fields excluded from
  automatic animation marking and animation authoring.
- [ ] Preserve and expose locked, unlocked, static, animated, and renamed-
  animated source identities through conversion and reimport.
- [ ] Automatically mark eligible properties animated while the editor's
  animation record mode authors their bindings, matching Thry's workflow.
- [ ] Provide keyframe/add-binding actions for scalar, vector, color, texture,
  referenced, packed, and repeated-slot fields with clear unsupported cases.
- [ ] Prevent a property-mode change from discarding an existing binding or
  invalidating a clip without confirmation and a repair path.
- [ ] Replace Unity Render Queue Shader generation with native render-state and
  preset actions; document why no generated Unity-style queue shader is needed.
- [ ] Surface `DoNotRename` or equivalent constraints in any optimizer name-
  specialization path used by imported locked animations.
- [ ] Make compile/prewarm failures visible and retain the previous usable
  variant where safe; never silently switch an explicitly requested GPU path to
  unrelated behavior.

### Localization, Help, Notes, And External Content

- [ ] Import the active Poiyomi `shader_locale` data into versioned locale
  catalogs keyed by stable schema IDs rather than Unity GUIDs.
- [ ] Implement locale selection, locale fallback, missing-key diagnostics,
  argument substitution, and deterministic fallback to source labels.
- [ ] Ensure search indexes both the active locale and raw/source identities so
  translated users can still follow property names from diagnostics or docs.
- [ ] Provide an editor workflow for inspecting and authoring translation
  overrides without modifying the pinned imported source catalog.
- [ ] Preserve help/author labels and documentation targets with source-version
  provenance and render unavailable/unsafe targets as non-clickable text.
- [ ] Implement persistent per-material/per-property notes with a visible
  indicator, search support, undo, asset lifetime handling, and clear ownership
  between imported and local data.
- [ ] Classify `LocalMessage`, `RemoteMessage`, remote version checks, remote
  images, and similar embedded Thry facilities as active or unreachable for the
  pinned shader.
- [ ] For any active remote content, require explicit opt-in, HTTPS/domain and
  size policy, cache/version controls, offline behavior, and non-executable
  rendering; opening the inspector must never fetch remote content implicitly.
- [ ] Preserve author/help/license attribution for adapted Thry/Poiyomi UX or
  algorithms in the UI and generated asset metadata where required.

### Undo, Persistence, Safety, And Performance

- [ ] Define one material-edit transaction service shared by ordinary controls,
  actions, presets, linking, batch edits, texture generators, and viewport
  tools.
- [ ] Include material values, mode/feature state, render state, tags, asset
  references, generated assets, link/preset state, and local overrides in the
  relevant transaction boundary.
- [ ] Restore exact before/after values on undo/redo and issue no more variant,
  dependency, preview, and asset invalidation than the final state requires.
- [ ] Assign stable ImGui IDs from schema and target identity so duplicate
  labels, localization changes, filtering, and repeated slots cannot edit the
  wrong property.
- [ ] Eliminate per-frame reflection, annotation parsing, expression parsing,
  LINQ-heavy tree rebuilding, avoidable string formatting, and transient
  collection allocation from the large-inspector draw path.
- [ ] Bound preview texture memory, cache entries, generated thumbnails, locale
  data, clipboard payloads, and preset indexes; release them on asset/editor
  lifecycle events.
- [ ] Make background preview, file encoding, and variant work cancellable and
  marshal mutations back to the editor thread safely.
- [ ] Validate all imported enum/value counts, referenced properties, widget
  argument shapes, action targets, file paths, URLs, and external command IDs at
  schema-load time.
- [ ] Add a schema compatibility/version migration contract so persisted open
  state, notes, presets, links, and clipboard data fail safely after a shader or
  engine upgrade.
- [ ] Record telemetry for inspector build/draw time, visible/submitted node
  count, condition invalidations, preview memory, action duration, and variant
  churn without logging sensitive local paths or note contents.

Acceptance criteria:

- [ ] The generated pinned-source audit contains no active Thry/Poiyomi
  annotation or reachable material workflow without a reviewed classification,
  implementation owner, and validation case.
- [ ] Every active specialized drawer renders and edits its intended semantic
  values; none silently falls through to the generic property control.
- [ ] Poiyomi's nested structure, conditions, actions, search, help, presets,
  texture tools, decal positioning, multi-material editing, linking,
  localization, animation, and optimizer workflows are usable from ImGui.
- [ ] All edits, including multi-property and generated-asset operations, are
  deterministic, undoable, reimport-safe, and correctly invalidate variants.
- [ ] A maximal inspector remains responsive within an approved draw-time and
  allocation budget and does not fetch or execute untrusted external content.

## Phase 12 - Editor, Import Reporting, And Reimport Workflow

- [ ] Add a structured conversion summary to asset import results.
- [ ] Report source shader version, detected lock state, parity classification,
  generated features/passes, warnings, preserved inactive values, and failures.
- [ ] Group diagnostics by material and feature family.
- [ ] Add a machine-readable report format suitable for CI and corpus audits.
- [ ] Add an inspector view for original Poiyomi identity and conversion status.
- [ ] Show exact/native-equivalent/preserved-inactive status per enabled source
  feature.
- [ ] Expose engine-native equivalents with concise semantic-difference help.
- [ ] Prevent inspector controls from exposing dormant uniforms as working
  features.
- [ ] Support deterministic reimport when the converter catalog or shader source
  version changes.
- [ ] Store converter version and source descriptor version with generated
  material assets.
- [ ] Preserve user-authored post-conversion overrides separately from imported
  state so reimport does not silently destroy edits.
- [ ] Provide an explicit reset/reconvert operation.
- [ ] Add batch conversion/audit support for avatar or Unity-project imports.
- [ ] Add report counters for sampler pressure, generated variants, passes, and
  unsupported integrations.

Acceptance criteria:

- [ ] A user can determine what changed or was lost without inspecting logs or
  shader source.
- [ ] Reimport is deterministic and does not overwrite separated local
  overrides.

## Phase 13 - Test Corpus And Automated Validation

### Fixture Corpus

- [ ] Add licensed unlocked and locked 9.3.64 materials.
- [ ] Add one focused material for every feature family and render preset.
- [ ] Add maximal practical materials that combine interacting features.
- [ ] Add meshes with UV0-UV3, vertex colors, tangents, skinning, morph targets,
  mirrored transforms, and non-uniform scale.
- [ ] Add texture fixtures covering sRGB, normal, linear masks, packed data,
  cubemaps, and texture arrays.
- [ ] Add animation clips for scalar, vector, color, texture, repeated-slot, and
  renamed-animated bindings.
- [ ] Add pinned schema fixtures containing every active built-in, Thry, and
  Poiyomi annotation plus commented/inactive lookalikes that the inventory must
  exclude.
- [ ] Add locale, preset, clipboard, packing-recipe, texture-array, material-
  link, cross-shader, and user-override fixtures with explicit version data.
- [ ] Add multi-material fixtures that exercise compatible, mixed, missing,
  packed/split, and intentionally incompatible semantic properties.
- [ ] Record the Poiyomi version, Unity version, source values, and asset license
  for each fixture.

### Unit And Contract Tests

- [ ] Test every catalog property classification.
- [ ] Test YAML and texture-metadata parsing across supported Unity layouts.
- [ ] Test version and locked-shader detection.
- [ ] Test every texture, scalar, vector, color, enum, and render-state mapping.
- [ ] Test unknown-property preservation and diagnostic stability.
- [ ] Test pass generation and state isolation.
- [ ] Test animation binding remapping and static/animated specialization.
- [ ] Test semantic default textures and sampler state.
- [ ] Test variant determinism, dependency activation, and unused-feature
  pruning.
- [ ] Test maximal sampler/descriptor-limit handling.
- [ ] Snapshot-test the pinned UI-usage inventory so additions, removals,
  comments, malformed annotations, and embedded Thry source changes produce a
  reviewed catalog diff.
- [ ] Test schema hierarchy, source order, stable IDs, section balancing,
  default/persistent expansion, alternatives, localization, and schema-version
  migration.
- [ ] Test condition and arithmetic parsing, precedence, coercion, texture/
  render-queue/animation operands, dependency invalidation, unknown values,
  cycle detection, and malformed input.
- [ ] Test action parsing and atomic execution for property, tag, shader, URL,
  editor-command, render-preset, failure rollback, undo, and single variant
  invalidation.
- [ ] Contract-test every active widget registration against value shape,
  declared references, source defaults, mixed values, reset, static/animated
  modes, and generic-fallback prohibition.
- [ ] Test property/section/material copy, Paste Special, clipboard migration,
  preset preview/revert/apply/order, semantic cross-shader application, and
  precise skipped-value reports.
- [ ] Test multi-material edits and link propagation for one transaction, no
  cycles, asset deletion/reimport, mixed modes, and compatible semantic types.
- [ ] Test deterministic gradient, curve, ramp, RGBA packer, and texture-array
  outputs plus color-space, format, dependency, cancellation, overwrite, path,
  and undo behavior.
- [ ] Test local notes, search indexing, texture-use navigation, material
  cleanup preservation, optimizer state transitions, and animation-record
  auto-marking.
- [ ] Fuzz imported labels, annotations, expressions, URLs, paths, action
  targets, locale values, and clipboard/preset payloads; invalid data must not
  execute commands, escape asset roots, hang, or mutate partial state.

### ImGui Interaction And Tool Validation

- [ ] Add a deterministic inspector interaction harness that can select nodes,
  expand/collapse, filter, edit, invoke context actions, and verify resulting
  material/schema/undo state without relying only on screenshots.
- [ ] Validate raw-name and localized search, ancestor reveal, focus transfer,
  state restoration, duplicate-label IDs, and navigation after reimport.
- [ ] Exercise every specialized drawer with keyboard, mouse, drag/drop,
  clipboard, mixed selection, reset, and animation modes.
- [ ] Exercise preset preview cancellation, action rollback, generated-asset
  cancellation, multi-edit partial compatibility, and viewport-tool cancel.
- [ ] Capture reviewed ImGui reference images for collapsed, expanded, searched,
  conditional, mixed-value, warning/error, optimizer, preset, packer, gradient,
  array, cross-editor, and decal-tool states.
- [ ] Validate narrow/wide inspector layouts, supported Windows DPI scales,
  long translations, missing glyphs, and high-property-count scrolling.
- [ ] Validate renderer/material/scene selection changes while modal, preview,
  background, and viewport tools are active.
- [ ] Validate editor restart persistence for expansion, locale, notes, presets,
  links, local overrides, and generated recipes without persisting transient
  preview state.
- [ ] Verify no remote content is fetched and no external editor command runs
  until the applicable explicit policy/confirmation path is exercised.

### Shader Compilation

- [ ] Compile minimal, common, family-maximal, and global-maximal variants.
- [ ] Compile base, depth, shadow, velocity, picking, outline, and transparency
  pass permutations.
- [ ] Compile standard, OpenVR, and OpenXR vertex variants.
- [ ] Compile OpenGL and Vulkan GLSL paths.
- [ ] Use pairwise/combinatorial feature sampling for interactions that cannot
  be exhaustively enumerated.
- [ ] Treat shader warnings as validation failures for new code.

### Visual Validation

- [ ] Capture reference images from the pinned Poiyomi/Unity environment using
  documented camera, light, exposure, color-space, and render settings.
- [ ] Reproduce reference scenes in the Unit Testing World.
- [ ] Capture XRENGINE OpenGL and Vulkan results from identical camera views.
- [ ] Compare opaque, cutout, each transparent preset, outlines, shadow maps,
  depth/normal, motion vectors, and final composited output.
- [ ] Validate multiple camera positions so stale or view-independent sampling
  failures cannot pass accidentally.
- [ ] Establish numeric image-difference thresholds per feature family and keep
  human review for expected engine-native differences.
- [ ] Use RenderDoc for pass/resource discrepancies not explained by screenshots
  or logs.

### Performance And Reliability

- [ ] Measure import time and allocations per material and avatar corpus.
- [ ] Measure variant generation and backend compilation time.
- [ ] Measure shader source/binary size and prewarm-cache growth.
- [ ] Measure steady-state CPU binding and submission allocations.
- [ ] Measure GPU cost for isolated feature families and representative combined
  materials.
- [ ] Measure sampler/descriptor pressure and texture residency.
- [ ] Measure schema load/compile time, first inspector open, steady expanded and
  filtered ImGui draw time, condition invalidation cost, and managed allocations
  using a maximal Poiyomi material.
- [ ] Measure preview GPU/CPU memory, preset/search index size, packing and array
  generation time, and batch/cross-editor variant churn.
- [ ] Stress repeated open/close, search, locale switching, undo/redo, preset
  preview, texture generation cancellation, material relinking, and reimport for
  stale references or leaked resources.
- [ ] Stress material reimport, shader hot reload, device recreation, and scene
  teardown.
- [ ] Validate no new OpenGL/Vulkan warnings or resource-lifetime errors.

Acceptance criteria:

- [ ] The fixture corpus has no silent conversion omissions.
- [ ] Exact mappings meet approved image thresholds on OpenGL and Vulkan.
- [ ] Native equivalents have reviewed reference differences documented.
- [ ] All targeted pass, VR, animation, and render-state permutations compile
  and execute without new warnings.
- [ ] Steady-state hot paths introduce no avoidable heap allocations.
- [ ] The maximal Poiyomi inspector meets approved responsiveness, allocation,
  memory, and background-work cancellation budgets.

## Phase 14 - Documentation, Release, And Maintenance

- [ ] Update the Unity conversion integration guide with the actual supported
  Poiyomi version and parity table.
- [ ] Document every native-equivalent integration and semantic difference.
- [ ] Document conversion report codes and remediation steps.
- [ ] Document how static versus animated properties affect variants.
- [ ] Document sampler/descriptor limits and behavior for over-limit materials.
- [ ] Publish the pinned Thry/Poiyomi UI-usage catalog and a parity table mapping
  every active annotation and reachable workflow to its XRENGINE equivalent.
- [ ] Add an uber material authoring guide covering hierarchy, search,
  conditions/actions, specialized controls, presets, clipboard, texture tools,
  decal positioning, cross-material editing, linking, localization, notes,
  animation, and variant preparation.
- [ ] Document schema/action security, safe links, remote-content policy,
  generated-asset path rules, undo boundaries, and reimport/override behavior.
- [ ] Document how third-party shaders register custom controls and tools
  without arbitrary reflection or shader-supplied code execution.
- [ ] Add a contributor guide for updating the property catalog for a new
  Poiyomi release.
- [ ] Add a source-version audit tool to detect upstream property/pass changes.
- [ ] Require a catalog diff and fixture update before declaring another
  Poiyomi version supported.
- [ ] Record adapted-code attribution and license notices.
- [ ] Update generated MCP/docs outputs only if public tools or editor workflows
  change.
- [ ] Include what changed, why, validation, risks, native-equivalent behavior,
  and remaining unsupported integrations in PR notes.

Acceptance criteria:

- [ ] Public documentation and the converter report agree with executable
  support.
- [ ] Future upstream changes are detectable without manually diffing a multi-
  megabyte shader.

## Master Feature-Parity Audit

This checklist is the final release audit. Phase completion does not imply
parity until the corresponding item below has a fixture, mapping classification,
runtime implementation or explicit inactive classification, and validation.

### Color And Normals

- [ ] Main color/texture and adjustments.
- [ ] Main normal map and normal correction.
- [ ] Detail color and detail normal.
- [ ] Full alpha options.
- [ ] Decals 0-3.
- [ ] Backface controls.
- [ ] RGBA color/normal/PBR/emission masking.

### Shading

- [ ] Light data, AO, detail shadow, and shadow masks.
- [ ] Texture Ramp shading.
- [ ] Multilayer Math shading.
- [ ] Wrapped shading.
- [ ] Skin shading.
- [ ] ShadeMap shading.
- [ ] Flat shading.
- [ ] Realistic shading.
- [ ] Cloth shading.
- [ ] SDF shading.
- [ ] Anisotropics.
- [ ] Matcaps 0-3.
- [ ] Cubemap/reflection probes.
- [ ] Rim lighting 0-1 and depth rim.
- [ ] Subsurface scattering.
- [ ] Reflections and first/second specular.
- [ ] Clear coat.
- [ ] Environmental rim.
- [ ] Stylized reflections.
- [ ] Backlight.
- [ ] LTCGI native adapter/classification.
- [ ] Light Volumes native adapter/classification.

### Outlines

- [ ] Inverse-hull pass.
- [ ] Basic, Rim, Directional, and Drop Shadow modes.
- [ ] Width/mask/texture/color/emission/color-adjustment controls.
- [ ] Distance alpha, screen width, lighting, and Z offset.
- [ ] Vertex-color controls.
- [ ] Cull/depth/stencil/blend/AudioLink controls.

### Special Effects

- [ ] UV tile/face discard.
- [ ] Depth bulge.
- [ ] Full dissolve family.
- [ ] Texture-array flipbook.
- [ ] Emissions 0-3.
- [ ] Glitter/sparkle.
- [ ] Pathing.
- [ ] Mirror/camera visibility.
- [ ] Depth FX/touch glow.
- [ ] Stats overlay classification/implementation.
- [ ] Proximity color.
- [ ] Internal parallax effects.
- [ ] Video/CRT/Gameboy effects.
- [ ] Voronoi.
- [ ] Truchet.
- [ ] Material post-processing.

### AudioLink

- [ ] Master AudioLink provider and overrides.
- [ ] Decal spectrum.
- [ ] Volume color.
- [ ] Per-feature AudioLink consumers.

### Vertex

- [ ] Basic/fun vertex manipulation.
- [ ] Look-at.
- [ ] Glitching.
- [ ] Vertex-color inputs and masks.
- [ ] Uzumore/view-clip prevention.
- [ ] Bounds, shadows, depth, outlines, and motion-vector agreement.

### Global Data And Modifiers

- [ ] Blacklight masking.
- [ ] Global themes 0-3.
- [ ] Global mask textures 0-3 and all RGBA channels.
- [ ] Vertex/backface/mirror/camera/distance global mask modifiers.

### UV And Sampling

- [ ] UV0-UV3.
- [ ] Deliot-Heitz stochastic sampling.
- [ ] Hex-tile stochastic sampling.
- [ ] UV distortion and AudioLink modulation.
- [ ] Local/world projection.
- [ ] Panosphere.
- [ ] Polar UVs.
- [ ] Standard parallax heightmapping/POM.

### Rendering And Animation

- [ ] World AO blocker behavior/classification.
- [ ] Every Poiyomi render preset.
- [ ] RGB/alpha blend factors and operations.
- [ ] Depth, cull, color mask, offset, fog, and queue state.
- [ ] Front/back/outline stencil state.
- [ ] GPU instancing and multi-view behavior.
- [ ] EarlyZ, base, additive-equivalent, outline, and shadow participation.
- [ ] Locked/unlocked material animation bindings.
- [ ] Renamed-animated property bindings.
- [ ] Static property specialization and variant prewarming.

### ThryEditor / ImGui Material Authoring

- [ ] Pinned active annotation, metadata-option, context-action, auxiliary-tool,
  and workflow inventory.
- [ ] Stable-ID schema tree with ordered nested sections and referenced
  properties.
- [ ] Persistent/default expansion, simple/advanced display, clipping, and
  scalable rendering of the full property tree.
- [ ] Localized/raw-name search with ancestor reveal, navigation, highlighting,
  and state restoration.
- [ ] Localized/rich labels, tooltips, help boxes, warnings, author/help links,
  alternatives, spacing, headers, separators, and borders.
- [ ] Compiled show/enable/enable-children conditions with dependency tracking,
  texture/render-queue/animation operands, cycle detection, and diagnostics.
- [ ] Typed property/tag/shader/URL/editor action graph with complete render-
  preset side effects, validation, rollback, and one undo step.
- [ ] Unity built-in property annotations used by the pinned shader.
- [ ] `ThryWideEnum`, `ThryToggle`, `ThryToggleUI`, `MultiSlider`, vector/
  component controls, `ThryMask`, and multi-property controls.
- [ ] Poiyomi `ButtonVector` and `ThryMultiFloatButtons` controls.
- [ ] `ThryTexture`, texture layouts/options/references, `TextureKeyword`, and
  texture semantic/import feedback.
- [ ] `ThryRGBAPacker` compact and advanced workflows with non-destructive
  recipes and deterministic asset generation.
- [ ] Gradient, curve/multi-curve, ramp, and texture-array authoring tools.
- [ ] Decal positioning viewport/raycast/gizmo workflow.
- [ ] Full/section/property presets, collections, search, preview/revert,
  sequencing, inclusion, and cross-shader semantic application.
- [ ] Property/section/material copy, paste, Paste Special, reset, non-default
  status, source/default inspection, and versioned clipboard.
- [ ] Static/animated/renamed state, animation-record auto-marking, keyframe/
  binding commands, do-not-animate/lock/rename policy, and binding safety.
- [ ] Native optimize/prepare/prewarm status and per-material/batch manager
  workflows replacing destructive Unity shader locking.
- [ ] Mixed-value multi-selection and Cross-Shader Editor equivalent with
  compatibility/skipped-value reporting.
- [ ] Persistent, cycle-safe material linking by semantic property ID.
- [ ] Shader translation preview, material cleanup, texture-use finder,
  unlocked/unprepared material list, and exact-property navigation.
- [ ] Locale catalogs/fallback/override authoring and persistent searchable
  material/property notes.
- [ ] Allowlisted custom widget, external texture tool, and editor-command
  registration with explicit unsupported-node UI.
- [ ] Safe URL/remote-content/output-path policy and no implicit fetch or
  arbitrary code/reflection execution.
- [ ] Unified undo/redo, dirty/save, generated-asset, selection-lifetime,
  reimport, imported/local-override, and schema-migration contracts.
- [ ] Stable ImGui identity, responsive high-DPI layouts, bounded caches and
  previews, cancellable background work, and approved draw/allocation budgets.

## Dependency And Risk Register

### Shader Variant Explosion

- [ ] Measure combinations before adding each repeated feature family.
- [ ] Prefer static counts, dependency pruning, and slot-local specialization.
- [ ] Define prewarm and cache budgets for imported avatars.
- [ ] Report pathological variants instead of compiling unbounded combinations
  during rendering.

### Sampler And Descriptor Limits

- [ ] Record guaranteed OpenGL and Vulkan limits used by the engine.
- [ ] Validate high-slot Poiyomi materials against those limits during import.
- [ ] Use arrays/tables only when texture semantics and sampler state remain
  faithful.
- [ ] Never drop a texture silently to fit the limit.

### Transparency And Pass Differences

- [ ] Treat Unity blend/depth behavior as explicit state, not a broad
  transparency label.
- [ ] Document where Forward+ replaces Unity ForwardAdd semantics.
- [ ] Validate depth sorting and OIT choices against each source preset.

### Locked Shader And Version Drift

- [ ] Keep version signatures and renamed-property rules in data catalogs.
- [ ] Fail safely when the source version cannot be identified.
- [ ] Keep raw source values so a future catalog can reconvert the asset.

### Third-Party Runtime Inputs

- [ ] Keep AudioLink, LTCGI, Light Volumes, mirror, blacklight, and game-specific
  inputs behind explicit engine service contracts.
- [ ] Report unavailable providers once per material/import, not once per frame.
- [ ] Avoid sampling uninitialized or stale integration resources.

### Cross-Backend And VR Divergence

- [ ] Validate feature math, derivatives, array/cube sampling, clip/discard,
  depth, and stencil on OpenGL and Vulkan.
- [ ] Validate inverse-hull and vertex features per eye.
- [ ] Keep VR permutations in the same feature/parity test matrix.

### Imported UI Metadata And External Actions

- [ ] Treat labels, conditions, actions, URLs, editor IDs, file names, locale
  data, presets, and clipboard payloads as untrusted imported input.
- [ ] Parse with bounded depth/length/count limits and fail individual nodes
  visibly without preventing safe material inspection.
- [ ] Allowlist engine editor/tool commands and require safe-link/remote-content
  policy rather than porting Thry's reflection or implicit network behavior.
- [ ] Pin the embedded implementation because metadata grammar and side effects
  can change independently of the Poiyomi shader version string.

### Inspector Scale And State Consistency

- [ ] Avoid evaluating, formatting, or submitting hidden nodes in the thousands-
  property Poiyomi inspector.
- [ ] Keep schema, condition, search, locale, preview, and variant caches
  separately invalidatable and bounded.
- [ ] Test rapid selection, reimport, locale, mode, filter, and variant changes
  for stale IDs, references, conditions, previews, or focus.
- [ ] Keep one source of truth for semantic values so ordinary, cross-material,
  linked, preset, animation, and viewport editors cannot drift.

### Generated Authoring Assets And Reimport

- [ ] Keep gradients, packed textures, arrays, presets, notes, links, and local
  overrides in versioned assets/metadata with explicit ownership.
- [ ] Validate overwrite and output roots, preserve recipes and dependencies,
  and make partially failed/cancelled generation recoverable.
- [ ] Prevent source reimport from deleting generated/local authoring work or
  silently reapplying stale imported Thry actions.
- [ ] Define cleanup/orphan handling for generated outputs when materials or
  source assets are moved or deleted.

### Licensing And Test Assets

- [ ] Verify the license of every copied algorithm, source fragment, test
  material, texture, and reference image.
- [ ] Keep required attribution with adapted MIT-licensed Poiyomi code.
- [ ] Do not add avatar assets without explicit redistribution rights.

## Final Definition Of Done

- [ ] The pinned 9.3.64 property/pass inventory contains no unclassified
  runtime-visible entries.
- [ ] The pinned Poiyomi/Thry UI-usage inventory contains no unclassified active
  annotation, metadata option, context action, or reachable material workflow.
- [ ] Every enabled source feature is exact, a reviewed native equivalent, or
  explicitly preserved inactive with a diagnostic.
- [ ] No unsupported property is silently ignored.
- [ ] Locked and unlocked versions of equivalent materials produce equivalent
  descriptors, output, and animations.
- [ ] All render presets, passes, texture roles, UV channels, repeated slots,
  vertex effects, and animation paths pass their automated contracts.
- [ ] Reference scenes pass approved visual comparison on OpenGL and Vulkan.
- [ ] Standard, OpenVR, and OpenXR variants compile and render correctly.
- [ ] Import, variant preparation, binding, and render submission meet approved
  CPU/GPU and allocation budgets.
- [ ] Inspector/reporting accurately identifies native equivalents and missing
  external services.
- [ ] The ImGui uber inspector provides reviewed native equivalents for all
  ThryEditor capabilities exercised by Poiyomi Toon; active specialized
  annotations never silently fall back to a generic field.
- [ ] Nested UI, conditions/actions, specialized controls, search/localization,
  presets/clipboard, texture/curve/array/decal tools, multi-material editing,
  linking, cleanup/navigation, animation, and optimization workflows pass their
  functional acceptance tests.
- [ ] Every inspector and auxiliary-tool mutation is undoable, save/reimport-
  safe, type-compatible, and deterministic across single- and multi-material
  use.
- [ ] The maximal material inspector meets approved response-time, allocation,
  memory, background cancellation, and security budgets.
- [ ] Documentation, license attribution, fixture manifests, and maintenance
  tooling are complete.
- [ ] The existing "best-effort" warning is replaced by an exact versioned
  support statement backed by the generated parity report.
