# Poiyomi Toon 9.3 Parity Validation

parity validation closes the Poiyomi Toon 9.3.64 conversion project with a versioned,
redistributable corpus and repeatable unit, contract, shader, inspector, visual,
performance, and live-backend validation.

## Pinned corpus

- Manifest: `XREngine.UnitTests/TestData/Poiyomi/ParityCorpus/corpus-manifest.json`
- License: `XREngine.UnitTests/TestData/Poiyomi/LICENSE.txt` (`CC0-1.0`)
- Poiyomi source: `9.3.64`, commit
  `c5aaeeb3a67782b7e8a26e184d5e0a1970792294`
- Unity authoring baseline: `2022.3.22f1`
- Catalog integrity SHA-256:
  `1d72086a4e46344649d0f99d6b17e5666cdb33cfcba20d1fa270c7bae4124236`

The manifest records unlocked and optimized/locked materials, focused feature
families, every render preset, maximal practical combinations, mesh attributes,
texture roles, animation binding kinds, schema annotations and inactive
lookalikes, versioned authoring payloads, multi-material compatibility cases,
fixed visual conditions, comparison thresholds, and performance budgets.

The three reviewed PPM references are intentionally small analytical fixtures.
They exercise deterministic exact/native-equivalent comparison behavior without
embedding upstream copyrighted shader assets.

Authoritative Unity reference captures were generated from Unity `2022.3.22f1`
and the pinned Poiyomi commit using `.poiyomi/Poiyomi Toon`, linear color, a
fixed 640x360 render target, fixed directional light, and the three manifest
camera poses. The source package remains a user-provided/pinned checkout and is
not redistributed. The reviewed metadata and PNGs are versioned under
`XREngine.UnitTests/TestData/Poiyomi/ParityCorpus/UnityReferences/`; the generated
images are CC0-1.0 and their source shader is MIT-licensed.

## Automated matrix

`PoiyomiParityCorpusTests` verifies fixture completeness, licensing, catalog
integrity, classifications, geometry, texture, animation, schema, authoring, and
multi-material coverage.

`PoiyomiParityContractTests` verifies parsing, conversion, preservation,
diagnostics, variants, pass isolation, sampler fallback rungs, schema and
condition semantics, atomic actions, widgets, clipboard/presets/layers, path
safety, and malformed-input fuzzing.

`PoiyomiInspectorInteractionTests` drives a headless ImGui interaction
harness through mouse, keyboard, drag/drop, clipboard, reset, animation,
context-action, persistence, reimport, cancellation, localization, missing-glyph,
DPI, narrow/wide, and scrolling cases.

`PoiyomiShaderCompilationTests` compiles representative minimal, common,
family-maximal, and global-maximal variants to SPIR-V; checks all semantic passes;
checks desktop/OpenVR/OpenXR-compatible vertex paths; compares OpenGL and Vulkan
resolved-source contracts; and covers all feature pairs deterministically.
Shaderc warnings are promoted to compilation errors.

`PoiyomiVisualPerformanceTests` verifies analytical visual thresholds,
fixed scene conditions, schema/variant/packing/search/cancellation stress,
source and sampler pressure, memory bounds, and allocation-free steady probes.

## Running validation

Automated suite only:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~PoiyomiParity"
```

Full OpenGL and Vulkan live validation:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Validate-PoiyomiParity.ps1
```

The runner uses named isolated editor sessions, waits for the Uber Shader World,
captures three camera positions and the final pipeline texture per backend,
dumps CPU/GPU/render-profiler and texture-streaming data, scans backend logs, and
writes a machine-readable report under `Build/_AgentValidation/<run>/reports/`.
Before accepting captures, it repeatedly samples `FinalPostProcessOutputTexture`
and rejects an empty, non-finite, or effectively black target. This prevents a
backend's deferred swapchain initialization clear from being mistaken for a
valid rendered frame.

Use `-NoBuild` only when the current isolated editor binaries already contain
the source under test. Use `-SkipLiveValidation` for CI workers without a GPU;
that mode does not satisfy live-backend acceptance.

## Acceptance evidence

A parity validation closeout requires:

1. all parity validation tests passing;
2. three visibly reviewed OpenGL captures;
3. three visibly reviewed Vulkan captures;
4. no non-teardown OpenGL/Vulkan validation or shader errors;
5. recorded CPU/GPU/render-profiler output for both backends;
6. all parity validation checklist boxes checked only after the above evidence exists.

RenderDoc is required only if the live captures disagree or the logs do not
identify the failing pass/resource. The parity validation closeout capture confirmed a
Vulkan frame with 160 events and 21 draw calls and was closed after inspection.
