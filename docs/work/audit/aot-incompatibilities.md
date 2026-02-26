# AOT Compatibility Scan (XRENGINE)

Date: 2025-12-22

This document tracks code patterns in this repo that are **incompatible** or **high-risk** for .NET **NativeAOT** (and, secondarily, ILLink trimming).

> Scope notes
>
> - The repo contains multiple apps (Editor, VRClient, Server, etc.) and third-party code under `Build/Submodules/`.
> - **AOT viability is per-final-app**. Some findings are “Editor-only” and may not matter if the Editor is never AOT-compiled.
> - Findings are grouped by severity and include a suggested remediation path.

---

## Legend

- **🚫 AOT-blocker**: Will not work (or is effectively unsupported) under NativeAOT.
- **⚠️ AOT-risk**: Can compile but may fail at runtime, produce IL3050 warnings, or require explicit metadata.
- **🧹 Trimming-risk**: Primarily a linker/trimming issue (IL2026/IL20xx).

---

## AOT-blockers (must redesign/remove)

### AOT-001 — Runtime enum generation via `System.Reflection.Emit`

- Location: `XREngine.VRClient/Program.cs`
- Pattern:
  - `AssemblyBuilder.DefineDynamicAssembly(...)`
  - `moduleBuilder.DefineEnum(...)`
  - `MakeGenericMethod([...dynamic types...])`
- Why this breaks AOT:
  - NativeAOT does not support runtime IL emission / defining new managed types.
- Suggested AOT-compatible approaches:
  1. **Build-time codegen** (recommended):
     - Move the “action names” config to a build input and generate a `.cs` file with `enum EActionCategory` / `enum EGameAction` at build time.
     - Then call `GenerateGameSettings<EActionCategory, EGameAction>()` directly.
  2. **Stop requiring enums**:
     - Replace the generic enum parameters with string IDs (or integer IDs) and validate at runtime.
     - If you need type safety, use `readonly struct` wrappers around `int`/`string`.

### AOT-002 — Dynamic assembly loading (plugin system)

- Location: `XRENGINE/Scene/Components/Scripting/GameCSProjLoader.cs`
- Pattern:
  - `AssemblyLoadContext` + `LoadFromStream` / `LoadFromStream(pdb)`
  - `assembly.GetExportedTypes()` to discover subclasses of `XRComponent` / `XRMenuItem`
- Why this breaks AOT:
  - NativeAOT apps generally cannot load arbitrary new managed assemblies at runtime.
  - Even when some forms of loading are technically possible in limited scenarios, the reflection + metadata requirements are not compatible with typical trimming/AOT workflows.
- Suggested AOT-compatible approaches:
  1. **No runtime managed plugin loading** for AOT targets:
     - Gate behind `#if !AOT` (or a runtime feature flag) and disable for NativeAOT builds.
  2. **Compile-time plugin registration**:
     - Make “plugins” project references included at build time.
     - Use a source generator or an explicit registry to list available `XRComponent`/`XRMenuItem` types.
  3. **Scripting alternative**:
     - Use an interpreted scripting language (Lua, JS via embedding, etc.) for AOT builds.
     - Keep the C# dynamic-load path for non-AOT desktop builds.

### AOT-003 — `dynamic` COM automation (WScript.Shell)

- Location: `Build/Submodules/Flyleaf/FlyleafLib/Utils/Utils.cs` (method `GetLnkTargetPath`)
- Pattern:
  - `dynamic windowsShell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)`
  - `dynamic shortcut = windowsShell.CreateShortcut(filepath)`
- Why this breaks AOT:
  - `dynamic` uses the runtime binder and reflection-heavy paths.
  - COM automation via ProgID + dynamic dispatch is brittle under trimming/AOT.
- Suggested AOT-compatible approaches:
  1. If this is needed for your AOT target, replace with **typed interop**:
     - Use CsWin32 (`Microsoft.Windows.CsWin32`) to generate `IShellLinkW` / `IPersistFile` COM interop and resolve `.lnk` targets via ShellLink.
  2. If not needed for AOT targets, **exclude / conditionally compile** it.

---

## AOT-risk (fixable with metadata / refactors)

### AOT-010 — Expression tree compilation (`Expression.Compile()`)

- Location: `XRENGINE/Core/Tools/DelegateBuilder.cs`
- Pattern:
  - `Expression.Lambda<T>(...).Compile()`
- Why this is risky:
  - On many runtimes, `Compile()` uses runtime codegen (DynamicMethod / IL emission) which is not supported in NativeAOT.
- Suggested AOT-compatible approaches:
  1. Prefer `MethodInfo.CreateDelegate(...)` where possible:
     - For a direct wrapper to a method, `CreateDelegate` is the simplest AOT-safe path.
  2. If you must build a “partial application” wrapper:
     - Use `Compile(preferInterpretation: true)` (if acceptable for perf) to avoid runtime codegen.
     - Or fall back to a reflection invoke path for AOT builds (slower but functional).
  3. Best long-term: generate strongly-typed delegates at build time (source generator).

### AOT-011 — `System.Text.Json` runtime serialization of arbitrary types (IL3050)

- Locations:
  - `XRENGINE/Scene/Components/Networking/RestApiComponent.cs` (Serialize/Deserialize payload helpers are explicitly suppressed for IL3050)
  - `XRENGINE/Scene/Components/Networking/WebhookListenerComponent.cs` (similar pattern)
  - `XRENGINE/Engine/Engine.VRState.cs` (Serialize/Deserialize of `VRInputData` is annotated with `RequiresDynamicCode`)
  - Multiple other call sites in engine/submodules
- Pattern:
  - `JsonSerializer.Deserialize<T>(...)` where `T` may be user-provided or otherwise not statically known.
  - `JsonSerializer.Serialize(payload, payload.GetType(), ...)`
- Why this is risky:
  - NativeAOT requires JSON metadata to be available at compile time.
  - Arbitrary caller-provided types are not representable unless you provide a registration mechanism.
- Suggested AOT-compatible approaches:
  1. For engine-owned payload types:
     - Add a `JsonSerializerContext` (source-generated) that includes all known engine DTOs.
     - Use `JsonSerializer.Serialize(value, EngineJsonContext.Default.SomeType)` / `Deserialize(..., EngineJsonContext.Default.SomeType)`.
  2. For genuinely user-defined arbitrary payload types:
     - Change API to accept/return `JsonNode`/`JsonDocument`/`string` in AOT builds.
     - Or require user code to provide a `JsonTypeInfo<T>` or a `JsonSerializerContext` at registration time.

### AOT-012 — Reflection-based binary serialization (`CookedBinarySerializer`)

- Location: `XRENGINE/Core/Files/CookedBinarySerializer.cs` (and associated cooked-binary APIs)
- Pattern:
  - Heavy `System.Reflection` usage for reading/writing arbitrary objects.
  - Many public methods are annotated with `RequiresUnreferencedCode` + `RequiresDynamicCode`.
- Why this is risky:
  - Reflection-driven serializers require runtime metadata; trimming can remove it.
  - NativeAOT often cannot support “serialize arbitrary object graph” without explicit shape metadata.
- Suggested AOT-compatible approaches:
  1. Prefer **per-type serialization**:
     - Require `ICookedBinarySerializable` to be implemented for all types used in AOT builds.
  2. Prefer **source-generated serialization**:
     - If feasible, migrate cooked-binary payloads to MemoryPack (already referenced) or another generator-based serializer.
  3. Add a build-time “type registry”:
     - Enumerate all serializable types for the target app and generate lookup tables instead of reflection.

### AOT-013 — `Marshal.PtrToStructure<T>` / runtime marshalling for generic structs

- Location: `XRENGINE/Rendering/API/Rendering/Objects/Buffers/XRDataBuffer.cs`
- Pattern:
  - `Marshal.PtrToStructure<T>(...)` for generic `T : struct`
  - Suppression: `IL3050` in `Get<T>`
- Why this is risky:
  - Generic marshalling can require runtime-generated marshalling stubs.
  - AOT-friendly marshalling usually requires `unmanaged` and blittable layouts.
- Suggested AOT-compatible approaches:
  1. Restrict to blittable/unmanaged:
     - Change constraints to `where T : unmanaged`.
  2. Replace marshalling calls:
     - Use `Unsafe.ReadUnaligned<T>(...)` / `MemoryMarshal.Read<T>(...)` over a `Span<byte>`.
  3. Keep a non-AOT fallback if needed.

### AOT-014 — Scanning AppDomain assemblies/types at runtime

- Location: `XRENGINE/Scene/Transforms/TransformBase.cs` (`GetAllTransformTypes()`)
- Pattern:
  - `AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => a.GetExportedTypes())...`
  - Uses `GetCustomAttribute<DisplayNameAttribute>()` on each type
  - Marked with `RequiresUnreferencedCode`
- Why this is risky:
  - Trimming can remove types/attributes.
  - In AOT, “discover all types in all assemblies” is a red flag; it’s hard to preserve everything.
- Suggested AOT-compatible approaches:
  1. Replace runtime discovery with explicit registration:
     - Maintain a `static readonly Type[] TransformTypes = { typeof(Transform), typeof(RigidBodyTransform), ... }` for AOT builds.
  2. Use a source generator to auto-populate this list.
  3. If you must keep discovery for non-AOT builds, gate it behind build flags.

### AOT-015 — Prefab variant instancing relies on reflection

- Location: `XRENGINE/Rendering/XRWorldInstance.cs` (and prefab services)
- Pattern:
  - Public APIs annotated with `[RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]`
- Why this is risky:
  - Prefab overrides often require reflection to diff/apply properties.
- Suggested AOT-compatible approaches:
  1. Use generated “override apply” code per prefab component type (source generator).
  2. Restrict prefab override support in AOT builds to a fixed set of known fields/properties.
  3. Add explicit metadata via linker descriptors (works for trimming, does not fix dynamic-code needs).

---

## Editor-only reflection (likely OK if Editor is not AOT)

The Editor project contains extensive reflection usage (`PropertyInfo`, `MethodInfo`, `Activator.CreateInstance`, `Type.GetType(string)`, etc.).
This is typically fine if:

- The **Editor app is never shipped as NativeAOT**, and
- AOT targets are limited to runtime apps (e.g., game client/server).

Representative locations:

- `XREngine.Editor/Undo.cs` (reflection metadata caches)
- `XREngine.Editor/UI/Panels/Inspector/*` (property editors use `PropertyInfo` heavily)
- `XREngine.Editor/ProjectBuilder.cs` (`Type.GetType(typeName, ...)`)
- `XREngine.Extensions/Reflection/*` (general reflection helpers)

If you do want an AOT-friendly Editor build, these areas would need the same treatment as above:
explicit type registries + generated code + removal of string-based type resolution.

---

## Recommended “AOT mode” strategy

1. Decide which apps are AOT targets (likely `XRENGINE` runtime, maybe `XREngine.Server`; probably not the Editor).
2. Add a build symbol (e.g., `AOT`) and/or project property to gate features:
   - Disable dynamic plugin loading
   - Disable runtime type discovery
   - Disable reflection-based generic JSON and cooked-binary fallback
3. Add source generators for:
   - Transform type registry
   - Prefab override apply code
   - JSON serializers (System.Text.Json source generation)
   - (Optional) cooked-binary serialization

---

## Architecture option: hidden non-AOT GameHost process (AOT engine + hotload)

If you **must** support user game code load/unload at runtime *and* want an AOT-compiled engine, the most robust model is to split into two OS processes:

- **EngineHost** (NativeAOT): renderer, physics, audio, asset IO, networking, deterministic simulation core, platform integration.
- **GameHost** (CoreCLR / non-AOT): loads/unloads user assemblies via `AssemblyLoadContext`, runs user gameplay logic, reflects over user types, uses dynamic JSON, etc.

The EngineHost treats GameHost as an external authority that issues gameplay decisions.

### High-level responsibilities

**EngineHost (AOT)**

- Owns the world state authoritative data structures.
- Executes the main loop, frame timing, and physics steps.
- Applies gameplay commands from GameHost.
- Publishes read-only snapshots/events to GameHost.

**GameHost (non-AOT)**

- Loads/unloads user assemblies and discovers entry points.
- Maintains user script instances.
- Produces gameplay commands based on snapshots/events.
- Optionally hosts editor/dev tooling hooks.

### IPC transport options (Windows)

- Named pipes (recommended first): simple, fast, works well for local hidden helper process.
- TCP loopback: easiest to debug with external tools; slightly more overhead.
- Shared memory + events: highest throughput; more complexity.

### Suggested message protocol

Use a versioned binary protocol with explicit schemas; avoid reflection-based “serialize arbitrary object graphs”.

- `Hello/Handshake` (protocol version, feature flags)
- `LoadGame` (path, config, optional PDB)
- `UnloadGame` (id)
- `Tick` / `FixedTick` (delta times + input)
- `Snapshot` (world state delta or selected query results)
- `Commands` (GameHost -> EngineHost: spawn, destroy, set component values, play sounds, etc.)
- `Logs` / `Diagnostics` (both directions)

For serialization, choose one of:

- MemoryPack (already present in repo in places) for DTOs
- System.IO.Pipelines + custom structs for hot paths

### Load/unload flow

1. EngineHost launches GameHost hidden (no window) with a pipe name/token.
2. GameHost connects and handshakes.
3. EngineHost sends `LoadGame` with a path/stream.
4. GameHost loads assembly into a collectible `AssemblyLoadContext` and instantiates the entry point.
5. Per frame/step:
  - EngineHost sends `Snapshot` and `Tick` / `FixedTick`.
  - GameHost replies with `Commands`.
6. On reload:
  - EngineHost sends `UnloadGame`.
  - GameHost drops references, calls `Unload()`, and forces GC cycles to reclaim.

### How to keep this maintainable

- Treat all cross-process types as **DTO contracts** in a small shared project (IL library) with no reflection usage.
- Keep the EngineHost API surface minimal: “query + command” instead of exposing internal engine objects.
- Make GameHost stateless-ish: it should be able to reconstruct state from snapshots after reload.

### Tradeoffs

- Added latency/overhead for IPC and snapshotting.
- Requires designing a stable command/snapshot contract.
- In exchange, you get clean separation: AOT-friendly engine and fully dynamic user code.

---

## Workflow option: non-AOT Engine/Editor, NativeAOT only for cooked final game EXE

This is typically the most practical approach:

- **During development**: Engine + Editor run on CoreCLR/JIT (fast iteration, reflection/dynamic OK).
- **When cooking/shipping**: you build a dedicated **game executable** with `dotnet publish` + NativeAOT.

Important constraint: the *shipped* game EXE must avoid (or gate off) AOT blockers:

- No runtime `AssemblyLoadContext` user code loading
- No `System.Reflection.Emit` / `Expression.Compile()` codegen
- No “arbitrary type” `System.Text.Json` without source-gen `JsonSerializerContext`

### How to enable AOT only at cook/publish time

1. Keep `PublishAot` **off** in library projects (engine, shared libs).
2. Enable `PublishAot=true` only on the **final executable project** when publishing.

Recommended mechanism: a publish profile (`.pubxml`) for the final game EXE.

Example `dotnet publish` invocation (Windows x64):

- `dotnet publish path/to/YourGame.csproj -c Release -r win-x64 -p:PublishAot=true -p:SelfContained=true`

You can also add additional knobs per project needs:

- `-p:PublishTrimmed=true` (only once you have trimming-safe annotations/roots)
- `-p:InvariantGlobalization=true` (if acceptable)

### Structuring projects for this workflow

- **Engine library** (e.g., `XRENGINE/XREngine.csproj`): keep as a normal class library; it becomes part of the AOT image when referenced by the final EXE.
- **Editor EXE**: stays non-AOT.
- **Final Game EXE**: a separate entrypoint project that references:
  - Engine + required runtime libs
  - The cooked/compiled game code (statically referenced; no dynamic loading)

If you want “user code hot reload” in dev but “static code” in shipping:

- Build user game code into a normal referenced project for Release shipping.
- Keep dynamic load/unload tooling only in dev/editor builds.

---

## Tracking checklist

- [ ] Remove/replace Reflection.Emit enum generation (AOT-001)
- [ ] Decide policy for runtime C# plugin loading (AOT-002)
- [ ] Replace Expression.Compile usage or switch to interpretation (AOT-010)
- [ ] Convert JSON usage to source-gen contexts for engine DTOs (AOT-011)
- [ ] Convert cooked-binary serialization to generator/per-type (AOT-012)
- [ ] Restrict XRDataBuffer generic marshalling to `unmanaged` (AOT-013)
- [ ] Replace type discovery with generated registries (AOT-014)
- [ ] Address prefab override reflection (AOT-015)
