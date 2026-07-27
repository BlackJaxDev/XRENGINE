# Poiyomi Material Authoring Architecture

material authoring replaces the flat Poiyomi material view with an engine-native,
manifest-driven authoring system. It consumes the embedded Poiyomi Toon 9.3.64
catalog and never loads Unity editor assemblies or reflects drawer type names.

## Authoring schema

`PoiyomiAuthoringSchemaCatalog` compiles the pinned catalog once per
`ShaderUiManifest`. The resulting tree preserves source declaration order and
`m_start`/`m_end` plus `s_start`/`s_end` nesting. Each node has a stable semantic
ID, source identity, widget classification, typed `PropertyOptions`, compiled
condition expressions, explicit references, and source diagnostics.

Malformed markers, references, conditions, unsupported tools, and unknown
options remain visible diagnostics. Unsupported nodes are never reinterpreted
as generic executable editor metadata.

The expression compiler supports arithmetic, ordered/equality comparisons,
grouping, inversion, `&&`/`||`, and legacy `&`/`|`. Dependencies are recorded
on the compiled expression. Material contexts expose parameter values, texture
presence, render pass, static/animated state, and allowlisted capability tokens.

## ImGui inspector

The Properties tab detects the Poiyomi-backed uber manifest and renders:

- nested, persistent sections keyed by user and schema fingerprint;
- simple/advanced and unsupported-node modes;
- search over labels, alternative labels, source names, semantic IDs,
  tooltips, and ancestors;
- exact semantic/source-property reveal;
- clipped submission for the large pinned tree;
- conditionally visible/enabled controls;
- stable semantic ImGui IDs, animation/static mode, reset/copy context actions,
  tool status, diagnostics, and annotation inspection;
- prepare and rebuild controls backed by native uber variants.

Selecting multiple materials opens the Cross-Shader Material Editor. It builds
compatibility by semantic ID and GLSL value contract, shows mixed values and
accepted target counts, and applies primary values through one preflighted undo
transaction with one variant request per resulting material.

## Transactions and persisted payloads

`MaterialAuthoringTransaction` is the shared preflight/undo boundary for
ordinary authoring operations, action lists, presets, linked values, generated
textures, and decal tools. It validates every step before mutation, tracks all
targets in one undo scope, rolls back an unexpected apply failure, marks assets
dirty, and coalesces variant invalidation.

Preset and clipboard payloads are versioned and store semantic IDs rather than
Unity property names. Preset preview reports unavailable or incompatible
semantic values before application. Expansion and local note storage is also
versioned; schema fingerprints prevent stale state from crossing upgrades.

## Safe actions and tools

The widget and editor-command registries are closed allowlists. Imported
`ThryCustomGUI`, external editor type names, and unknown tool IDs cannot execute.
External help targets require absolute HTTPS URLs and an editor confirmation
handler. Merely opening an inspector performs no network or process operation.

The reusable texture backend includes versioned four-channel packing recipes,
image/constant/gradient sources, explicit graph wiring, the pinned image-operation
set, linear/sRGB intent, deterministic previews, gradient and curve baking,
ordered texture-array generation, cancellation, dependency manifests, approved-
root validation, overwrite policy, and PNG/JPEG/EXR encoding. Generated files,
sidecars, imports, sampler assignment, and referenced frame counts participate in
the shared structural transaction boundary.

Decal positioning is represented by a bounded preview session: live previews
may update a material, cancel restores the exact initial transform, and commit
uses the shared undo/variant transaction. Material link groups prevent
re-entrant propagation and use the same transaction boundary.

## Completion services

The ImGui workspaces expose rendering state, presets and Paste Special, texture
packing, gradients/curves/ramps, texture arrays, decal controls, semantic links,
cleanup, locales/notes, and variant optimization. The native service layer also
provides:

- exact base/add/outline blend, depth, cull, queue, and tag action adapters for
  every field in the pinned `_Mode` graph;
- versioned semantic link registries, explicit imported/preset/local value
  layers, protected cleanup reports, and failure-isolated batch variant work;
- viewport lifetime validation, bounded preview/job ownership, safe remote
  classification, animation binding guards, and schema-load validation;
- a cached search index and clipped row submission so search is linear in the
  schema size when its query changes and does not recursively rebuild per row.

`PoiyomiAuthoringParityAudit` is the executable acceptance ledger. It reads the
pinned catalog and requires every active annotation plus every reachable menu,
auxiliary window, and inspector workflow to have a classification, owner,
native behavior, and validation identity.
## Validation

The focused material authoring fixtures validate the real embedded 3,000+ node catalog,
stable ordering and semantic mapping, expression dependencies/coercion, closed
widget registration, the complete pinned audit, `_Mode` action coverage and
native render-state rollback, deterministic texture authoring, structural undo,
locale/remote safety, layered state, and preset/clipboard/link round-trips.

The focused editor build and material authoring test fixture are the minimum regression
gate for changes to this subsystem.
