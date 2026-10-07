# Unit Test Project Reorganization TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Test Suite Layout](../../../developer-guides/testing/test-suite-layout.md)
Validation: [Platform Validation](../../testing/platform/platform-validation.md)

## Current State

`XREngine.UnitTests` is the current default NUnit project. It targets `net10.0-windows7.0`, still references broad product projects, and still contains flat subsystem folders. A scan on 2026-10-06 found 641 C# files under `XREngine.UnitTests`, with 332 under `Rendering/`. `GpuTestBase.cs` remains at the project root. `Shared/TestCategories.cs` does not exist. `XREngine.UnitTests/Headless/XREngine.HeadlessTests.csproj` links a narrow headless subset.

## Open Code Items

### Shared Test Infrastructure

- [ ] Add shared NUnit category constants. Files/types: `XREngine.UnitTests/Shared/TestCategories.cs`. Done when: all new lane markers use one shared source of truth.
- [ ] Add repository path and workspace file helpers. Files/types: `XREngine.UnitTests/Shared/RepoPaths.cs`. Done when: tests stop copying ad hoc root discovery and source-reading helpers.
- [ ] Add test output path helpers. Files/types: `XREngine.UnitTests/Shared/TestOutputPaths.cs`. Done when: generated test output writes only to approved test-owned locations.
- [ ] Move the GPU fixture to the shared fixture area. Files/types: `XREngine.UnitTests/GpuTestBase.cs`, `XREngine.UnitTests/Shared/Fixtures/GpuTestBase.cs`. Done when: references compile and the fixture carries hardware and OpenGL categories.
- [ ] Add helper attributes only if they reduce duplicated NUnit metadata. Files/types: shared test attributes. Done when: hardware, source-contract, and slow integration markers are clear and optional.
- [ ] Add `XREngine.UnitTests/README.md`. Files/types: test project README. Done when: it points to the guide and states the local folder rules without making tests read Markdown.

### Inventory And Classification

- [ ] Generate a test inventory manifest. Files/types: inventory tool or script, `XREngine.UnitTests/**/*.cs`. Done when: the manifest records path, namespace, classes, test count, categories, explicit markers, non-parallel markers, GPU fixture inheritance, source reads, private reflection, output writes, and detectable project references.
- [ ] Classify each existing test file into one lane. Files/types: inventory manifest. Done when: every test is marked Unit, Contract, Integration, Hardware, Performance, Shared, TestData, or Quarantine.
- [ ] Create temporary quarantine folders. Files/types: `Contracts/Quarantine/`, `Integration/Quarantine/`, `Hardware/Quarantine/`, `Unit/Quarantine/`. Done when: uncertain files move there with an inventory rationale.
- [ ] Move source-text tests to source-contract folders. Files/types: `Contracts/SourceContracts/...`. Done when: source-reading tests are no longer in broad subsystem folders.
- [ ] Move GPU, window, native interop, and runtime-driver tests to hardware folders. Files/types: `Hardware/OpenGL/...`, `Hardware/Vulkan/...`, `Hardware/OpenXR/...`, `Hardware/NativeInterop/...`. Done when: hardware prerequisites are explicit and default runs do not execute them.
- [ ] Move editor, world, import, and service-bootstrap tests to integration folders. Files/types: `Integration/...`. Done when: cross-subsystem tests are discoverable and isolated.
- [ ] Move pure behavior tests to unit folders. Files/types: `Unit/...`. Done when: unit folders do not require GPU, windows, native DLLs, editor startup, headset runtimes, or source scans.

### Rendering Folder Decomposition

- [ ] Split rendering unit tests by stable domain. Files/types: `Unit/Rendering/Lightmapping/`, `Probes/`, `Shadows/`, `Materials/`, `Textures/`, `Meshes/`, `Pipelines/`, `Vulkan/`. Done when: pure rendering behavior tests no longer sit directly under `Rendering/`.
- [ ] Move rendering source-contract tests by domain. Files/types: `Contracts/SourceContracts/Rendering/Shaders/`, `Vulkan/`, `OpenXR/`, `OpenGL/`. Done when: shader, Vulkan, OpenXR, and OpenGL contract tests are in the contract lane.
- [ ] Move rendering hardware tests by API. Files/types: `Hardware/OpenGL/Rendering/`, `Hardware/Vulkan/Rendering/`. Done when: `GpuTestBase`-derived tests and driver/runtime tests are outside the default lane.
- [ ] Move rendering integration tests. Files/types: `Integration/Rendering/`. Done when: runtime service and window/controller tests that can run without real GPU hardware are separated from unit and hardware tests.

### Non-Rendering Folder Decomposition

- [ ] Move math and geometry tests to unit folders. Files/types: `XRMath/`, `Geometry/`, `Unit/XRMath/`, `Unit/Geometry/`. Done when: namespaces and paths match the unit lane.
- [ ] Move data and core tests to unit or contract folders. Files/types: `Core/`, `Data/`, `Unit/Core/`, `Unit/Data/`, `Contracts/SourceContracts/Core/`. Done when: AOT/source-generated checks are contracts and pure data checks are units.
- [ ] Move scene tests by requirement. Files/types: `Scene/`, `Unit/Scene/`, `Integration/Scene/`, `Hardware/OpenGL/Scene/`. Done when: deserialization and GPU fixture requirements are explicit.
- [ ] Move editor tests by requirement. Files/types: `Editor/`, `Unit/Editor/`, `Integration/Editor/`, `Integration/Editor/Packaging/`. Done when: slow archive tests are explicit or slow, and editor runtime work is not in pure units.
- [ ] Move asset importer tests. Files/types: FBX and glTF tests, `Unit/Assets/...`, `Integration/Assets/...`, `Hardware/NativeInterop/...`. Done when: parser tests, corpus round trips, and native smoke tests are separated.
- [ ] Move audio tests. Files/types: `Audio/`, `Unit/Audio/`, `Integration/Audio/`, `Hardware/Audio/`. Done when: pure processors and settings are separated from native transports.
- [ ] Move physics tests. Files/types: `Physics/`, `Unit/Physics/`, `Hardware/OpenGL/Physics/`, `Hardware/NativeInterop/Physics/`. Done when: pure topology and component tests do not share folders with native or GPU tests.
- [ ] Move MCP tests. Files/types: `Mcp/`, `Unit/Mcp/`, `Integration/Mcp/`, `Contracts/DocsContracts/Mcp/`. Done when: registry, protocol, host-level, and docs-contract tests are separated. Do not add Markdown-reading tests.

### Names And Test Debt

- [ ] Rename permanent test files and classes that contain work-plan history terms. Files/types: tests containing `Todo`, `Backlog`, `Completion`, temporary milestone labels, or priority labels. Done when: behavior names replace them or an allowlist explains a stable external spec.
- [ ] Update namespaces after moves and renames. Files/types: moved test files. Done when: namespace names match lane and subsystem.
- [ ] Update CI filters, task filters, scripts, and docs references after renames. Files/types: workflows, `.vscode/tasks.json`, docs that name tests. Done when: focused Vulkan/OpenXR and other known filters still locate the intended tests.
- [ ] Inventory private reflection use. Files/types: inventory manifest, affected tests. Done when: each use is replaced, moved to contracts with rationale, or removed as non-contract trivia.
- [ ] Inventory source-file reads. Files/types: inventory manifest, affected tests. Done when: source reads use shared helpers and live under `Contracts/SourceContracts` unless replaced by stable seams.
- [ ] Add helper assertions for source-contract tests. Files/types: contract test helpers. Done when: failures include clear messages and repository-relative paths.

### Project Split

- [ ] Create shared, unit, contract, integration, hardware, performance, and test-data projects under `tests/`. Files/types: `tests/XREngine.Tests.*/*.csproj`. Done when: projects compile with lane-specific references.
- [ ] Move shared helpers into `XREngine.Tests.Shared`. Files/types: shared fixtures and helpers. Done when: lane projects reference shared helpers instead of copying them.
- [ ] Minimize project references by lane. Files/types: test project files. Done when: pure unit tests avoid `XREngine.Editor`, hardware tests own rendering/native references, and integration tests reference only required cross-subsystem projects.
- [ ] Decide and implement the compatibility plan for `XREngine.UnitTests`. Files/types: `XREngine.UnitTests/XREngine.UnitTests.csproj`, solution, workflows. Done when: either the compatibility project remains intentionally or all consumers move to split projects.
- [ ] Update solution, VS Code tasks, and CI workflows after the split. Files/types: `XRENGINE.slnx`, `.vscode/tasks.json`, workflow files. Done when: default, contract, integration, hardware discovery, and focused lanes run the intended projects.

### Test Data, Output, And Guard Rails

- [ ] Decide the durable location for checked-in `TestData`. Files/types: `XREngine.UnitTests/TestData`, `tests/XREngine.TestData`. Done when: small corpus assets have ownership and consumers.
- [ ] Add test-data documentation for corpus assets if a durable package is created. Files/types: test-data README. Done when: source, license, purpose, consumers, and regeneration steps are documented without becoming test assertions.
- [ ] Move large generated baselines out of source when they are not durable. Files/types: generated baselines. Done when: source contains only durable test inputs.
- [ ] Add cleanup helpers for temp output. Files/types: shared output helpers. Done when: tests clean test-owned temp output and never write arbitrary source folders.
- [ ] Add meta-tests or analyzer-style checks for placement rules. Files/types: guard tests or scripts. Done when: new broad-bucket tests, work-plan names, xUnit attributes, misplaced source-reading tests, misplaced `GpuTestBase` inheritance, missing hardware prerequisites, unapproved private reflection, source-folder writes, and pure-unit editor references are detected.

## Decisions Needed

- [ ] Choose whether split projects live under a new top-level `tests/` folder or whether `XREngine.UnitTests` is renamed in place first. Owner: testing owner.
- [ ] Choose whether long-term source architecture checks remain NUnit tests or become Roslyn analyzers/report scripts. Owner: testing/tooling owner.
- [ ] Choose whether hardware lanes are normal CI discovery or manual/local tasks until dedicated runners exist. Owner: CI owner.
- [ ] Choose when the pure unit project removes the `XREngine.Editor` reference. Owner: testing/runtime owner.
- [ ] Choose whether existing checked-in corpus summaries remain under `TestData` or move to a shared test-data package. Owner: testing owner.

## Out Of Scope

- Running the test suite as part of this documentation cleanup.
- Adding tests that read Markdown docs.
- Changing test frameworks without approval.
- Deleting generated LLM tests without the deletion policy evidence.
- Reorganizing production source code.
