# Uber Material Authoring Architecture

The material editor shows Uber materials with an engine-native authoring tree.
The tree comes only from the annotations in the Uber shader source. An imported
source shader does not change the tree, and saved editor data contains only Uber
names.

## Authoring schema

`UberAuthoringSchemaCatalog` builds one `ShaderAuthoringSchema` for each
`ShaderUiManifest` and caches it.

- The root node has the semantic ID `uber/root`.
- Each category is a section (`uber/section/<category>`). A property uses its own
  category, or the category of its feature, or `General`.
- Each feature or subcategory in a category is a subsection
  (`uber/subsection/<category>/feature/<id>` or
  `uber/subsection/<category>/<subcategory>`).
- Each property is a property node (`uber/property/<name>`). Its display name,
  type, default, range, enum, toggle, and tooltip come from its `//@property`
  annotation.

Only annotated properties are in the tree. Engine-driven uniforms are not in the
tree. Sections, subsections, and properties keep the order of their first
declaration, and the declaration order of the nodes follows the tree order. The
schema fingerprint is a hash of the feature and property contracts.

The schema ID is `uber`. Semantic IDs contain no shader version, so presets,
clipboard data, links, translations, and preset-include overrides stay valid
when the Uber shader adds or moves properties. A saved ID that is not in the
current tree is reported as unavailable.

## ImGui inspector

The Properties tab detects the Uber manifest and renders:

- nested sections and subsections with persistent expansion state;
- search over labels, property names, semantic IDs, tooltips, and ancestors;
- exact property-name or semantic-ID reveal;
- clipped row submission for large trees;
- animation and static modes, reset and copy context actions, and diagnostics;
- prepare and rebuild controls backed by native Uber variants.

Each feature subsection has a switch in its header. The switch turns the
feature on or off through the same variant request as the toolbar. The switch is
read-only for a required feature and for a feature that is not available. When a
feature has dependencies or conflicts, turning it on first asks the user to
resolve them. When a feature is off, its header stays in the tree
and its properties are hidden. The "show disabled modules" option and an active
search show the properties of disabled features.

A property with a `visible` condition is hidden while the condition is false. A
property with an `enabled` condition is read-only while the condition is false.
The conditions come from the `//@property` annotations, and they use the shared
expression evaluator, so a condition can read property values and
`feature:<id>`. See [Uber Shader UI Annotations](uber-shader-ui-annotations.md).

The engine Transparency Mode control sets the render mode.

Selecting multiple materials opens the Cross-Shader Material Editor. It builds
compatibility by semantic ID and GLSL value contract, shows mixed values and
accepted target counts, and applies primary values through one preflighted undo
transaction with one variant request per resulting material.

## Transactions and persisted payloads

`MaterialAuthoringTransaction` is the shared preflight and undo boundary for
ordinary authoring operations, presets, linked values, generated textures, and
decal tools. It validates every step before mutation, tracks all targets in one
undo scope, rolls back an unexpected apply failure, marks assets dirty, and
coalesces variant invalidation.

Preset and clipboard payloads are versioned and store semantic IDs. Preset
preview reports unavailable or incompatible semantic values before application.
Expansion and local note storage is also versioned. Schema fingerprints prevent
stale state from crossing upgrades.

## Tools

The widget and editor-command registries are closed allowlists. Shader source can
reference a registered ID but cannot supply executable code.

The texture backend includes versioned four-channel packing recipes,
image, constant, and gradient sources, explicit graph wiring, linear and sRGB
intent, deterministic previews, gradient and curve baking, ordered texture-array
generation, cancellation, dependency manifests, approved-root validation,
overwrite policy, and PNG, JPEG, and EXR encoding. Generated files, sidecars,
imports, sampler assignment, and referenced frame counts use the shared
structural transaction boundary.

Decal positioning uses a bounded preview session. Live previews can update a
material, cancel restores the initial transform, and commit uses the shared undo
and variant transaction. Material link groups prevent re-entrant propagation and
use the same transaction boundary.

## Source shader import

The source toon importer reads its embedded source catalog only during import.
It converts each source material to Uber properties. The material editor does not
read the source catalog.

## Validation

The focused material authoring fixtures validate the Uber schema order and
semantic IDs, expression dependencies and coercion, closed widget registration,
deterministic texture authoring, structural undo, locale and remote safety,
layered state, and preset, clipboard, and link round-trips.

The focused editor build and material authoring test fixture are the minimum
regression gate for changes to this subsystem.
