# Test Suite Layout

[Developer guides](../README.md) · [Unit test project reorganization TODO](../../work/todo/tests/unit-test-project-reorganization-todo.md)

This guide defines where tests belong. Tests must verify code behavior or stable contracts. Tests must not read Markdown docs in `docs/`, `README.md`, todo files, or guide files.

## Current Layout

The current default test project is `XREngine.UnitTests/XREngine.UnitTests.csproj`. It targets `net10.0-windows7.0` and uses NUnit. It still references broad product projects, including `XREngine.Editor`. It also links sample gameplay files and render-benchmark helpers. `XREngine.UnitTests/Headless/XREngine.HeadlessTests.csproj` reuses a small set of tests for a headless executable lane.

The current folders are not yet the target layout. `Rendering/` is still the largest flat bucket. `GpuTestBase.cs` is still at the project root. No shared `TestCategories` source exists yet.

## Target Lanes

Organize tests by purpose first and subsystem second.

| Lane | Use for | Default run |
|---|---|---|
| Unit | Fast deterministic tests for one small API surface. | Included. |
| Contracts | Source, metadata, generated manifest, and architecture wiring contracts. | Included when stable and not slow. |
| Integration | Cross-subsystem behavior without real GPU, headset, native hardware, or driver-specific setup. | Included only when small and deterministic. |
| Hardware | GPU contexts, graphics APIs, native DLLs, headset runtimes, drivers, or device-specific support. | Excluded unless the lane provisions requirements. |
| Performance | Benchmarks, timing baselines, allocation checks, and profiling sentinels. | Excluded. |

## Intermediate Folder Layout

Use this layout before project splitting if a direct split is too noisy:

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

Place reusable assertions, fixtures, path helpers, and output helpers under `Shared/`. Do not copy ad hoc file readers or reflection helpers into each test.

## Long-Term Project Layout

Use this split after lane categories and folder moves are stable:

```text
tests/
  XREngine.Tests.Shared/
  XREngine.Tests.Unit/
  XREngine.Tests.Contracts/
  XREngine.Tests.Integration/
  XREngine.Tests.Hardware/
  XREngine.Tests.Performance/
  XREngine.TestData/
```

Keep project references narrow. The pure unit project must not reference `XREngine.Editor` unless it tests editor logic through a deliberate seam. Hardware tests can reference rendering and native projects. Integration tests can reference editor or control-plane projects only when the test requires them.

## Categories

Add one shared source of truth for NUnit categories before broad moves:

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

Default local and CI runs include `Unit`, `Contract`, and small `Integration` tests. They exclude `Hardware`, `Performance`, `Slow`, and `[Explicit]` tests.

## Unit Tests

Use unit tests for in-memory behavior, small value types, deterministic clocks, fake services, and isolated temp files.

Do not use unit tests for GPU contexts, real windows, native DLL availability, editor startup, OpenXR or OpenVR runtimes, source-file string scanning, or long-running performance thresholds.

## Contract Tests

Use contract tests for stable source-level or metadata-level invariants. File and class names should end in `ContractTests`.

Prefer generated manifests, public metadata, diagnostic descriptors, internal registries, shader reflection output, or settings metadata. Use raw source text only when no stable seam exists. Put source-text tests under `Contracts/SourceContracts/`. Do not scan Markdown docs.

## Integration Tests

Use integration tests for model import, serialization, asset package round trips, runtime service bootstrap with fake hosts, and unit-testing world settings.

Use isolated temp directories. Keep checked-in test data small. Mark slow tests with `[Category(TestCategories.Slow)]` or `[Explicit]` when the default lane must not run them.

## Hardware Tests

Use hardware tests for GPU, graphics API context, native DLL, headset runtime, and driver-specific behavior.

Mark hardware tests with `[Category(TestCategories.Hardware)]` and a precise category such as `OpenGL`, `Vulkan`, `OpenXR`, or `NativeInterop`. Use `[Explicit]` unless the CI lane provisions the requirement. If prerequisites are missing, report `Assert.Inconclusive` with a precise reason. Never hang while waiting for a visible window or headset.

## Performance Tests

Use performance tests for benchmark and profiling sentinels. Do not run them by default. Write output to a test-owned output directory or a task-run report directory. Use relative or machine-independent paths in reports. Thresholds must be relative to a local baseline or wide enough for machine variance. Do not use performance tests as proof of visual correctness.

## Naming Rules

Use stable behavior names:

- `VulkanDescriptorLifetimeTests`
- `OpenXrTemporalHistoryIsolationTests`
- `LightmapBakeManagerTests`
- `ShaderSourceResolverCachingTests`

Do not use work-plan history in permanent names. Avoid terms such as `Todo`, `Backlog`, `Completion`, temporary milestone labels, and priority labels unless the term is part of a stable external specification. Temporary migration names can exist only under a quarantine folder and must have an open code item.

## Test Data And Output

Keep small durable corpus assets under `TestData` or the later `XREngine.TestData` project. Add a local `README.md` for corpus source, license, purpose, consumers, and regeneration steps when that test-data package is created.

Test-generated files can write only to test-owned temp directories, `TestResults`, or a task-run report directory. Tests must not write arbitrary source folders. Disposable output must stay out of the repository.

## Common Commands

Use these commands from the repository root unless a task gives a narrower command:

```powershell
dotnet test XREngine.UnitTests\XREngine.UnitTests.csproj
```

Run focused lanes with NUnit category filters after categories exist. Example filter forms:

```powershell
dotnet test XREngine.UnitTests\XREngine.UnitTests.csproj --filter "TestCategory!=Hardware&TestCategory!=Performance&TestCategory!=Slow"
dotnet test XREngine.UnitTests\XREngine.UnitTests.csproj --filter "TestCategory=Integration"
dotnet test XREngine.UnitTests\XREngine.UnitTests.csproj --filter "TestCategory=OpenGL"
dotnet test XREngine.UnitTests\XREngine.UnitTests.csproj --filter "TestCategory=Vulkan"
dotnet test XREngine.UnitTests\XREngine.UnitTests.csproj --filter "TestCategory=OpenXR"
dotnet test XREngine.UnitTests\XREngine.UnitTests.csproj --filter "TestCategory=Performance"
```

## Guard Rails

Add meta-tests or analyzer-style checks during the reorganization:

- fail if a new test file is added directly under broad buckets after migration;
- fail if permanent test names contain work-plan history terms;
- fail if xUnit attributes appear in NUnit projects;
- fail if a source-reading test is outside `Contracts/SourceContracts`;
- fail if `GpuTestBase` inheritance appears outside `Hardware`;
- fail if explicit hardware tests omit prerequisite guidance;
- warn or fail on private reflection outside approved helpers;
- fail if test output writes into source folders;
- fail if the pure unit project references `XREngine.Editor`.

## Deletion Policy

Delete a test only when duplicate clearer coverage exists, the test asserts non-contract implementation trivia, production behavior intentionally changed and replacement coverage exists, or the test was a temporary sentinel. Record each deletion in the migration note with the old path, reason, and replacement coverage or `no replacement needed`.
