# Authored lit material cooking (2026-10-02)

## Scope

This is a bounded source-to-browser slice of UR05.03 and UR05.05, not completion
of general shader generation or material batching. An engine-created
`XRMaterial` explicitly owns the `AuthoredLitV1` opaque PBR contract while its
original desktop GLSL stage remains in the authored asset. The target planner
reads that material's six numeric factors, surface texture roles and Uber
authored state. It selects tint, base-color texture or normal-mapped texture
PBR features, and emits a per-material schema-2 `.material.json` source for a
schema-3 WebGPU recipe named `mat-<persistent-material-guid>`.

The standalone ShaderCooker pins the canonical engine Slang source and selected
includes, compiles WGSL for the selected target, verifies the complete physical
ABI, and publishes a content-addressed descriptor. The browser publisher
requires that descriptor in the selected project shader manifest before export.
It verifies the original canonical desktop fragment and creates a detached
browser-target material with exact stage-companion identity. The original
material, shader text, YAML and texture objects are not changed. A missing,
stale, unsupported or ambiguous cook fails with a material-specific reason.

The texture projection uses a v2 typed derived-cache payload to retain the
companion stage type and descriptor identity while writing aliased image bytes
once. The v1 built-in payload remains readable and its writer is unchanged;
older readers reject v2 at the version field. This is a new-feature-only
derived-cache format, not a migration of authored assets or user caches.

## Checks performed

- ShaderCooker, WebGPU runtime and Editor Release graph builds passed with zero
  warnings and zero errors using the existing `ui-shaders/temp-build` and
  `desktop-build` roots. The Editor command used
  `EnableWindowsTargeting=true` and `Platform=AnyCPU` on Linux
- Three disposable per-material recipes (tint, texture, normal texture)
  compiled from canonical Slang to WGSL and passed schema-3 physical ABI
  validation in `Build/_AgentValidation/20261001-225000-lit-surface/scratch/authored-lit-cook`
- A factory-created authored opaque PBR textured material with metallic factor
  0.25 and one image in base-color, metallic and roughness roles was saved as
  `XRWorld` YAML, cooked from its material recipe, projected during real
  `ExportAuthoredWorld`, and reloaded from the cooked world. The original
  `World.asset` remained SHA-256
  `056d156aa2293715132625b5d997162032489a104de6f2e71bc35d443817ac1e`.
  The cooked carrier retained `AuthoredLitV1`, one exact stage descriptor
  `1e8a468ce20209c73659ae04bd859938ae4248782d7236dfcd148d02a9e241d1`,
  all image aliases, and all six PBR factors. The export listed 83 assets and
  40 shader artifacts. Log: `scratch/authored-lit-cook/authored-world-cook.log`
- A factory-created authored color material with BaseColor (0.4, 0.7, 0.9),
  Specular 0.8, Roughness 0.35, Metallic 0.15 and Emission 0.1 passed the same
  YAML → material cook → detached stage companion → cooked-world reload path.
  Its source `World.asset` stayed SHA-256
  `fc27c38b38349f33bffaa309c79c1a847cb073f4d419a16d0e5134ed78512537`;
  its exact descriptor is
  `ac455836a8ebe769698afbceae4fac2b90ebe6c0f22f25aba590423a747f69c6`.
  After hydration, changing Roughness to 0.62 advanced BindingValueVersion
  and read back correctly; changing the stage identity cleared `AuthoredLitV1`
  and advanced ShaderStateRevision. The authored-only post-cooked hook restores
  subscriptions that generic reflection hydration suppresses. Log:
  `scratch/authored-lit-cook/authored-color-world-cook.log`
- The v1 writer body was byte-compared against a disposable copy of its
  pre-change implementation on the same material and texture instance. Both
  produced exactly 5,048 bytes, SHA-256
  `5b52fba35cb36a8ffdebb2a3da951de4de704b6d2fa7149ecee3d61609e6ee35`.
  Separate freshly created graphs were not used for the equality claim because
  a nested runtime identity differed between them. The same-instance proof is
  recorded in `scratch/authored-lit-cook/v1-byte-probe/equal-body.txt`

## Boundaries and next work

The project still prepares the per-material MaterialRecipe with the standalone
ShaderCooker before browser export. Editor publishing does not implicitly run
the pinned Slang toolchain or mutate the original authored source to produce a
missing artifact. General authored GLSL and Uber feature lowering, masked or
blended materials, authored shadow casters, skinning, and material batching
remain open. The scratch driver validated export and cooked reload; its optional
full package step was not run because that isolated driver did not generate
`AotRuntimeMetadata.bin`. Browser pixel qualification and desktop
OpenGL/Vulkan comparison remain open before closing UR05.03.
