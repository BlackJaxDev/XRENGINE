# glTF Import Validation

Architecture: [Model Import](../../../developer-guides/assets/model-import.md), [Native Dependencies](../../../developer-guides/runtime/native-dependencies.md)  Code todos: none

Related validation: [Asset Import Validation](asset-import-validation.md).

## Setup

Use `Build-Editor` before editor import checks. Use `Generate-UnitTestingWorldSettings` when import-policy or unit-testing-world behavior changes.

Use these focused commands when glTF import behavior changes:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~Gltf"
dotnet run --project .\XREngine.Benchmarks -- --gltf-phase0-report
```

Do not run these commands from this document cleanup.

## Checks

### fastgltf Native Import

Architecture link: [Model Import](../../../developer-guides/assets/model-import.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Native route default | Import `.gltf` and `.glb` assets with default options. | Assets route through the native fastgltf-backed path. | Passed | 2026-04-28. |
| Assimp fallback | Import an asset with `GltfBackend = AssimpLegacy`, `GltfBackend = Assimp`, or an `Auto` rejection fallback. | Assimp remains an explicit compatibility path. | Passed | 2026-04-28. |
| Corpus regression | Run the focused glTF unit tests over `external-static-scene`, `data-uri-unlit`, `skinned-morph-animated`, `morph-sparse-extras`, `embedded-buffer-view-scene`, `large-production-scene`, and `malformed-truncated-glb`. | 11 focused tests pass and malformed containers reject deterministically. | Passed | 2026-04-28: 11 passed, 0 failed. |
| Benchmark snapshot | Run the glTF benchmark report. | The report compares native and Assimp wall time, allocations, and peak working set. | Passed | 2026-04-28: `large-production-scene` native 1651.16 ms, Assimp 1862.30 ms; native allocations 301,527,712 B, Assimp 350,272,824 B; native peak working set 599,310,336 B, Assimp 779,182,080 B. |
| Extension growth | Add corpus assets when supported extension coverage expands. | New supported extensions have fixtures and golden summaries. | Open | Last evidence: none. |
| Native ABI change | Run the native export or import smoke when the fastgltf C ABI changes. | Managed code and staged `FastGltfBridge.Native.dll` agree on the ABI. | Open | Last evidence: none. |

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
| None recorded | None recorded | None recorded |
