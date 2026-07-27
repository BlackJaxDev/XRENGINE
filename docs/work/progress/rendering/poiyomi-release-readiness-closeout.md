# Poiyomi Toon 9.3.64 Release Readiness Closeout

- Completed: 2026-07-26
- Source version: `9.3.64`
- Source commit: `c5aaeeb3a67782b7e8a26e184d5e0a1970792294`
- Shader SHA-256: `7efb9176022291a041ecf332bf999f68ba33591d6f446e60757be83e968e61d8`
- Outcome: Complete

## Delivered Contract

The converter, runtime material path, ImGui authoring system, reimport workflow,
and validation corpus now expose a versioned support contract for the pinned
Poiyomi Toon source. Runtime-visible values are converted to exact or reviewed
native behavior where available. Values without an engine service remain in the
versioned descriptor and produce deterministic conversion diagnostics instead
of being silently discarded.

The embedded source catalog contains 3,736 properties, 137 texture properties,
five passes, 41 active annotation kinds, 27 display-option kinds, 62 reachable
authoring workflows, and zero unclassified runtime properties. The generated
parity reference publishes every active annotation and reachable workflow.

## Release Surface

- Public conversion and remediation guide:
  [Poiyomi Toon material conversion](../../../developer-guides/rendering/poiyomi-toon-material-conversion.md)
- Contributor and upstream-update guide:
  [Poiyomi Toon maintenance](../../../developer-guides/rendering/poiyomi-toon-maintenance.md)
- Generated property and authoring reference:
  [Poiyomi Toon parity](../../../reference/rendering/poiyomi-toon-9.3.64-parity.md)
- Version-audit command: `Tools/Reports/Test-PoiyomiSourceVersion.ps1`
- Parity-report generator: `Tools/Reports/Generate-PoiyomiParityReport.ps1`
- Full validation command: `Tools/Validate-PoiyomiParity.ps1`
- MIT attribution notices:
  `docs/licenses/Poiyomi-Toon-9.3.64-MIT.txt` and
  `docs/licenses/ThryEditor-MIT.txt`

## Validation Evidence

The pinned checkout source audit reported zero catalog changes. Its report is:

`Build/_AgentValidation/20260725-poiyomi-material-runtime/release-readiness/poiyomi-source-version-audit.json`

The automated Poiyomi matrix passed 126 of 126 tests before the final checklist
closure assertion was added. The complete isolated live validator then passed
on both rendering backends:

| Backend | Camera captures | Final-pipeline captures | Streamed previews | Validation errors |
| --- | ---: | ---: | ---: | ---: |
| OpenGL | 3 | 3 | 4 clean | 0 |
| Vulkan | 3 | 3 | 4 clean | 0 |

Every final-pipeline capture had zero non-finite samples and a nonzero average
and maximum RGB value. The OpenGL and Vulkan camera sets were visually inspected
and showed camera-dependent scene output. Both named editor sessions stopped
cleanly after capture.

After closing the checklist, a clean rebuild and rerun passed 127 of 127 tests,
including the regression that requires `Status: Complete` and rejects any
remaining unchecked item.

The machine-readable report and captures are under:

`Build/_AgentValidation/20260725-poiyomi-material-runtime/release-readiness/final-live/`

Build output contained no errors. Remaining warnings were existing
Magick.NET security advisories and pre-existing OscCore nullable/unused-member
warnings; this work introduced no new compiler warnings.

## Maintenance Gate

Any future supported source update must produce a catalog diff, update affected
fixtures and reference captures, regenerate the public parity reference, review
native-equivalent behavior and license attribution, and pass the complete
OpenGL/Vulkan validator before the supported version statement changes.
