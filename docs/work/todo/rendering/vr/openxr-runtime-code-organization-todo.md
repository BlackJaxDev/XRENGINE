# OpenXR Runtime Code Organization TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [OpenXR VR Rendering](../../../../architecture/rendering/openxr-vr-rendering.md), [OpenXR Runtime](../../../../developer-guides/vr/openxr-runtime.md), [Runtime Project Organization](../../../../architecture/runtime/project-organization.md)
Validation: [OpenXR Validation](../../../testing/xr/openxr-validation.md)

## Current State

The native OpenXR runtime implementation now lives in `XREngine.Runtime.XR.OpenXR` with 43 C# files. Engine-facing OpenXR contracts and smoke DTOs still live in `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/`. Renderer-specific graphics bindings live in the OpenGL and Vulkan renderer projects. `XREngine.Editor/Program.OpenXrSmokeRunController.cs` still hosts the executable smoke controller. Many source-contract tests still read exact OpenXR source-file paths, which makes safe structural refactoring harder.

## Open Code Items

### Refactor safety

- [ ] Add test helpers that discover OpenXR source files recursively instead of requiring exact file paths. Update `OpenXrTimingPipelineContractTests`, `RvcRenderingContractTests`, `VrViewRenderModeContractTests`, `VulkanP1ValidationTests`, and other OpenXR source-contract tests. Done when: moving a method between OpenXR files does not fail a test solely because its file path changed.
- [ ] Replace exact-path source-text assertions with behavior, symbol-level, or recursive-source assertions where practical. Done when: remaining structural assertions identify required contracts rather than one partial file.
- [ ] Inventory scripts that consume `openxr-smoke-summary.json` and list required schema fields in a guide or validation schema doc. Done when: smoke JSON compatibility is explicit before schema changes.
- [ ] Inventory CLI arguments and environment variables used by the smoke controller and runtime validation hooks. Done when: smoke CLI compatibility is documented in one stable guide.

### Production layout

- [ ] Organize `XREngine.Runtime.XR.OpenXR` into responsibility folders while preserving namespaces and behavior. Cover bootstrap, runtime state, frame lifecycle, views, input, graphics gateway, integration, diagnostics, and swapchain/device-loss ownership. Done when: the project root contains only the primary runtime facade and responsibility folders.
- [ ] Rename `Init.cs`, `Instance.cs`, `Extensions.cs`, and `Validation.cs` to names that sort with their owning OpenXR type during the partial-class transition. Done when: file names communicate ownership and no serialized field or namespace changes occur.
- [ ] Standardize durable C# type and file casing on `OpenXr` where public API compatibility permits it. Done when: product prose can still say OpenXR, but type names no longer mix `OpenXRAPI` and `OpenXr` without reason.
- [ ] Simplify or document the `XREngine.Rendering.API.Rendering.OpenXR` namespace. Done when: the namespace is intentional and matches surrounding rendering namespaces or a compatibility note explains why it stays.
- [ ] Put each enum, interface, class, record, and struct in its own matching file. Split `XrGraphicsBindings.cs`, `OpenXrProbeRetryPolicy.cs`, OpenXR Vulkan requirements support types, strict-SPS support types, and smoke temporal ledger support types. Done when: touched OpenXR code follows one type per file.

### Runtime ownership

- [ ] Split oversized `OpenXRAPI` partial files by responsibility inside `XREngine.Runtime.XR.OpenXR`. Done when: no partial file combines unrelated state, frame lifecycle, input, graphics, diagnostics, and backend behavior.
- [ ] Move backend-neutral camera, viewport, pose, and desktop mirror dispatch out of backend-named files. Done when: OpenGL-named and Vulkan-named files own only backend-specific resources and commands.
- [ ] Extract a small runtime coordinator and owned services for runtime discovery, SteamVR launch coordination, instance creation, session lifecycle, frame-loop state, pose/view caches, input, and graphics binding. Done when: components cannot mutate unrelated subsystem state through broad partial-class access.
- [ ] Create one authoritative runtime discovery policy. Merge behavior now split across native loader, instance creation, and Vulkan requirements. Done when: environment, registry hive, registry view, and fallback behavior match across OpenGL, Vulkan, Monado, and SteamVR lanes.
- [ ] Make graphics bindings own session binding, swapchains, rendering, cleanup, and backend policy instead of delegating every operation back into the coordinator. Done when: the active frame path uses binding authority for acquire, wait, release, and cleanup.
- [ ] Remove unused graphics-binding methods or route real frame behavior through them. Done when: `RenderViews` and related compatibility methods are either removed or behaviorally owned.

### Diagnostics and smoke boundary

- [ ] Decide which always-recorded `RecordSmoke*` counters are general telemetry and which are smoke-only instrumentation. Done when: general telemetry uses runtime diagnostic names and validation-only collection is gated through an allocation-safe sink.
- [ ] Add an immutable OpenXR runtime diagnostics snapshot with only runtime-owned OpenXR data. Done when: runtime code exposes diagnostics, not an editor/tool-specific JSON report model.
- [ ] Move temporal-history diagnostics out of OpenXR and rename them without smoke or milestone names. Done when: temporal diagnostics live with the rendering feature that owns them.
- [ ] Keep GPU capture hooks in the runtime assembly only when they require private renderer state, and return runtime-neutral capture records. Done when: validation code can consume captures without depending on private runtime types.
- [ ] Create `XREngine.Rendering.Validation` or another owner-approved validation assembly. Done when: editor and unit tests share validation behavior without test dependencies leaking into runtime or editor production dependencies.
- [ ] Move smoke summary contracts, builders, scenario definitions, and evidence validators into the validation assembly. Done when: `XREngine.Runtime.Rendering` does not reference the validation project.
- [ ] Move `OpenXrSmokeRunController` into an editor diagnostics feature folder as a top-level type. Split option parsing, run orchestration, ledger retention, capture collection, summary building, validation, and exit policy. Done when: the smoke controller is no longer a large nested `Program` partial.
- [ ] Replace flat smoke frame and summary DTOs with composed sections after the diagnostics seam works. Done when: one schema bump updates Monado, SteamVR, strict-stereo, Vulkan validation scripts, and behavioral tests together.

### Durable names and schema

- [ ] Rename runtime types, fields, methods, log keys, settings, and validators that contain durable milestone numbers. Done when: names describe behavior such as deterministic validation pose override, temporal-history diagnostics, strict-stereo fault injection, strict-stereo boundary capture, and temporal-scenario evidence validation.
- [ ] Use immutable records or `init`-only contracts in the validation model where JSON tooling permits it. Done when: report DTOs are stable and clear without unnecessary mutation.
- [ ] Use typed enums in validation models where string values do not need forward compatibility. Done when: validators reject invalid known values before reports reach downstream scripts.
- [ ] Document the new smoke schema and retain an example report in testing documentation if useful. Done when: report sections reflect data ownership and are understandable without reading the editor controller.

## Decisions Needed

- [ ] Should the primary runtime type ultimately be `OpenXrApi`, `OpenXrRuntime`, or a small facade plus internal services? Owner: Rendering / XR.
- [ ] Should `XREngine.Rendering.Validation` be a reusable class library or an editor-owned feature assembly? Owner: Rendering / Testing.
- [ ] Should general diagnostics use a sink interface, an owned recorder, or immutable snapshot sources composed at capture time? Owner: Rendering.
- [ ] Should smoke JSON contract types remain mutable DTOs or become immutable records with explicit serializers? Owner: Rendering / Testing.
- [ ] Should existing environment-variable names that contain old milestone numbers change immediately or with the report-schema migration? Owner: Rendering / Testing.

## Out Of Scope

- Behavior changes during mechanical file moves.
- Moving smoke infrastructure into `XREngine.UnitTests`.
- Adding per-frame allocations in disabled diagnostics.
- Renaming public settings or serialized fields without an explicit compatibility plan.
