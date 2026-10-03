# Generated opaque PBR shadow companions

Canonical generated `AuthoredLitV1` color, independent-texture and RGB-normal
surfaces can receive and cast the existing bounded WebGPU standalone shadows.
The shared material keeps its persistent identity, authored stages, semantic,
render pass, render options, factors and texture roles. The backend selects an
equivalent cooked receiver program; it does not retag or serialize a replacement
material.

## Exact source and companion proof

Selection reuses the content owner's generated-surface proof used by native
publication. Every retained stage must resolve to one verified whole program
whose generated name matches the material's persistent ID. The original
physical PBR ABI and complete pinned canonical source closure must match.
Arbitrary GLSL, retained source text, unknown material extensions, billboard
behavior and custom vertex callbacks do not acquire this route through a tag.

The existing material-variant catalog selects six possible receiver companions:
directional-only and directional/point/spot receivers for each of color,
texture and normal texture. The original vertex strides remain 24, 32 and 48
bytes. Directional receiver resource counts are 9, 15 and 17; local receiver
counts are 14, 20 and 22. View, object, material factors, independent texture
roles, AO and forward lighting retain their original binding contracts.

Each selected companion is checked against its verified descriptor and module,
exact catalog declaration, canonical Slang frontend path, pinned active source
hashes and complete physical ABI. Ordinary Slang descriptors can retain
unrelated watched files from the staging tree, but those do not substitute for
the actual frontend or its active dependencies. Proof parsing and hashing are
cached by immutable artifact identity/ownership. Live source identity, factors,
roles and vertex restrictions are checked again without allocating.

The shadow frontend preserves the generated PBR evaluation, normal convention,
independent scalar maps, AO, ambient and emission. Only the selected direct
light contributions acquire shadow visibility. The local-shadow companion is
preferred when installed; a directional-only companion remains usable when no
local shadow is selected. Missing required support reports the exact variant
and a recook diagnostic. Older generated descriptors remain readable in their
existing unshadowed raster path; conservative old generated provenance cannot
silently authorize lowering.

## Shared casting and audit

The shared cooked caster resolver admits the same proven opaque V1 source to
the canonical depth-only directional, radial-R16F point and projected-R16F spot
programs. The selected light override retains its existing shadow raster and
uniform ownership. Direct and GPU material selection call the same resolver;
there is no second source-free-color-only acceptance rule.

Direct and generic authored indexed draws consume the same receiver/caster
selection and GPU skin/morph position stream. Normal/tangent streams remain
available to textured receivers, while caster programs consume the same
deformed position. This change does not alter submission strategy or add CPU
deformation or fallback. The separate generic indirect integration has its own
validation record; shadow selection alone does not establish that integration.

The cold world audit records every V1 material rather than collapsing all three
profiles into one semantic flag. A shadowed world must prove each generated
source, its selected receiver and every required caster companion. Failures
name the scene, material, mesh and pass. The existing finite profile remains:
at most one standalone map of each type, directional normal-Z depth without
cascades or atlas, sequential point cubemap capture, projected spot depth, PCSS
8/8, and the existing dimensions and format checks.

Both typed texture carrier versions, generic authored serialization, original
generated raster modules, desktop GLSL and dependencies are unchanged.

## Validation boundary

The real ShaderCooker packaged 12 focused programs and 100 programs in the full
fixture. All three generated V1 modules remained byte-identical to their
canonical unshadowed counterparts. The Editor graph built with zero warnings
and errors.

The final ignored production-method witness passed 310 checks, including real
detached projection, all six receiver and three caster ABI/source proofs,
actual WebGPU receiver selection, live source mutation, independent texture
roles, shared caster selection and per-material cold audit. Removing each
required local receiver or caster rejects shadowed content while preserving
unshadowed audit behavior. Missing owners, wrong material IDs, custom source,
wrong source hashes/defines/layout/ownership and old generated provenance
reject the new route. Three thousand warmed source admission calls allocated
zero bytes. The five existing V2 coverage modes still passed their unchanged
physical ABI admission.

The same witness called `ExportAuthoredWorld` and hydrated the resulting binary
world twice, preserving the exact authored Advanced and Default pipeline
sources with all three V1 material profiles and all three types of casting
lights. Export left the authored world bytes unchanged. Hydrated surfaces
retained their generated identities, exact PBR values, independent map roles
and the authored shadow-light profiles.

Disposable probe source, fixture, generated descriptors and module-equivalence
evidence are under
`Build/_AgentValidation/20261001-225000-lit-surface/scratch/authored-shadows/`.
The shared run's `logs/authored-shadows-*.log` files record the cooks and
production probes. The final probe used the coordinated zero-warning/error
Rendering and Editor outputs from
`logs/authored-decals-topology-rendering-refresh.log` and
`logs/authored-decals-topology-editor-refresh.log`, plus the successful WebGPU
output recorded by `authored-indirect/webgpu-build.log`. Earlier repeat build
logs retain temporary cross-worker interface/accessibility failures that were
resolved before this final probe. No extra full graph rebuild was required.

The exact own-file hashes and isolated hunks are recorded in
`Build/_AgentValidation/20261001-225000-lit-surface/reports/authored-shadow-hunks.json`.
The staged index remained 59 files at
`eb0ef0069abfd6c4859027d302de7db6289081cb`.

This is source, cook, managed selection and export/hydration evidence. It does
not establish live browser GPU silhouettes, numeric lighting parity, shadow
resource retirement or device performance. Those acceptance rows remain open.
