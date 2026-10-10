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

## Browser capability source locations (2026-10-03)

`BrowserCapabilityReport.json` keeps its schema-1 scene, node, component,
material, pass, and reason fields. It adds optional `sourcePath`, `sourceLine`,
and `sourceColumn`. Paths beneath the known game and engine asset roots are
published as `/game/` or `/engine/` identities; validated relative artifact
identities remain relative. Unknown absolute machine paths are omitted from
the report and redacted from reasons. A frontend
`ShaderCompilationException` supplies its exact original path, line, and
column when that path can be represented truthfully. File-only findings omit
line and column. If a compiler file cannot be published as a known source,
its coordinates are also omitted rather than attributed to the fallback
world file. The original exception remains available to the local Editor.

Startup and streamed-scene dependency cooks can fail during material
projection before `BrowserWorldCapabilityAudit` runs. The projection attaches
material, pass, and shader-file provenance at the failure site; the owning
Editor cook records the finding and rethrows. Report disposal persists an
`incomplete` report under the project `Intermediate/Build` directory. A
completed preflight still saves `blocked` or `no-known-required-findings`.

Validation used the production `BrowserBuildState.Prepare` and
`ExportAuthoredWorld` methods through a disposable driver:

- The negative RollingBall world produced a `blocked` report with three
  required and two optional findings. Its shadow finding names
  `/game/Worlds/RollingBallWorld.asset` without invented coordinates. The
  canonical source snapshot verified all 187 input files unchanged. Evidence:
  `Build/_AgentValidation/20261001-225000-lit-surface/logs/diagnostic-negative-cook.log`
  and `scratch/diagnostic-report-negative/project/Intermediate/Build/BrowserCapabilityReport.json`
  under that run root.
- An isolated five-material coverage fixture with a valid engine-only shader
  manifest failed at `BrowserCook.AuthoredLitCookMissing`, before world audit.
  Its persisted `incomplete` report names `Coverage-Opaque`, pass
  `forward-coverage`, the missing recipe reason, and
  `/engine/Shaders/Common/StandardLitColorCoverageForward.fs`; line and column
  are absent. The copied source `World.asset` hash was checked unchanged across
  a repeat export. Evidence: `logs/diagnostic-missing-material-cook-proof.log`
  and `scratch/diagnostic-missing-cook-fixture/Intermediate/Build/BrowserCapabilityReport.json`
  under the same run root.
- A focused report-serialization probe supplied a constructed
  `ShaderCompilationException`. It retained line 37, column 19, material,
  pass, and a normalized engine source path. An unknown absolute compiler
  path was redacted with no fabricated path or coordinates. This exercises
  report serialization, not a real failing Slang compile. Evidence:
  `logs/diagnostic-line-probe.log` and
  `scratch/diagnostic-line-probe/output/Build/BrowserCapabilityReport.json`
  under the same run root.

The coordinated Editor Release/AnyCPU build passed with zero warnings and
errors under the existing shared build lock; its log is
`Build/_AgentValidation/20261001-225000-lit-surface/logs/diagnostic-final-editor-build.log`.
A focused Editor recompile after the unknown-path refinement also passed.
These probes do not establish live browser rendering, pixel correctness, or
complete generator and feature coverage. UR05.05 remains open.

The exact changed-file group for this diagnostic slice is:

- `XREngine.Editor/Publishing/BrowserCapabilityReport.cs` — optional
  source fields, normalization, compiler positions, and partial reporting
- `XREngine.Editor/Publishing/BrowserMaterialCookProjection.cs` — failure
  provenance and the coordinated `AuthoredLitV2` detached projection and
  canonical seven-snippet verification
- `XREngine.Editor/Publishing/BrowserWorldCapabilityAudit.cs` — source
  identities and the coordinated `AuthoredLitV2` depth-normal admission
- `XREngine.Editor/Publishing/BrowserRenderingCapabilityAudit.cs` — source
  identities for output and material/pass findings
- `XREngine.Editor/ProjectBuilder.BrowserEngineAssets.cs` — startup-world
  cook failure capture and asset-root normalization inputs
- `XREngine.Editor/ProjectBuilder.BrowserStreamedScenes.cs` — streamed-scene
  cook failure capture
- `docs/work/progress/platform/browser-authored-lit-material-cooking-2026-10-02.md`
  — this durable validation record

The projection and audit files also carry the coordinated authored coverage
implementation. Publish them with that coherent `AuthoredLitV2` group; the
diagnostic slice alone does not represent the admitted coverage contract.

## Boundaries and next work

The project still prepares the per-material MaterialRecipe with the standalone
ShaderCooker before browser export. Editor publishing does not implicitly run
the pinned Slang toolchain or mutate the original authored source to produce a
missing artifact. General authored GLSL and Uber feature lowering, arbitrary
masked or blended material recipes, skinning, and material batching remain
open. Bounded authored coverage and shadow behavior belong to the coordinated
[`AuthoredLitV2` coverage contract](browser-authored-color-coverage-2026-10-03.md)
and require their own qualification. The scratch driver
validated export and cooked reload; its optional full package step was not run
because that isolated driver did not generate
`AotRuntimeMetadata.bin`. Browser pixel qualification and desktop
OpenGL/Vulkan comparison remain open before closing UR05.03.
