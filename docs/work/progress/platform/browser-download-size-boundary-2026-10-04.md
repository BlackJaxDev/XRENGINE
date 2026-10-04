# Browser download-size boundary

The current Development interpreter build remains untrimmed. Both the browser
project and the Editor publisher set `PublishTrimmed=false` and
`RunAOTCompilation=false`; the portable build guard requires metadata and
serializer qualification before either mode can change. There is no lazy managed
assembly loader in the unified startup path. Missing closed-world metadata
dependencies remain errors.

The pinned non-Blazor `Microsoft.NET.Sdk.WebAssembly` host has no verified public
lazy-loader integration. The documented
[.NET 10 lazy assembly loader](https://learn.microsoft.com/en-us/aspnet/core/blazor/webassembly-lazy-load-assemblies?view=aspnetcore-10.0)
and its MSBuild item apply to Blazor. The installed browser runtime declares a
lazy-resource category but exposes no corresponding public `RuntimeAPI` method;
an internal runtime hook is not a supported implementation contract. The current
renderer, game registrations, physics, audio and transport participate in startup.
No independently optional managed assembly has been identified in the sample.
Deferral therefore needs a concrete optional assembly, its transitive dependency
and metadata boundary, and a qualified public loading route before byte savings
can be claimed. Merely adding a Blazor item to this project does not implement it.

The native-Jolt Browser Release build on 2026-10-04 produced 206 framework WASM
resources totaling 58,060,183 bytes, with matching gzip resources totaling
26,698,955 bytes, about 25.46 MiB. These are build resources, not a measured
published transfer. This output lacks the published page, browser-publish record
and content manifest, and includes both uncompressed and gzip files. No final
application-plus-content size, alternative encoding result, startup timing or
mobile-device acceptance is established here. The
[readiness budget](../rendering/mobile-browser-readiness.md#devices-and-measurable-budgets)
remains 20 MiB initial compressed transfer.

Authored scene streaming is implemented through
`XRProject.BrowserStreamedScenePaths` and `SceneStreamingVolumeComponent`. The
unified runtime loads scene dependencies through the asynchronous asset catalog,
awaits `IRuntimeScenePreparationSource.PrepareSceneAsync`, and then attaches the
scene. The current sample does not provide an identified optional scene to defer;
reclassifying required startup content would not establish a useful reduction.

The Editor now retains startup and per-scene shader membership in optional,
versioned `shaderDelivery` metadata. Global pipeline, compute and material-variant
bindings remain essential; only exact identities used exclusively by streamed
scenes can defer their descriptor and WGSL payloads. Packages without this
metadata preserve the existing eager behavior. Ordinary dependency bounds and
the complete catalog identity/hash checks remain unchanged.

The session owns stable shader, material-variant, pipeline and compute catalog
objects backed by one append-only snapshot. An asset-preparation hook fetches and
verifies the predeclared batch before scene deserialization can resolve shaders.
The renderer keeps its existing catalog bindings. Validation, cancellation and
source retirement are checked before publication; failed preparation adds no
partial batch. A complete valid batch remains session-owned if later scene
hydration fails. Scene preparation still completes before scene attachment.

The combined Editor and native-Jolt Browser builds pass with zero warnings or
errors. Independent source review and 46 provider, loader and packager checks
cover the bounded delivery contract. A genuine authored-scene export produced
97 startup shaders and three optional scene shaders without changing the
authored world or scene bytes. All 204 package payload hashes and lengths were
verified. The production JavaScript loader fetched none of the optional payloads
at startup and fetched their six descriptor/WGSL files on demand. This used a
local fetch adapter; browser/.NET attachment and renderer continuity remain live
acceptance work.

The shared-world-package profile still verifies every packaged file eagerly;
the ordinary Editor output used above does not use that profile. No measured
application transfer reduction is claimed. Managed assembly loading, trimming,
actual published transfer measurement and the readiness budget remain open,
with shipping-mode selection owned by the separate runtime qualification
decision.
