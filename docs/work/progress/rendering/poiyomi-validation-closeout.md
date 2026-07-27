# Poiyomi Toon 9.3 Validation Closeout

parity validation is complete. The corpus, automated contracts, shader compilation,
headless inspector interaction, visual comparison, performance probes, and live
OpenGL/Vulkan validation now form one repeatable acceptance path.

## Pinned inputs

- Poiyomi Toon `9.3.64`, commit
  `c5aaeeb3a67782b7e8a26e184d5e0a1970792294`.
- Unity `2022.3.22f1` with `.poiyomi/Poiyomi Toon` in linear color.
- Redistributable fixtures are CC0-1.0; the pinned source shader is MIT.
- The corpus manifest covers locked and unlocked materials, focused and maximal
  feature sets, all presets, mesh/texture/animation shapes, schema annotations,
  authoring payloads, and compatible/incompatible multi-material cases.
- Three authoritative 640x360 Unity reference PNGs and their camera, lighting,
  exposure, color-space, and source metadata are tracked under
  `XREngine.UnitTests/TestData/Poiyomi/ParityCorpus/UnityReferences/`.

## Automated acceptance

The final run passed 53 of 53 parity validation tests. Those tests cover corpus integrity,
classification and conversion contracts, malformed-input safety, undoable
actions, schema and widget behavior, deterministic inspector interaction,
OpenGL/Vulkan shader paths, all semantic passes, desktop/OpenVR/OpenXR vertex
variants, pairwise feature sampling, numeric visual thresholds, performance
budgets, allocation-free steady probes, cancellation, and lifecycle stress.

While validating the real OpenGL path, the no-forward-lighting permutation
exposed feature code that referenced `light` and `surfacePbr` outside their
conditional scope. The fragment shader now creates a neutral feature context for
that permutation, and
`NoForwardLightingVariant_KeepsNeutralFeatureContextAndCompiles` prevents the
regression.

## Live backend evidence

Final evidence root:
`Build/_AgentValidation/20260725-poiyomi-material-runtime/parity/final-run-5/`

The machine-readable report is
`reports/poiyomi-parity-validation.json`. It records a passing test run and a
passing live result for both backends.

| Backend | Views | Final targets | Minimum average RGB | Maximum RGB | Non-finite samples | Validation errors | Streaming previews |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| OpenGL | 3 | 3 | 1.5462958 | 1.6132812 | 0 | 0 | 4/4 ready |
| Vulkan | 3 | 3 | 0.65296865 | 1.8574219 | 0 | 0 | 4/4 ready |

The three camera positions were visually reviewed for each backend. They show
camera-dependent scene output, so stale or view-independent sampling cannot pass
the acceptance gate. The raw final-pipeline captures were reviewed separately.
No texture-streaming validation failure was reported.

Vulkan performs deferred initialization clears before its desktop final target
becomes valid. The runner now waits for a finite, non-black final target and
rejects empty captures; this backend-native startup difference is not treated as
rendered output. OpenGL and Vulkan have different native exposure/composition,
which is covered by the pinned native-equivalent thresholds and human review.

CPU, GPU, render-profiler, texture-streaming, screenshots, pipeline captures,
and scanned backend logs are stored in the final evidence root. The log scan
found no shader compilation, OpenGL validation, Vulkan validation, or resource-
lifetime errors in accepted output.

## GPU inspection

RenderDoc capture:
`Build/_AgentValidation/20260725-poiyomi-material-runtime/parity/live-validation/renderdoc/poiyomi-vulkan_frame600.rdc`

The Vulkan capture contains 160 events, 21 draw calls (4 indexed, 17
non-indexed), one dispatch, and the expected sequence of executed command-buffer
passes through the final color pass. It established that the live Vulkan path
was issuing real scene work; the RenderDoc session was closed after inspection.

## Outcome

Every parity validation acceptance criterion is satisfied: the fixture corpus has no
silent omissions, exact mappings stay within pinned thresholds, reviewed native
differences are documented, targeted pass/VR/animation/render-state variants
compile and execute without new warnings, steady-state probes allocate nothing,
and the maximal inspector remains within the versioned responsiveness, memory,
and cancellation budgets.
