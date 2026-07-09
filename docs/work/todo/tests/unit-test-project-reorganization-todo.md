# Unit Test Project Reorganization TODO

Last Updated: 2026-07-09
Owner: Testing / Architecture
Status: Proposed
Target Branch: `tests-unit-project-reorganization`

## Goal

Turn `XREngine.UnitTests` from an organically grown LLM test bucket into a
governed, discoverable, deterministic test suite with clear lanes for unit,
contract, integration, hardware, and performance coverage.

The result should make it obvious where a new test belongs, which tests are
safe for every local/CI run, which tests are intentionally brittle
source-contract tripwires, and which tests require GPU, native DLLs, editor
runtime services, or hardware.

## Current State Snapshot

Snapshot from 2026-07-09:

- `XREngine.UnitTests` is an NUnit project targeting `net10.0-windows7.0`.
- The project contains about 282 C# source/support files excluding `bin`,
  `obj`, and `TestResults`.
- About 166 files live under the flat `XREngine.UnitTests/Rendering/` folder.
- The project mixes:
  - real unit tests,
  - source-text contract tests,
  - TODO/progress/completion acceptance tests,
  - editor and world integration tests,
  - GPU/OpenGL tests,
  - native interop smoke tests,
  - performance/baseline harnesses,
  - test utilities.
- Test names frequently encode implementation phases or LLM work-plan state,
  for example `Phase`, `P0`, `P1`, `Todo`, `Backlog`, and `Completion`.
- `XREngine.UnitTests.csproj` references broad product projects including
  `XREngine.Editor`, which makes the whole suite carry editor/runtime weight.
- `RenderPipelineScriptCompilerTests.cs` is excluded because it uses xUnit
  attributes in an NUnit project.
- Local runtime output exists under `XREngine.UnitTests/bin`, `obj`, and
  `TestResults`; these are not tracked and should remain disposable.

## Principles

- Organize by test purpose first, subsystem second.
- Keep the fast lane fast: pure unit tests must not require GPU, windows,
  native DLLs, editor startup, network, headset runtime, or external assets.
- Make brittle tests honest. Source-text assertions are useful, but they belong
  in a clearly named contract lane.
- Test class and file names should describe engine behavior, not the phase or
  TODO item that originally generated them.
- Hardware, native interop, and performance tests must be opt-in unless a lane
  explicitly provisions those requirements.
- Prefer public behavior or internal test seams over private reflection.
- Do not silently delete generated LLM tests; triage them into keep, rewrite,
  move, merge, or delete-with-rationale.
- Avoid adding dependencies or changing test frameworks as part of layout work
  unless explicitly approved.

## Target Layout

Long-term preferred layout:

```text
tests/
  XREngine.Tests.Shared/
    Assertions/
    Fixtures/
    TestSupport/
    EngineTestScope.cs
    RepoPaths.cs

  XREngine.Tests.Unit/
    Animation/
    Assets/
    Audio/
    Core/
    Data/
    Geometry/
    Physics/
    Rendering/
      Lightmapping/
      Materials/
      Meshes/
      Pipelines/
      Probes/
      Shadows/
      Textures/
      Vulkan/
    Scene/
    XRMath/

  XREngine.Tests.Contracts/
    SourceContracts/
      Editor/
      Rendering/
        OpenGL/
        OpenXR/
        Shaders/
        Vulkan/
      Runtime/
    DocsContracts/

  XREngine.Tests.Integration/
    Assets/
      Fbx/
      Gltf/
    Editor/
    RuntimeServices/
    Serialization/
    World/

  XREngine.Tests.Hardware/
    NativeInterop/
    OpenGL/
    OpenXR/
    Vulkan/

  XREngine.Tests.Performance/
    Physics/
    Rendering/

  XREngine.TestData/
    Fbx/
    Gltf/
    Humanoid/
```

Acceptable intermediate layout inside the existing project:

```text
XREngine.UnitTests/
  Shared/
  Unit/
  Contracts/
  Integration/
  Hardware/
  Performance/
  TestData/
```

Use the intermediate layout first if project splitting would make the initial
move too noisy.

## Test Lane Definitions

### Unit

Fast, deterministic tests for a small API surface.

Allowed:

- In-memory objects.
- Small temp files under a test-owned temp directory.
- No-op/fake services.
- Deterministic clocks and schedulers.

Not allowed:

- GPU contexts.
- Real windows.
- Native DLL availability checks.
- Editor startup.
- OpenXR/OpenVR runtime calls.
- Source-file string scanning.
- Long-running performance thresholds.

### Contracts

Tests that guard source-level, docs-level, or architecture wiring invariants.

Allowed:

- `ReadWorkspaceFile` or equivalent repo-file reads.
- String checks against source code.
- Reflection for public/internal metadata.
- Generated manifest checks.

Rules:

- File/class names should include `ContractTests`.
- Tests must be scoped to a stable contract, not a phase name.
- Prefer checking generated contracts or public metadata over raw source text
  where a stable seam exists.

### Integration

Tests that cross subsystems but do not require real GPU/headset/native hardware.

Examples:

- Model import using checked-in small corpus assets.
- Serialization and asset package round trips.
- Runtime service bootstrap with fake hosts.
- Unit Testing World settings parsing.

Rules:

- Use isolated temp directories.
- Avoid writing into repo source folders.
- Keep checked-in test data small and documented.
- Mark slow rows with `[Category("Slow")]` only when they are excluded from
  the default lane.

### Hardware

Tests requiring GPU, graphics API context, native DLLs, headset runtime, or
driver-specific support.

Rules:

- Mark with `[Category(TestCategories.Hardware)]`.
- Also mark `[Category(TestCategories.OpenGL)]`, `Vulkan`, `OpenXR`,
  `NativeInterop`, or another precise requirement.
- Use `[Explicit]` unless the CI lane provisions the requirement.
- Never hang waiting for a visible window or headset.
- If prerequisites are missing, report `Assert.Inconclusive` with a precise
  reason.

### Performance

Benchmarks, timing baselines, allocation checks, and profiling sentinels.

Rules:

- Do not run by default.
- Write output under `TestResults` or `Build/_AgentValidation/<run>/reports`.
- Thresholds must account for machine variability or be relative to a local
  baseline.
- Performance tests must not be used as proof of visual correctness.

## Naming Rules

New or migrated tests should use stable behavior names:

- Good: `VulkanDescriptorLifetimeTests`
- Good: `OpenXrTemporalHistoryIsolationTests`
- Good: `LightmapBakeManagerTests`
- Good: `ShaderSourceResolverCachingTests`

Avoid names that encode work-plan history:

- Avoid: `VulkanTodoP2ValidationTests`
- Avoid: `GpuRenderingBacklogTests`
- Avoid: `OpenXrStereoTemporalIsolationCompletionTests`
- Avoid: `AlphaToCoveragePhase2Tests`

Temporary migration names are allowed only under a quarantine folder and must
have an explicit follow-up item in this document.

## Categories

Add one shared source of truth for NUnit categories:

```csharp
internal static class TestCategories
{
    public const string Unit = "Unit";
    public const string Contract = "Contract";
    public const string Integration = "Integration";
    public const string Hardware = "Hardware";
    public const string Performance = "Performance";
    public const string Slow = "Slow";
    public const string OpenGL = "OpenGL";
    public const string Vulkan = "Vulkan";
    public const string OpenXR = "OpenXR";
    public const string NativeInterop = "NativeInterop";
}
```

Expected default run:

- Include `Unit`, `Contract`, and small `Integration` tests.
- Exclude `Hardware`, `Performance`, `Slow`, and `[Explicit]` tests.

## Phase 0 - Branch, Inventory, And Baseline

- [ ] Create dedicated branch `tests-unit-project-reorganization`.
- [ ] Record current dirty-worktree state before moving tests.
- [ ] Generate an inventory manifest under
  `Build/_AgentValidation/<run>/reports/unit-test-inventory.json` with:
  - [ ] file path,
  - [ ] namespace,
  - [ ] class names,
  - [ ] test method count,
  - [ ] NUnit categories,
  - [ ] `[Explicit]` / `[NonParallelizable]` markers,
  - [ ] `GpuTestBase` inheritance,
  - [ ] source-text reads,
  - [ ] private reflection use,
  - [ ] temp/output path writes,
  - [ ] project references required if statically detectable.
- [ ] Capture a default baseline:
  - [ ] `dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj`
  - [ ] focused rendering lane if one already exists,
  - [ ] list of failures, inconclusive tests, skipped tests, and runtime.
- [ ] Confirm `bin`, `obj`, and `TestResults` are ignored and untracked.
- [ ] Decide whether Phase 1 uses the intermediate in-project layout or starts
  with split projects.
- [x] Add this TODO to `docs/work/README.md`.

## Phase 1 - Test Governance Scaffolding

- [ ] Add `XREngine.UnitTests/Shared/TestCategories.cs`.
- [ ] Add `XREngine.UnitTests/Shared/RepoPaths.cs` for finding the workspace
  root and reading repo files.
- [ ] Add `XREngine.UnitTests/Shared/TestOutputPaths.cs` for temp and
  `TestResults` output paths.
- [ ] Move `GpuTestBase.cs` to `Shared/Fixtures/GpuTestBase.cs`.
- [ ] Normalize `GpuTestBase` comments to ASCII and mark the fixture category:
  - [ ] `[Category(TestCategories.Hardware)]`,
  - [ ] `[Category(TestCategories.OpenGL)]`.
- [ ] Add helper attributes if useful:
  - [ ] `HardwareTestAttribute`,
  - [ ] `SourceContractTestAttribute`,
  - [ ] `SlowIntegrationTestAttribute`.
- [ ] Add a short `XREngine.UnitTests/README.md` explaining:
  - [ ] test lanes,
  - [ ] naming rules,
  - [ ] category rules,
  - [ ] output path rules,
  - [ ] where new tests belong.
- [ ] Update `AGENTS.md` with a concise test-suite placement rule so future
  LLMs stop adding tests to broad subsystem buckets.

## Phase 2 - Quarantine And Classification

- [ ] Create temporary migration folders:
  - [ ] `Contracts/Quarantine/`,
  - [ ] `Integration/Quarantine/`,
  - [ ] `Hardware/Quarantine/`,
  - [ ] `Unit/Quarantine/`.
- [ ] Classify every existing test file into one lane in the inventory
  manifest.
- [ ] Move obvious source-text tests into `Contracts/SourceContracts/...`.
- [ ] Move obvious GPU/window tests into `Hardware/OpenGL/...` or
  `Hardware/Vulkan/...`.
- [ ] Move obvious editor/world/import tests into `Integration/...`.
- [ ] Move true pure tests into `Unit/...`.
- [ ] Leave uncertain tests in a quarantine folder with a comment in the
  manifest explaining the uncertainty.
- [ ] Preserve namespaces only when low-risk; otherwise update namespaces to
  match the new lane and subsystem.
- [ ] Run `dotnet test` after the first batch of moves and record failures.

## Phase 3 - Rendering Folder Decomposition

Split the current flat `Rendering` folder by stable domain:

- [ ] `Unit/Rendering/Lightmapping/`
  - [ ] Move `LightmapBakeManagerTests.cs`.
  - [ ] Replace private reflection with a test seam or justify keeping it.
- [ ] `Unit/Rendering/Probes/`
  - [ ] Move light probe math/readiness tests that do not need GPU.
- [ ] `Unit/Rendering/Shadows/`
  - [ ] Move shadow atlas/resource/fallback tests that exercise public runtime
    behavior.
- [ ] `Unit/Rendering/Materials/`
  - [ ] Move material layout, material inspector, Uber material, and material
    variant behavior tests.
- [ ] `Unit/Rendering/Textures/`
  - [ ] Move mipmap, texture descriptor, texture streaming, and texture
    metadata behavior tests.
- [ ] `Unit/Rendering/Meshes/`
  - [ ] Move mesh bounds, BVH, meshlet, remapper, and mesh submission tests.
- [ ] `Unit/Rendering/Pipelines/`
  - [ ] Move render pipeline resource cache/lifecycle/runtime-service tests.
- [ ] `Unit/Rendering/Vulkan/`
  - [ ] Move Vulkan pure data-model tests.
  - [ ] Move Vulkan feature-profile and settings resolver tests.
- [ ] `Contracts/SourceContracts/Rendering/Shaders/`
  - [ ] Move shader source text contract tests.
- [ ] `Contracts/SourceContracts/Rendering/Vulkan/`
  - [ ] Move Vulkan source-text and diagnostic wiring contract tests.
- [ ] `Contracts/SourceContracts/Rendering/OpenXR/`
  - [ ] Move OpenXR source-contract and docs-contract tests.
- [ ] `Hardware/OpenGL/Rendering/`
  - [ ] Move `GpuTestBase`-derived OpenGL compile/integration tests.
- [ ] `Hardware/Vulkan/Rendering/`
  - [ ] Move tests that require Vulkan runtime or driver validation.
- [ ] `Integration/Rendering/`
  - [ ] Move runtime service and window/controller tests that cross subsystems
    but can run without real GPU hardware.

## Phase 4 - Non-Rendering Decomposition

- [ ] Move math and geometry tests:
  - [ ] `XRMath/` -> `Unit/XRMath/`,
  - [ ] `Geometry/` -> `Unit/Geometry/`.
- [ ] Move data/core tests:
  - [ ] pure data tests -> `Unit/Data/`,
  - [ ] source-generated/AOT contract tests -> `Contracts/SourceContracts/Core/`,
  - [ ] profiler/logging runtime tests -> `Unit/Core/Diagnostics/` or
    `Integration/Core/Diagnostics/`.
- [ ] Move scene tests:
  - [ ] lifecycle and transform behavior -> `Unit/Scene/`,
  - [ ] YAML/deserialization with GPU fixture -> `Integration/Scene/` or
    `Hardware/OpenGL/Scene/` depending on actual requirements.
- [ ] Move editor tests:
  - [ ] editor services with no runtime startup -> `Unit/Editor/`,
  - [ ] editor/world settings and asset cooking -> `Integration/Editor/`,
  - [ ] slow archive tests -> `Integration/Editor/Packaging/` with
    `[Explicit]` or `[Category(TestCategories.Slow)]`.
- [ ] Move asset importer tests:
  - [ ] FBX parser/semantic tests -> `Unit/Assets/Fbx/`,
  - [ ] FBX/glTF corpus round trips -> `Integration/Assets/...`,
  - [ ] native DLL smoke tests -> `Hardware/NativeInterop/`.
- [ ] Move audio tests:
  - [ ] pure processors/settings -> `Unit/Audio/`,
  - [ ] transport/native/OpenAL tests -> `Hardware/Audio/` or
    `Integration/Audio/`.
- [ ] Move physics tests:
  - [ ] pure component and topology behavior -> `Unit/Physics/`,
  - [ ] compute/OpenGL tests -> `Hardware/OpenGL/Physics/`,
  - [ ] native Jolt/PhysX tests -> `Hardware/NativeInterop/Physics/`.
- [ ] Move MCP tests:
  - [ ] registry/rate limiter -> `Unit/Mcp/`,
  - [ ] protocol/host-level tests -> `Integration/Mcp/`,
  - [ ] docs parity -> `Contracts/DocsContracts/Mcp/`.

## Phase 5 - Rename Phase/TODO/Backlog Tests

For each renamed test, preserve intent and update any CI filters.

- [ ] Rename files/classes containing `Todo` to behavior names.
- [ ] Rename files/classes containing `Backlog` to behavior names.
- [ ] Rename files/classes containing `Completion` to behavior names.
- [ ] Rename files/classes containing `PhaseN`, `P0`, `P1`, or `P2` unless the
  phase is part of a stable external spec.
- [ ] Update namespaces after renames.
- [ ] Update any references in docs, workflows, or test filters.
- [ ] Keep a rename map in the Phase 5 validation note.

Suggested rename examples:

| Current | Better |
|---|---|
| `VulkanTodoP2ValidationTests` | `VulkanPipelineValidationTests` |
| `GpuRenderingBacklogTests` | `GpuRenderingArchitectureContractTests` |
| `OpenXrStereoTemporalIsolationCompletionTests` | `OpenXrTemporalIsolationTests` |
| `AlphaToCoveragePhase2Tests` | `AlphaToCoveragePipelineTests` |
| `GpuIndirectPhase7ZeroReadbackTests` | `GpuIndirectZeroReadbackTests` |
| `VulkanP0ValidationTests` | `VulkanFrameAndDescriptorValidationTests` |
| `VulkanP1ValidationTests` | `VulkanCommandAndStateValidationTests` |

## Phase 6 - Reflection And Source-Text Debt

- [ ] Inventory private reflection use.
- [ ] For each private reflection test, decide:
  - [ ] replace with public behavior assertion,
  - [ ] add `internal` diagnostic/test seam with `InternalsVisibleTo`,
  - [ ] move to `Contracts` and document why private shape is the contract,
  - [ ] delete if it only tests implementation trivia.
- [ ] Inventory tests that read repo source files.
- [ ] Replace source-string checks with stable seams when practical:
  - [ ] generated manifests,
  - [ ] public diagnostic descriptors,
  - [ ] internal registries,
  - [ ] shader reflection output,
  - [ ] settings metadata.
- [ ] Leave unavoidable source-contract tests in `Contracts/SourceContracts`.
- [ ] Add helper assertions for source-contract tests so string checks include
  clear failure messages and repo-relative paths.

## Phase 7 - Project Splitting

Only start after folder moves and category rules are stable.

- [ ] Create `tests/XREngine.Tests.Shared/XREngine.Tests.Shared.csproj`.
- [ ] Create `tests/XREngine.Tests.Unit/XREngine.Tests.Unit.csproj`.
- [ ] Create `tests/XREngine.Tests.Contracts/XREngine.Tests.Contracts.csproj`.
- [ ] Create `tests/XREngine.Tests.Integration/XREngine.Tests.Integration.csproj`.
- [ ] Create `tests/XREngine.Tests.Hardware/XREngine.Tests.Hardware.csproj`.
- [ ] Create `tests/XREngine.Tests.Performance/XREngine.Tests.Performance.csproj`
  if performance tests remain substantial enough to justify a project.
- [ ] Move shared helpers into `XREngine.Tests.Shared`.
- [ ] Minimize project references:
  - [ ] unit tests should avoid `XREngine.Editor` unless testing editor logic,
  - [ ] contracts should reference only what they need to compile helpers,
  - [ ] hardware tests may reference rendering/native projects,
  - [ ] integration tests may reference editor/control-plane projects.
- [ ] Keep `XREngine.UnitTests` as a temporary compatibility project or remove
  it after solution/workflow updates.
- [ ] Update `XRENGINE.slnx`.
- [ ] Update `.vscode/tasks.json` test tasks.
- [ ] Update CI workflows and any focused Vulkan/OpenXR test lanes.

## Phase 8 - Test Data And Output Cleanup

- [ ] Decide whether checked-in `TestData` remains under the test project or
  moves to `tests/XREngine.TestData`.
- [ ] Add a `TestData/README.md` explaining:
  - [ ] source/license of each corpus asset,
  - [ ] why each file is small enough to keep,
  - [ ] which tests consume it,
  - [ ] how to regenerate summaries.
- [ ] Move large generated baselines out of source if they are not durable.
- [ ] Ensure test-generated files write only to:
  - [ ] test-owned temp directories,
  - [ ] `TestResults`,
  - [ ] `Build/_AgentValidation/<run>/`,
  - [ ] never arbitrary repo source paths.
- [ ] Add cleanup helpers for temp output.

## Phase 9 - Meta-Tests And Guard Rails

Add tests or a small analyzer-style validation suite that fails when new tests
violate placement rules.

- [ ] Fail if a new test file is added directly under broad buckets like
  `Rendering/` after migration.
- [ ] Fail if a test class/file name contains `Todo`, `Backlog`, or
  `Completion` outside a migration quarantine folder.
- [ ] Fail if a test class/file name contains `Phase\d+`, `P0`, `P1`, or `P2`
  outside an allowlist.
- [ ] Fail if xUnit attributes such as `[Fact]` or `[Theory]` appear in NUnit
  test projects.
- [ ] Fail if a test reading repo source files is outside `Contracts`.
- [ ] Fail if `GpuTestBase` inheritance appears outside `Hardware`.
- [ ] Fail if `[Explicit]` hardware/native tests do not explain the local
  prerequisite.
- [ ] Warn or fail on private reflection outside approved helpers.
- [ ] Fail if test output writes into repo source folders.
- [ ] Fail if a project references `XREngine.Editor` from the pure unit project.

## Phase 10 - Documentation And Developer Workflow

- [x] Update `docs/work/README.md` with the active test reorganization TODO.
- [ ] Update `docs/developer-guides/testing.md` or create it if no testing guide
  exists.
- [ ] Document common commands:
  - [ ] fast default unit + contract lane,
  - [ ] integration lane,
  - [ ] hardware OpenGL lane,
  - [ ] hardware Vulkan lane,
  - [ ] OpenXR/manual lane,
  - [ ] performance lane.
- [ ] Update `AGENTS.md` with:
  - [ ] test lane placement rules,
  - [ ] category requirements,
  - [ ] prohibition on TODO/phase names for permanent tests,
  - [ ] instruction to add test support helpers instead of copying ad hoc
    `ReadWorkspaceFile` and reflection helpers.
- [ ] Update any PR template/checklist if one exists.

## Validation Gates

Minimum validation after each move batch:

- [ ] `dotnet build XRENGINE.slnx`
- [ ] `dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj` until project
  splitting lands.
- [ ] After splitting, run:
  - [ ] unit project,
  - [ ] contracts project,
  - [ ] integration project default lane,
  - [ ] hardware project discovery without executing explicit tests.
- [ ] Confirm no tracked `bin`, `obj`, `TestResults`, or generated run output.
- [ ] Confirm old and new test counts match except for documented deletions or
  intentionally merged duplicates.
- [ ] Confirm CI/workflow filters still find focused Vulkan/OpenXR tests.

## Deletion Policy

Tests may be deleted only when at least one condition is true:

- [ ] Duplicate coverage exists in a clearer test and the duplicate adds no
  distinct assertion.
- [ ] The test asserts implementation trivia that is no longer a contract.
- [ ] The test is permanently invalid because the production behavior changed
  intentionally and a replacement test covers the new contract.
- [ ] The test was a temporary TODO/completion sentinel and the durable
  behavior is now covered elsewhere.

Every deletion must be listed in the migration note with:

- old path,
- reason,
- replacement coverage or explicit "no replacement needed" rationale.

## Risks

- Large moves can obscure real behavior changes. Keep moves separate from test
  rewrites where practical.
- Source-contract tests may become noisy during active renderer refactors; move
  them to the contract lane before judging their value.
- Splitting projects may expose hidden project-reference coupling. Treat that
  as useful signal, but fix it in small batches.
- Hardware tests can destabilize the default lane if category gates are wrong.
- Renaming tests may break CI filters, local scripts, and docs links.

## Open Questions

- Should split projects live under a new top-level `tests/` folder, or should
  `XREngine.UnitTests` be renamed in place first?
- Should source-contract tests remain NUnit tests, or should long-term source
  architecture checks become Roslyn analyzers/report scripts?
- Should hardware lanes be part of normal CI discovery, or only manual/local
  tasks until dedicated runners exist?
- Should the pure unit project remove the `XREngine.Editor` reference
  immediately, or after all editor tests move?
- Should existing checked-in corpus summaries remain under `TestData`, or move
  to a shared `tests/XREngine.TestData` package?

## First Implementation Slice

A good first PR should be intentionally boring:

- [ ] Add `Shared/TestCategories.cs`.
- [ ] Add `Shared/RepoPaths.cs`.
- [ ] Add `XREngine.UnitTests/README.md`.
- [ ] Move `GpuTestBase.cs` to `Shared/Fixtures/GpuTestBase.cs`.
- [ ] Move `LightmapBakeManagerTests.cs` to
  `Unit/Rendering/Lightmapping/LightmapBakeManagerTests.cs`.
- [ ] Move one obvious source-contract file to
  `Contracts/SourceContracts/Rendering/...`.
- [ ] Add meta-test for xUnit attributes in the NUnit project.
- [ ] Run the default test project and record results in the PR.
