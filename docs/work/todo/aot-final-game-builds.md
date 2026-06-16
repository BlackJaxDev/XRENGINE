# AOT Final Game Builds

Last Updated: 2026-06-16
Status: A representative cooked final game launcher now publishes with NativeAOT and passes the generated `--aot-smoke` check. The production gate remains closed because analyzer validation still reports AOT/trim warnings in the generated launcher's shipped closure.

## Scope

First-class AOT target:

- `Cooked Final Game` launcher produced by `BuildSettings.PublishLauncherAsNativeAot`.
- Windows `win-x64`, self-contained, `PublishAot=true`.
- Published launchers define `XRE_PUBLISHED` and `XRE_AOT_RUNTIME`.
- Runtime boundary checks go through `XRRuntimeEnvironment`.
- Build/cook metadata is stored in `AotRuntimeMetadata.bin` inside the published config archive.

Explicitly out of the first AOT target:

- `XREngine.Editor` and all editor/dev reflection tooling.
- Runtime C# plugin loading and hot-reload workflows.
- `XREngine.VRClient` until it gets its own validated publish flow.
- `XREngine.Server` until it gets its own validated publish flow.
- Optional runtime integration projects unless a final launcher statically includes them.

## Capability Matrix

| Runtime | AOT target now | Dynamic managed plugins | YAML assets at runtime | Runtime type discovery | Notes |
|---|---:|---:|---:|---:|---|
| Editor/Dev | No | Yes | Yes | Yes | CoreCLR/JIT development surface. |
| Cooked Final Game | Yes | No | No | Generated metadata/registries only | First supported AOT target. |
| Dedicated Server | Deferred | No in eventual AOT mode | No in eventual AOT mode | Generated metadata/registries only | Needs separate publish validation. |
| VR Client | Deferred | No | No | Generated metadata/registries only | Static enum work is done, but publish flow is not validated. |

## Target Outcome

NativeAOT final game builds are considered ready when:

- a representative cooked final launcher publishes with `PublishAot=true`
- the published executable launches and loads its config/content archives
- runtime startup, world load, registered cooked asset load, input/bootstrap, and at least one render smoke path pass
- no shipped runtime path depends on runtime IL emission, runtime managed assembly loading, unbounded assembly scanning, YamlDotNet runtime asset loading, reflection-only JSON metadata, or generic runtime marshalling stubs
- any AOT/trim warnings from the final publish are either fixed or intentionally isolated outside the shipped runtime path

## Completed Work

### Shipping Boundary

- `BuildSettings.PublishLauncherAsNativeAot` exists and is limited to generated launcher publishing.
- Generated launchers add `XRE_PUBLISHED` and `XRE_AOT_RUNTIME` automatically when NativeAOT publishing is enabled.
- `XRRuntimeEnvironment` centralizes published/AOT runtime state and published config archive path wiring.
- Editor/dev behavior remains CoreCLR/JIT and keeps dynamic workflows.

### Hard Runtime Blockers

- `XREngine.VRClient/Program.cs` no longer emits enums at runtime.
- `XRENGINE/Scene/Components/Scripting/GameCSProjLoader.cs` throws immediately in NativeAOT runtime builds.
- `XRENGINE/Core/Tools/DelegateBuilder.cs` uses `MethodInfo.CreateDelegate` where possible and an interpreted expression fallback when dynamic code is unavailable.
- `XREngine.Animation/Property/Core/AnimationMember.cs` now routes cached expression delegates through interpreted expression compilation when dynamic code is unavailable.
- The stale `System.Reflection.Emit` import in `XREngine.Animation/State Machine/Layers/AnimLayer.cs` was removed.

### Generated Metadata And Registries

- `AotRuntimeMetadata.bin` is generated during config archive creation.
- Metadata includes known runtime type names, transform types and friendly names, type redirects, world-object replication metadata, YAML converter type names, and published runtime asset type names.
- `TransformBase` resolves transform type lists from metadata in published AOT builds.
- `XRTypeRedirectRegistry` uses metadata in published AOT builds and no longer falls back to assembly scanning when AOT metadata is missing.
- `RuntimeWorldObjectBase` uses metadata in published AOT builds and no longer falls back to assembly scanning when AOT metadata is missing.
- `ProjectBuilder` now records replication metadata for all `RuntimeWorldObjectBase` descendants, not only `XRWorldObjectBase`.
- `XRTexture2D` streaming payload type resolution uses AOT metadata in published AOT builds and no longer scans assemblies in that mode.
- `Tools/Generate-AotFactoryRegistrations.ps1` scans the engine, runtime rendering, and runtime input integration sources so generated registrations include transforms, viewport render commands, camera parameter types, post-process backing types, OpenXR render pipelines, and player controllers.

### Serialization

- Published runtime blocks YAML asset serialization/deserialization and direct `.asset` YAML loading.
- `AssetManager.Serializer` and `AssetManager.Deserializer` are lazy and fail fast for published runtime use.
- Runtime-owned JSON contracts use generated `System.Text.Json` metadata for discovery, VR input data, and realtime join handoff payloads.
- Generic REST/webhook payload APIs keep flexible dev/editor paths, while typed `JsonTypeInfo` overloads are available for shipping-safe payload materialization.
- Published cooked assets use registry-backed runtime binary serializers for the current supported runtime asset set.
- Generic cooked-binary fallback remains available for editor/dev cooking and is blocked or rejected in published AOT paths when the type is not registered.

### Runtime Activation

- Transform creation, player controllers, realtime game-mode bootstrap, camera parameters, post-process backing state, viewport render commands, render-pipeline script commands, and OpenXR pipeline cloning are registry/factory-backed for published AOT.
- Published AOT paths fail with registration errors instead of falling back to unrestricted `Activator.CreateInstance`.
- Prefab cloning uses bounded hierarchy cloning in published AOT, and property/component/transform overrides require explicit handlers.

### Low-Level Marshalling

- `XREngine.Extensions/Stream.cs` uses `MemoryMarshal`/`Unsafe` for unmanaged stream reads and writes.
- `XREngine.Data/Core/Memory/DataSource.cs` uses unmanaged constraints and `Unsafe` reads/writes for struct payloads.
- `XREngine.Runtime.Rendering/Buffers/XRDataBuffer.cs` typed raw buffer APIs are constrained to `unmanaged` and no longer use `Marshal.PtrToStructure<T>` / `Marshal.StructureToPtr`.
- `XREngine.Data/Tools/CoACD.cs` reads native mesh structs with `Unsafe.ReadUnaligned`.

## Current Audit Result

### Validation Run

Latest validation:

- Command: `powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Publish-AotFinalGame.ps1 -ProjectPath Samples\MonkeyBallVR\MonkeyBallVR.xrproj -NoClean`
- Result: publish succeeded and `--aot-smoke` passed.
- Smoke proof: `Build/Reports/aot-final-game-smoke.log`
- Publish log: `Build/Reports/aot-final-game-publish.log`
- Launcher NativeAOT log copy: `Build/Reports/aot-final-game-launcher-publish.log`
- Warning report: `Build/Reports/aot-final-game-publish-warnings.md`

Smoke output:

- `32657` metadata types loaded.
- `11` registered runtime asset types loaded.
- `4` game content assets found.
- `851` common assets found.

Analyzer result:

- `389` IL2xxx/IL3xxx warnings remain in the generated launcher closure.
- Classification from `Build/Reports/aot-final-game-publish-warnings.md`:
  - cooked-binary runtime/fallback surface: `172`
  - third-party/runtime library internals: `96`
  - editor/dev authoring, import, or cache surface: `63`
  - general first-party reflection/dynamic-code follow-up: `33`
  - first-party runtime follow-up: `25`

This means the runnable smoke target is green, but the AOT compatibility gate is not green.

The 2026-06-16 solution sweep checked first-party code outside `Build/Submodules`, `bin`, and `obj` for:

- runtime IL/type emission: `System.Reflection.Emit`, `DefineDynamicAssembly`, `DynamicMethod`, `ILGenerator`
- runtime assembly loading: `AssemblyLoadContext`, `LoadFromStream`, `LoadFromAssemblyPath`
- expression compilation: `System.Linq.Expressions`, `Expression.Compile`
- unbounded type discovery: `AppDomain.CurrentDomain.GetAssemblies`, `GetTypes`, `GetExportedTypes`
- runtime activation: `Activator.CreateInstance`, `MakeGenericType`, `MakeGenericMethod`, `Type.GetType`
- JSON/YAML reflection serializers: `JsonSerializer`, `JsonConvert`, `YamlDotNet`
- runtime marshalling: `Marshal.PtrToStructure`, `Marshal.StructureToPtr`, `Marshal.SizeOf`, function-pointer delegate marshaling
- AOT/trim annotations: `RequiresDynamicCode`, `RequiresUnreferencedCode`, `DynamicallyAccessedMembers`

Result:

- No first-party runtime `System.Reflection.Emit` code remains; only documentation mentions remain.
- Expression-tree runtime codegen is either interpreted when dynamic code is unavailable or is not expression-tree compilation.
- Runtime managed assembly loading remains only in dev/editor/plugin tooling and is blocked for published AOT runtime.
- First-class final-launcher type discovery paths are metadata-backed or AOT-guarded.
- Remaining broad reflection/YAML/cooked-binary paths are editor/dev authoring, cooking, compatibility, or fallback surfaces; however, the generated final launcher still includes enough of this broad engine closure for NativeAOT analyzers to warn.
- Remaining runtime marshalling hits are native interop/function-pointer glue or reflection-based cooked-binary fallback paths. The smoke path does not trip them, but the analyzer still sees parts of the fallback surface.

No additional runtime smoke blockers were found for the first-class cooked final game launcher target. Analyzer-incompatible closure remains and must be reduced or made statically safe before this can be called supported.

## Remaining Work

### Required Before Declaring AOT Final Builds Supported

- [x] Publish a representative cooked final launcher with `PublishLauncherAsNativeAot=true`.
- [x] Launch the published executable and smoke-test startup, config archive load, content archive load, world load, and registered cooked asset load.
- [x] Capture and classify publish warnings for the generated launcher.
- [x] Add a canonical task/script or documented command for the supported AOT publish path.
- [x] Add a small AOT smoke checklist to the build docs.
- [ ] Reduce the generated launcher closure so editor/dev authoring, import, cache, profile, and fallback serializer code is not part of the shipped NativeAOT target.
- [ ] Replace or strictly isolate the generic cooked-binary runtime/fallback surface from published AOT asset loading.
- [ ] Resolve the remaining first-party runtime analyzer warnings in metadata/type resolution, runtime activation, cooked asset registry, and asset loading paths.
- [ ] Establish a policy for third-party/runtime-library warnings after the first-party closure is narrowed.

### Analyzer And Build Hygiene

- [x] Add a final-launcher publish validation mode that enables AOT/trim analyzers for the shipped closure.
- [x] Keep global editor/dev analyzers relaxed unless they become useful; `Directory.Build.props` currently disables `EnableTrimAnalyzer` and `EnableAotAnalyzer`.
- [x] Add targeted tests for AOT metadata presence and fail-fast behavior when published AOT metadata is missing.
- [x] Add tests for `XRDataBuffer` unmanaged raw reads/writes and representative registered cooked asset loads.
- [ ] Make the canonical AOT validation fail or require explicit override when first-party runtime warning buckets are non-empty.

### Optional Scope Expansion

These are not blockers for the first AOT launcher, but must be handled before expanding AOT support:

- `XREngine.Server`: needs its own AOT publish flow and smoke test.
- `XREngine.VRClient`: static enum generation is fixed, but the executable is not validated as an AOT target.
- Optional runtime audio/STT/TTS provider integrations use reflection-based JSON helpers and must move to source-generated `JsonTypeInfo` before they are included in a published AOT target.
- Any shipped user/game-defined JSON payloads must provide `JsonTypeInfo` or stay at a raw `JsonNode`/string boundary.
- Any new runtime asset type must be registered with the published cooked asset registry before it is loadable in published AOT.
- Any new runtime-created type must have an explicit factory/registry entry before it is usable in published AOT.

## Non-Goals

- Do not make the editor AOT-compatible.
- Do not remove editor plugin loading or hot-reload workflows.
- Do not require all reflection in the repository to disappear.
- Do not support arbitrary runtime C# assembly loading in NativeAOT final builds.
- Do not build a custom IL2CPP-style transpiler as part of this milestone.

## Production Gates

The production gate is still closed. It reopens only when all are true:

- [x] `PublishLauncherAsNativeAot=true` publishes a representative final launcher successfully.
- [x] The generated launcher includes `AotRuntimeMetadata.bin` in the config archive.
- [x] Published runtime initializes metadata-backed registries without assembly-scan fallbacks on the smoke path.
- [x] Published runtime blocks YAML asset loading and dynamic managed plugin loading with actionable diagnostics.
- [x] Published runtime loads only registered cooked asset types on the smoke path.
- [x] Published runtime object activation uses registered factories on covered runtime paths.
- [ ] AOT/trim publish warnings for the shipped closure are fixed, suppressed with a documented reason, or excluded by narrowing the closure.
- [x] Smoke testing proves the published executable starts and loads representative cooked archives.

## Useful Files

- `XREngine.Data/Core/BuildSettings.cs`
- `XREngine.Editor/CodeManager.cs`
- `XREngine.Editor/ProjectBuilder.cs`
- `XREngine.Runtime.Core/XRRuntimeEnvironment.cs`
- `XREngine.Runtime.Core/AotRuntimeMetadata.cs`
- `XREngine.Runtime.Core/AotRuntimeMetadataStore.cs`
- `XREngine.Runtime.Core/World/RuntimeWorldObjectBase.cs`
- `XRENGINE/Core/XRTypeRedirectRegistry.cs`
- `XRENGINE/Core/Files/CookedAssetBlob.cs`
- `XRENGINE/Core/Files/CookedAssetTypeReference.cs`
- `XRENGINE/Core/Files/CookedBinary/CookedBinarySerializer.cs`
- `XREngine.Runtime.Rendering/Core/Files/RuntimeCookedBinarySerializer.cs`
- `XREngine.Runtime.Rendering/Buffers/XRDataBuffer.cs`
