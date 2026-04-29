# fastgltf glTF Import Testing

Last updated: 2026-04-28

The fastgltf-backed `.gltf` / `.glb` import path is implemented and documented in [Model Import](../../features/model-import.md). This page preserves the delivered validation record and gives future regression runs one stable place to update expected coverage.

## Delivered Runtime Coverage

- `.gltf` and `.glb` route through the native path by default.
- Assimp remains an explicit compatibility fallback through `GltfBackend = AssimpLegacy` or `GltfBackend = Auto` rejection fallback.
- Native bridge code lives in `Build/Native/FastGltfBridge` and stages `FastGltfBridge.Native.dll` under `XREngine.Gltf/runtimes/win-x64/native`.
- `XREngine.Gltf` owns managed JSON parsing, GLB chunk validation, extras and unknown-extension retention, native-handle lifetime, and batched accessor/buffer-view copy helpers.
- The committed corpus, manifest, and golden summaries live under `XREngine.UnitTests/TestData/Gltf/`.

## Corpus

- `external-static-scene`: external buffers, external images, multiple UV/color sets, deterministic remap keys, compatibility fallback.
- `data-uri-unlit`: data URIs, `KHR_materials_unlit`, supported `KHR_texture_transform` texCoord override subset.
- `skinned-morph-animated`: skinning, default-scene selection, translation and rotation animation coverage.
- `morph-sparse-extras`: sparse accessors, morph targets, retained extras, unknown extension payload preservation.
- `embedded-buffer-view-scene`: GLB parsing, embedded BIN payloads, buffer-view-backed image reads, baked matrix transforms.
- `large-production-scene`: representative benchmark workload.
- `malformed-truncated-glb`: deterministic malformed-container rejection.

## Regression Commands

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~Gltf"
dotnet run --project .\XREngine.Benchmarks -- --gltf-phase0-report
```

Use `Build-Editor` and `Generate-UnitTestingWorldSettings` when import-policy or unit-testing-world behavior changes.

## Completion Baseline

Focused unit-test result at completion: 11 glTF tests passed, 0 failed.

Benchmark snapshot at completion:

- `large-production-scene`: native 1651.16 ms vs Assimp 1862.30 ms.
- Native allocations: 301,527,712 B vs Assimp 350,272,824 B.
- Native peak working set: 599,310,336 B vs Assimp 779,182,080 B.

Smaller synthetic fixtures remain regression coverage assets, not the optimization target for wall-time wins.

## Future Test Additions

- Add new corpus assets when supported extension coverage expands.
- Add export-specific tests only when glTF export gets its own implementation plan.
- Keep native export smoke tests current when the C ABI changes.

## Related Documentation

- [Model Import](../../features/model-import.md)
- [Unit Testing World](../../features/unit-testing-world.md)
- [Native Dependencies](../../features/native-dependencies.md)
