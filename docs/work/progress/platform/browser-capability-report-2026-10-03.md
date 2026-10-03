# Browser authored-world capability report

The Editor's normal browser build now records a structured report at the project's
`Intermediate/Build/BrowserCapabilityReport.json` while cooking the saved startup
world and its declared streamed scenes. The report identifies the source world,
scene, node, component, material and pass where available. Each finding has a
stable `BrowserCook` code, a reason, and either `required` or `optional` severity.
The report is written outside staged browser output so it remains available when
required capability findings stop publication. Required findings are summarized
in the build error; the earlier activated browser bundle is preserved. An
`incomplete` status marks an interrupted preflight, including an error during
cooked-world hydration or streamed-scene processing. Previously generated
reports are replaced when a new world preflight begins.

Known authored capability checks continue across independent nodes, components,
meshes, and physics properties instead of stopping at the first unsupported
feature. Explicitly nonblocking Jolt static-friction coefficient mappings are
listed as optional findings. Other detected unsupported features are required
and block activation. The auditor does not remove authored components or
reinterpret unsupported values. Cancellation, corrupt serialized data, and
unexpected runtime failures remain immediate errors outside this aggregation.

This report covers the publisher's existing bounded authored-world, physics,
rendering, shader, UI, and shadow checks. It does not classify arbitrary game
component behavior created at runtime, every native-service dependency, or
later quality and device selections. An empty required list means this bounded
preflight found no known incompatibility, not that every browser path is safe.

## Bounded validation

The Release Editor graph build passed with zero warnings and errors. A subsequent
Editor-only refresh against those built dependencies also passed with zero
warnings and errors. The one-off probe in the ignored validation run invoked
the compiled Editor's game-assembly build and actual
`BrowserBuildState.Prepare`/`ExportAuthoredWorld` methods, stopping before WASM
publication. Its unchanged RollingBall input copied 187 authored files and
retained the source world's SHA-256
`b91540c5e6a1120c37101762affe92f84bcc688473c87d9162c1b8ee19b18b8a`.
The saved report had zero required findings and two optional Jolt friction
mappings. A copied world with independently unsupported directional shadow
settings, sleep notifications, and dominance group produced one complete
`blocked` report with three required and two optional findings. The build
error named the report, and both input snapshots passed their unchanged-file
verification. These checks exercise cook admission and report persistence; they
do not constitute a fresh browser runtime or device acceptance run.
