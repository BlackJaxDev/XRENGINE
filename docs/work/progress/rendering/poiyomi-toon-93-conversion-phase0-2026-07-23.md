# Poiyomi Toon 9.3 Conversion Phase 0

Date: 2026-07-23  
Status: Complete

## Outcome

Phase 0 pins Poiyomi Toon 9.3.64 and its embedded ThryEditor snapshot, adds a
deterministic source-inventory generator and checked catalog, defines exact and
locked shader matching rules, establishes stable diagnostics and generated-name
contracts, and adds focused regression tests.

This phase intentionally does not implement new shader features. The generated
catalog is the parity ledger consumed by later conversion and authoring phases.

## Pinned Source

| Item | Identity |
| --- | --- |
| Repository | `https://github.com/poiyomi/PoiyomiToonShader` |
| Commit | `c5aaeeb3a67782b7e8a26e184d5e0a1970792294` |
| Commit date | `2026-01-29T18:35:05+09:00` |
| Package version | `9.3.64` |
| Shader path | `_PoiyomiShaders/Shaders/9.3/Toon/Poiyomi Toon.shader` |
| Shader GUID | `9444ce77bf4418748b1e8591b9d97f85` |
| Shader Git blob | `4e3a68b3551e63e6b6c57625669d19e86f70ac8c` |
| Shader SHA-256 | `7efb9176022291a041ecf332bf999f68ba33591d6f446e60757be83e968e61d8` |
| Embedded ThryEditor tree | `6437aeaec7b715e7fd000bfd0bdd3d6b0840c6db` |
| Poiyomi license | MIT, Copyright (c) 2023 Poiyomi Inc. |
| ThryEditor license | MIT, Copyright (c) 2022 Thryrallo |

The generator requires a caller-provided checkout. Neither the pinned shader
nor upstream art is vendored into XRENGINE.

## Generated Inventory

Generator:
`Tools/Reports/Generate-PoiyomiToon93Catalog.ps1`

Catalog:
`XRENGINE/Scene/Importers/Poiyomi/Catalogs/poiyomi-toon-9.3.64.json`

Pinned inventory:

| Surface | Count |
| --- | ---: |
| Active ShaderLab properties | 3,736 |
| Declared texture properties | 137 |
| Passes | 5 |
| Active ShaderLab annotation kinds | 41 |
| Active annotation uses | 3,797 |
| Display-option kinds, including nested action/reference metadata | 27 |
| Active display-option uses | 1,788 |
| Declarative action kinds | 2 |
| Localization keys | 3,501 |
| Reachable menus, auxiliary windows, and inspector workflows | 62 |
| Unclassified runtime properties | 0 |

The parser removes line and block comments before locating the active
`Properties` block, so commented examples do not enter the catalog. It parses
multiline declarations with string-aware delimiter tracking. Every property
records source line, type, default, active annotations, raw display options,
condition/action/reference keys, shader-body reference count, semantic
classification, initial parity, current support, target behavior, owner, and
test slots.

The pass inventory records `EarlyZ`, `Base`, `Add`, `Outline`, and
`ShadowCaster`, including tags, fixed-function state, and pragmas. Keyword
groups and active preprocessor feature symbols are cataloged separately.

Workflow coverage combines discovered `MenuItem` and editor-window entry
points with required inspector workflows whose source files are asserted by
the generator. The list includes hierarchy/context editing, presets, material
linking, optimizer locking, decal placement, texture packing, gradients,
texture-use lookup, localization, notes, Paste Special, unprepared-material
management, shader translation, cross-material editing, and cleanup.

Of the 41 active annotation kinds, 23 resolve to a pinned drawer/decorator, 13
resolve to pinned Thry metadata handling, four resolve as Unity built-ins, and
one use of `lilToggleLeft` has no implementation in the pinned Poiyomi or
ThryEditor snapshot. That row is explicitly classified `unreachable` rather
than being mistaken for supported behavior.

## Matching Decisions

Unlocked 9.3.64 is accepted with one of:

1. The exact pinned Unity shader GUID.
2. Canonical shader name plus the exact `Poiyomi 9.3.64` source marker.
3. Canonical `Shaders/9.3/Toon` path plus the exact source marker.

Optimizer-generated shaders are identified using all available evidence:

1. `OriginalShaderGUID` is authoritative when present.
2. `Hidden/Locked` or `OptimizedShaders` identity plus an exact source marker is
   accepted.
3. The required Thry/Poiyomi property signature plus optimizer marker is an
   accepted fallback that emits `POI0002`, preserving the ambiguity.

Other Poiyomi family versions are rejected from Poiyomi-specific conversion
with `POI0001`; the importer does not silently apply the 9.3.64 mapper.

## Diagnostics

| Code | Default severity | Meaning |
| --- | --- | --- |
| `POI0001` | Warning | Recognized Poiyomi family with an unknown version |
| `POI0002` | Warning | Locked shader recognized only by property signature |
| `POI0003` | Error | Runtime property lacks a parity classification |
| `POI0004` | Error | Pinned source or catalog identity mismatch |
| `POI0005` | Warning | Preserved feature is inactive because integration is unavailable |
| `POI0006` | Warning | Runtime mapping is not implemented |
| `POI0007` | Error | Source value could not be parsed or preserved |
| `POI0008` | Warning | Render state lacks an exact engine representation |
| `POI0009` | Warning | Animation binding could not be mapped deterministically |
| `POI0010` | Info | Intentional native-equivalent semantic difference |

`UnityMaterialImportResult` now exposes structured diagnostics in addition to
its compatibility `Warnings` strings.

## Deterministic Naming

- Material: `{sourceStem}.poiyomi-9_3_64[.{sourceGuid8}].uber`
- Pass variant: `{materialName}.{passRole}.{variantHash:x16}`
- Preserved metadata: `{materialName}.poiyomi-source.json`
- Animation binding: `{materialName}/{semanticProperty}`

Segments are normalized to filesystem-safe deterministic identifiers.

## Fixture Decision

The future Poiyomi fixture corpus will use original synthetic materials,
animations, and tiny textures released under CC0-1.0. The policy is recorded in
`XREngine.UnitTests/TestData/Poiyomi/README.md` before fixture assets are added.
No upstream Poiyomi example art will be copied.

## Validation

- `dotnet build XRENGINE/XRENGINE.csproj --no-restore --verbosity:minimal`
  - Passed with 0 warnings and 0 errors.
- `dotnet build XREngine.UnitTests/XREngine.UnitTests.csproj --no-restore --verbosity:minimal -m:1`
  - Passed with 0 warnings and 0 errors.
- `dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --no-build --no-restore --filter "FullyQualifiedName~PoiyomiPhase0CatalogTests|FullyQualifiedName~UnityPoiyomiMaterialImporterTests"`
  - 7 passed, 0 failed.
- Two consecutive generator runs against the pinned checkout produced the same
  final catalog SHA-256:
  `37e40686a37f2c32f3e9119bbbc4920566d043475feefd19408fb2830d972d94`.
