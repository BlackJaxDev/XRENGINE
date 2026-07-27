# Poiyomi Toon Material Conversion

XRENGINE provides versioned conversion support for **Poiyomi Toon 9.3.64** at
commit `c5aaeeb3a67782b7e8a26e184d5e0a1970792294`. Other versions are not accepted
as equivalent: an unknown version produces `POI0001`, preserves inspectable
source data, and does not silently opt into the pinned converter.

## Support Contract

Every source feature and property has one of three outcomes:

| Outcome | Meaning |
| --- | --- |
| Exact | Authored values, render state, bindings, and shader behavior are represented directly. |
| Native equivalent | The material intent is implemented through an XRENGINE subsystem with a documented semantic difference. |
| Preserved inactive | The value remains in versioned source metadata and the report explains why no runtime path was enabled. |

There is no best-effort mode for recognized 9.3.64 materials. The conversion
report is the authority for the outcome of each enabled feature. Unsupported or
unavailable integrations are visible and never sampled from fabricated data.

## Runtime Parity

| Feature set | XRENGINE outcome | Important semantic difference |
| --- | --- | --- |
| Base color, normal, alpha, masks, detail, decals, emissions, flipbook, dissolve, parallax | Exact portable shader modules | Unity texture metadata is normalized into explicit engine sampler and color-space metadata. |
| Toon ramps and shading modes | Native equivalent | Evaluated with XRENGINE Forward+ light records rather than Unity ForwardBase/ForwardAdd macros. |
| PBR, specular, clear coat, reflections | Native equivalent | Uses engine BRDF, reflection probes, and environment services. |
| Matcaps, rim, backlight, subsurface, glitter | Native equivalent | Uses stereo-safe engine view vectors and portable derivative/noise behavior. |
| Outlines | Native equivalent | An inverse-hull engine material pass shares the same authored state and pass identity. |
| Render presets, blend/depth/stencil/fog/queue state | Exact where representable; otherwise reported native difference | Unity ForwardAdd is folded into Forward+ lighting rather than emitted as an additive light pass. |
| Static and animated properties | Exact intent | Static values specialize shader source; animated values remain live uniforms and bindings. |
| Locked and unlocked materials | Exact descriptor equivalence | XRENGINE prepares variants without destructive Unity shader locking. |
| AudioLink | Native adapter | Requires `IPoiyomiAudioLinkProvider`; absent providers preserve state and emit one material diagnostic. |
| LTCGI and Light Volumes | Native adapter | Requires `IPoiyomiEnvironmentProvider`; no provider means preserved inactive. |
| Mirror and camera visibility | Native view-context adapter | Uses `PoiyomiViewContextScope` and stereo-safe engine view flags. |
| VRChat, Udon, and game-specific inputs | Preserved inactive unless an explicit service exists | No Unity/VRChat runtime code is executed inside the engine. |

The exhaustive property, annotation, and workflow inventory is embedded as
`Importers/Poiyomi/Catalogs/poiyomi-toon-9.3.64.json`. The generated human-
readable table is [Poiyomi Toon 9.3.64 Parity](../../reference/rendering/poiyomi-toon-9.3.64-parity.md).

## Static, Animated, And Renamed Properties

Properties default to `Static`. Their current values become deterministic GLSL
literals and participate in the variant hash. Changing a static value requests a
new variant and prewarm entry.

`Animated` properties stay as uniforms. Animation import remaps Unity property
names to stable semantic bindings. Renamed animated properties keep the source
alias and the semantic destination so locked and unlocked materials resolve the
same binding. Moving a property back to static captures its last runtime value.

Feature membership remains compile-time removable. A disabled feature retains
its authored values but contributes no live code, samplers, descriptors, or
animation uniforms.

## Sampler And Descriptor Limits

The guaranteed baseline for both OpenGL 4.6 and Vulkan 1.0 is 16 fragment
samplers, 16 sampled images, and 16 KiB of uniform data. Vulkan additionally
guarantees 128 bytes of push constants.

`UberMaterialBindingPlanner` uses the first faithful rung:

1. direct samplers;
2. compatible texture arrays;
3. the material texture table;
4. bindless descriptors;
5. explicit unsupported result.

Arrays are used only when texture role, dimensionality, color space, and sampler
state remain compatible. The converter never drops textures to meet a limit. An
over-limit material receives a deterministic failure reason before rendering.

## Conversion Diagnostics

| Code | Meaning | Remediation |
| --- | --- | --- |
| `POI0001` | Source version is unknown. | Use the pinned 9.3.64 source or add a reviewed catalog for the new version. |
| `POI0002` | Locked shader matched only by an ambiguous signature. | Restore the original shader GUID/version metadata and reimport. |
| `POI0003` | Runtime-visible property lacks classification. | Update the catalog and parity fixture; conversion must not ship in this state. |
| `POI0004` | Catalog identity/hash mismatch. | Regenerate from the pinned commit and review the source audit. |
| `POI0005` | Runtime integration is unavailable. | Register the required provider or accept preserved-inactive behavior. |
| `POI0006` | Recognized runtime mapping is unavailable. | Inspect preserved source metadata and add a semantic mapping if runtime behavior is required. |
| `POI0007` | Source value could not be preserved. | Correct malformed YAML/metadata; this is a conversion error. |
| `POI0008` | Render state has an intentional native difference. | Review the pass-state section of the report and visual references. |
| `POI0009` | Animation binding was not mapped. | Restore the source property/rename metadata or add a catalog alias. |
| `POI0010` | Intentional native equivalent. | Review the documented semantic difference; no action is normally required. |
| `POI0011` | Asset reference is missing. | Restore the referenced Unity asset or replace the material slot. |
| `POI0012` | Texture asset is incompatible with its role. | Correct dimensionality/import classification and reimport. |
| `POI0013` | Requested UV channel is absent. | Import a mesh containing that UV channel or change the material selector. |
| `POI0014` | Enum value is outside the pinned schema. | Correct the source value or add a reviewed version-specific mapping. |
| `POI0015` | Pass prewarm failed. | Inspect shader diagnostics; the last-known-good variant remains active. |

## Authoring And Security

The ImGui material inspector compiles the pinned UI catalog into a stable-ID
schema tree. It supports nested sections, raw/localized search, conditions,
typed actions, specialized controls, presets, versioned clipboard data, texture
packing/arrays, gradients/curves, decal positioning, multi-material editing,
semantic links, notes, animation modes, and variant preparation.

Imported labels, expressions, actions, URLs, paths, locale data, presets, and
clipboard payloads are untrusted. Parsing is bounded. Shader metadata cannot
instantiate types, invoke reflection, execute code, fetch remote content, or
write outside approved asset roots. URL, remote-content, editor-command, and
generated-output operations require explicit allowlisted policy. Mutations use
one undo transaction and generated assets are committed atomically.

Third-party shaders extend the editor by registering an engine-owned widget or
tool ID with `ShaderAuthoringWidgetRegistry` during editor startup. The
registration supplies a typed capability and engine delegate. Shader source can
reference the ID but cannot provide executable code. Unknown IDs render a visible
unsupported node and remain inert.

## Reimport And Overrides

Imported values and local overrides are separate layers. Reimport refreshes the
source descriptor, report, and untouched imported values while preserving local
notes, links, recipes, generated assets, and explicit overrides. Preview state
is transient and never persists across restart. Schema migration uses stable
semantic IDs; missing nodes are reported instead of rebound by display label.

## Validation

Run the complete automated and live-backend acceptance path:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Validate-PoiyomiParity.ps1
```

The runner tests the corpus and contracts, compiles OpenGL/Vulkan and VR shader
permutations, waits for non-empty final targets, captures multiple camera views,
records profiler and streaming telemetry, and rejects shader, validation, or
resource-lifetime errors.
