# Poiyomi Toon 9.3 Parity Validation

Scope: Validate the Poiyomi Toon 9.3.64 conversion corpus, contracts, shader compilation, inspector behavior, visual parity, performance, and live backends.

Architecture: [Uber Shader Varianting](../../../architecture/rendering/uber-shader-varianting.md)  Code todos: none.

## Setup

Pinned corpus files are `XREngine.UnitTests/TestData/Poiyomi/ParityCorpus/corpus-manifest.json` and `XREngine.UnitTests/TestData/Poiyomi/LICENSE.txt`. The corpus uses Poiyomi source `9.3.64`, commit `c5aaeeb3a67782b7e8a26e184d5e0a1970792294`, Unity `2022.3.22f1`, and catalog SHA-256 `1d72086a4e46344649d0f99d6b17e5666cdb33cfcba20d1fa270c7bae4124236`.

Run automated parity validation with:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~PoiyomiParity"
```

Run full OpenGL and Vulkan live validation with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Validate-PoiyomiParity.ps1
```

Use `-NoBuild` only when the isolated editor binaries already contain the source under test. Use `-SkipLiveValidation` only for CI workers without a GPU. That mode does not satisfy live-backend acceptance.

## Checks

### Corpus And Contracts
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Corpus completeness | Run `PoiyomiParityCorpusTests`. | Fixture completeness, licensing, catalog integrity, classifications, geometry, texture, animation, schema, authoring, and multi-material coverage pass. | Open | none |
| Conversion contracts | Run `PoiyomiParityContractTests`. | Parsing, conversion, preservation, diagnostics, variants, pass isolation, sampler fallback, schema conditions, actions, widgets, clipboard, presets, layers, path safety, and fuzzing pass. | Open | none |
| Inspector behavior | Run `PoiyomiInspectorInteractionTests`. | Headless ImGui mouse, keyboard, drag/drop, clipboard, reset, animation, context actions, persistence, reimport, cancellation, localization, glyph, DPI, narrow/wide, and scrolling cases pass. | Open | none |

### Shader And Visual Parity
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Uber shader compilation | Run `UberShaderCompilationTests`. | Representative minimal, common, family-maximal, and global-maximal variants compile to SPIR-V. Shaderc warnings are errors. | Open | none |
| Visual and performance tests | Run `PoiyomiVisualPerformanceTests`. | Analytical thresholds, scene conditions, stress paths, memory bounds, and allocation probes pass. | Open | none |
| Unity references | Compare against reviewed Unity reference captures for the three manifest camera poses. | OpenGL and Vulkan captures match thresholds. | Open | none |

### Live Backends
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Full runner | Run `Tools\Validate-PoiyomiParity.ps1`. | The runner captures three camera positions and final pipeline textures per backend and writes a machine-readable report. | Open | none |
| OpenGL review | Review OpenGL captures. | Three captures are visibly valid. | Open | none |
| Vulkan review | Review Vulkan captures. | Three captures are visibly valid. | Open | none |
| Backend logs | Scan OpenGL and Vulkan logs. | No non-teardown validation or shader errors appear. | Open | none |
| Profiler output | Review CPU, GPU, render-profiler, and texture-streaming output for both backends. | Costs and streaming behavior are recorded. | Open | none |
| RenderDoc triage | Use RenderDoc only if captures disagree or logs cannot identify the failing pass or resource. | The failing pass or resource is identified. | Open | none |

## Failures
| Check | Symptom | Investigation or code item |
|---|---|---|
