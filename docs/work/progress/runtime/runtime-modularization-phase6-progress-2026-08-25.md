# Runtime Modularization Phase 6 Progress

Started: 2026-08-25

Last updated: 2026-08-27

Status: Complete. P6.0 through P6.8 are implemented and Phase 6 closed on
2026-08-28.

Branch: `codex/runtime-modularization-phase6`

Implementation base and current HEAD at P6.0 closeout:
`76e241e5937ad29d00d435de3c32be1d095ff327` (`Vulkan work`, 2026-08-25).

Reference tracker:
[Runtime Modularization Phase 6 TODO](../../todo/COMPLETED/runtime-modularization-phase6-todo.md)

## P6.0 Decision And Scope

P6.0 accepts design Option A without modification: Phase 6 removes
`XRENGINE/XREngine.csproj` and `XREngine.dll` entirely. A permanent or empty
compatibility facade is not an accepted end state. P6.0 establishes the
stateful inventories and executable gates used by later migration slices; it
does not move facade production sources or claim any P6.1 work.

The branch started directly at the recorded implementation base. Pre-existing
unrelated worktree state was separated and preserved:

- modified `Build/Submodules/OscCore-NET9` submodule worktree;
- untracked `Build/Dependencies/vcpkg/`; and
- a concurrent modification to
  `docs/work/todo/avatar/humanoid-body-root-compensation-todo.md`, first
  observed during final reconciliation and not touched by P6.0.

P6.0 made two small corrective changes outside its reporting artifacts:

- `Samples/MonkeyBallVR/MonkeyBallVR.csproj` now directly references
  `XREngine.Runtime.Bootstrap`, the assembly that owns
  `VRGameStartupSettings<,>`. This closes a Phase 5 consumer-graph omission
  exposed by the baseline build.
- `ModelCacheAssetManagerIdentityTests` now authors a disabled meshlet override.
  `ModelCookSettings` enables meshlets by default, so the former `true` fixture
  did not differ from the default and could not test override fingerprinting.

No dependency or submodule version changed.

## Durable Baseline Artifacts

| Artifact | Rows | Purpose | Generator |
|---|---:|---|---|
| [Source ownership manifest](runtime-modularization-phase6-source-ownership.tsv) | 358 initial / 384 tracked | Stateful file/type disposition, final owners, migration status, and concrete destination paths, including migration adapters discovered after the initial inventory | `Tools/Reports/Generate-RuntimeModularizationPhase6SourceOwnership.ps1` |
| [Identity migration report](runtime-modularization-phase6-identity-migration.tsv) | 103 | Every removed facade-forward identity, its stable full name, current assembly/type identity, and migration policy | Captured from the final built facade metadata before deletion |
| [Consumer API baseline](runtime-modularization-phase6-consumer-api-baseline.tsv) | 7 | Built-metadata facade type/member references for every direct consumer | `Tools/Reports/Get-RuntimeModularizationPhase6FacadeApiUsage.ps1` |
| [Project graph baseline](runtime-modularization-phase6-project-graph-baseline.tsv) | 24 | Destination, consumer, and facade project edges and project cargo | `Tools/Reports/Get-RuntimeModularizationPhase6ProjectGraph.ps1` |
| [Publish layout baseline](runtime-modularization-phase6-publish-layout-baseline.tsv) | 697 | File-level Editor, Server, and VRClient publish manifests with size, category, and SHA-256 | `Tools/Reports/Get-RuntimeModularizationPhase6PublishLayout.ps1` |

The ignored validation root is
`Build/_AgentValidation/20260825-103925-runtime-modularization-p60/`. It holds
command logs, publish outputs, screenshots, and temporary evidence only; no
tracked behavior depends on it.

## Facade Inventory

### Sources And Types

`dotnet msbuild XRENGINE/XREngine.csproj -nologo -getItem:Compile` evaluates
exactly 358 compile inputs. All 358 are hand-authored facade sources; zero
evaluated inputs are under `obj`, a generated directory, `*.g.cs`, or
`*.generated.cs`. Stale `obj/.../AotFactoryRegistrations.g.cs` files may exist
locally, but they are not facade compile inputs.

The source manifest identifies 646 declaration occurrences and 547 distinct
source-level type identities. It contains 464 public declaration occurrences.
The built Debug facade metadata contains 808 defined types excluding
`<Module>`, of which 365 are public or nested-public. The metadata count is the
authoritative consumer-visible type count; the source count deliberately
retains partial and conditional declarations for file-by-file migration.

Every source row has an explicit final owner and starts in `Pending` state:

| Disposition | Rows |
|---|---:|
| Move | 283 |
| Split | 35 |
| Refactor | 34 |
| Delete | 6 |

Owner counts overlap for split rows: Runtime.Core 215, Data 50,
Runtime.Rendering 46, ModelingBridge 26, Editor 24, Bootstrap 19,
InputIntegration 11, Animation 10, and explicit removal 6. No owner contains
`miscellaneous`, `temporary`, or `unclassified`.

### Project, Package, Native, And Build Cargo

The facade has these 12 direct project references:

- `XREngine.Animation`, `XREngine.Audio`, `XREngine.Data`,
  `XREngine.Extensions`, `XREngine.Fbx`, and `XREngine.Input`;
- `XREngine.Runtime.Core` and `XREngine.Runtime.Rendering`; and
- `XREngine.Runtime.AnimationIntegration`,
  `XREngine.Runtime.AudioIntegration`,
  `XREngine.Runtime.InputIntegration`, and
  `XREngine.Runtime.ModelingBridge`.

It has 19 direct package includes:

- `AssimpNetter`, `JoltPhysicsSharp`, `LZMA-SDK`, `MagicPhysX`, `MemoryPack`,
  `Newtonsoft.Json`, `System.IO.Hashing`, and
  `System.Security.Cryptography.ProtectedData`;
- `Silk.NET.Core`, `Silk.NET.DirectStorage`,
  `Silk.NET.DirectStorage.Native`, `Silk.NET.Input`, `Silk.NET.Windowing`,
  `Silk.NET.Windowing.Common`, `Silk.NET.Windowing.Extensions`,
  `Silk.NET.Windowing.Glfw`, and `Silk.NET.Windowing.Sdl`; and
- `Vecc.YamlDotNet.Analyzers.StaticGenerator` and `YamlDotNet`.

It also updates `StirlingLabs.assimp.native.win-x64`. The two declared
native/content inputs are
`runtimes/win-x64/native/lib_coacd.dll` and conditional
`$(NvidiaRtxgiWinX64Dir)RestirGI.Native.dll`. `nis.license.txt` is the
facade-owned license payload. The custom targets are `EnsureCoACD` and
`CopyRestirNative`. The two unique friend assemblies are
`XREngine.UnitTests` and `XREngine.Runtime.Bootstrap`; UnitTests is declared
once in the project and once in `Properties/AssemblyInfo.cs`.

### Type Identity, Serialization, Reflection, And AOT

- The source facade declares 103 `TypeForwardedTo` attributes. Its built
  metadata contains 121 exported-type rows because nested forwarded types add
  metadata rows.
- The lower redirect system has 25 `XRTypeRedirect` declaration sites in 25
  destination files. The two conditional Rive implementations represent one
  logical redirected type. Five facade files call
  `XRTypeRedirectRegistry.RewriteTypeName` directly; two additional facade
  loaders reach the registry through `CookedAssetTypeReference`.
- The serializer inventory includes 22 `Core/Files/CookedBinary` sources, 19
  named serializer/YAML/converter sources under `Core/Engine`, five core
  snapshot sources, `Core/Serialization/XREngineJsonSerialization.cs`, and
  `Core/Files/XRAsset.MemoryPack.cs`. Every file has an explicit row in the
  ownership manifest rather than a directory-only disposition.
- A bounded reflection/AOT search finds 27 facade files and 226 call,
  annotation, or constraint occurrences involving `Type.GetType`, assembly
  type discovery, activation/custom attributes, trimming annotations, or
  dynamic-code annotations. These are migration roots, not a claim that every
  match needs the same implementation.
- Bootstrap's current generated AOT source is 195 lines and registers 38
  transform factories, 13 post-process backing factories, and five camera
  parameter factories. Its input item scans eight roots: Bootstrap, the facade,
  Runtime.Core, Runtime.Rendering, and all four integration adapters.
- The retention paths that must disappear are Bootstrap's
  `..\XRENGINE\**\*.cs` AOT input and facade project reference,
  `Tools/Generate-AotFactoryRegistrations.ps1`'s facade scan,
  Editor `CodeManager`'s `XREngine.dll` entry, Editor project initialization's
  facade project path, and both solution entries.

## Consumer And Dependency Inventory

Seven projects directly reference the facade. Built metadata gives the
source/API baseline below; Benchmarks carries a direct project edge but its
current assembly emits no facade TypeRef or MemberRef.

| Consumer | Facade assembly reference | Type refs | Member refs |
|---|---:|---:|---:|
| Runtime.Bootstrap | yes | 58 | 300 |
| Editor | yes | 157 | 1,149 |
| Server | yes | 17 | 37 |
| VRClient | yes | 4 | 17 |
| UnitTests | yes | 212 | 1,091 |
| Benchmarks | no emitted use | 0 | 0 |
| Samples/MonkeyBallVR | yes | 15 | 116 |

Representative source-file use was also counted so migration planning is not
limited to project edges:

| Consumer | `Engine` | `AssetManager` | `XRWorldInstance` | startup settings | physics components | prefab APIs | game modes |
|---|---:|---:|---:|---:|---:|---:|---:|
| Runtime.Bootstrap | 19 | 1 | 5 | 4 | 1 | 0 | 3 |
| Editor | 108 | 19 | 30 | 10 | 7 | 13 | 9 |
| Server | 2 | 0 | 1 | 1 | 0 | 0 | 0 |
| VRClient | 1 | 0 | 0 | 1 | 0 | 0 | 0 |
| UnitTests | 52 | 29 | 16 | 11 | 36 | 14 | 3 |
| Benchmarks | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| Samples/MonkeyBallVR | 0 | 0 | 0 | 0 | 0 | 0 | 0 |

These are source-file counts from bounded symbol searches, not API call counts.
The metadata TSV is authoritative for actual emitted facade use and contains
the exact referenced type/member names. The project-graph TSV records the
approved destination graph before any P6.1 move and is enforced by the Phase 6
boundary tests.

## Serialized Identity And Migration Policy

The exact legacy assembly-qualified corpus is 26 source lines across four test
files:

- `XREngine.UnitTests/Prefabs/PrefabModelSerializationTests.cs`;
- `RuntimeModularizationPhase3RenderingTests.cs`;
- `RuntimeModularizationPhase4SerializationCompatibilityTests.cs`; and
- `RuntimeModularizationPhase5SerializationCompatibilityTests.cs`.

No checked-in production asset, sample asset, generated-settings source, JSONC
schema, or server asset contains an exact `, XREngine` assembly-qualified
identity. Sample assets do contain current namespace-qualified type names; they
are part of the repository migration/load validation corpus. Current build and
publish outputs intentionally contain `XREngine.dll` at this baseline.

Docs containing facade paths or assembly names were classified as design,
historical validation, investigation, or current CoACD ownership documentation;
they are not runtime load inputs. The CoACD architecture guide must be updated
when that cargo moves. Editor's live assembly list and project-template path are
load/build retention roots, not documentation.

The supported repository migration path is fixed as follows:

1. Migrate checked-in assets, scenes, prefabs, projects, cooked payloads,
   settings, and generated metadata to current owner identities where safe.
2. Keep known namespace/type redirects in the lower
   `XRTypeRedirectRegistry` and invoke rewriting before CLR assembly lookup in
   YAML, cooked, MemoryPack, prefab, project, and snapshot paths.
3. Provide an idempotent repository migration command for content that cannot
   be safely rewritten while loading.
4. Make an unknown legacy identity fail with the original identity, asset path,
   expected owner assembly when known, and migration guidance. Never map it
   silently to a different public type.
5. Replace raw `Type.GetType("..., XREngine")` expectations with supported
   loader/redirect tests before deleting the corresponding forward.

The intentional external pre-v1 break is also fixed: third-party binaries
compiled against `XREngine.dll` must be rebuilt against explicit runtime
assemblies. External content outside the repository corpus must run the
published migration command or use a known supported redirect. Unknown legacy
identities fail diagnostically. Phase 6 will not ship an empty compatibility
assembly solely for external binary or content compatibility.

## Build Baseline

`dotnet build XRENGINE.slnx --no-restore -m:1 -nr:false -v:minimal` passed in
1 minute 37 seconds with zero warnings and zero errors. Every destination and
every direct consumer also received an individual Debug build. The 23-project
destination/consumer set passed with zero warnings and zero errors after the
MonkeyBall Bootstrap reference correction. Per-project logs are under
`logs/project-builds/` in the validation root. The final closeout rerun after
all P6.0 C# and project changes also passed with zero warnings and zero errors
in 33.65 seconds.

The MonkeyBall sequence established three distinct facts:

- its first no-restore build correctly failed `NETSDK1004` because it had no
  assets file; an explicit restore passed;
- its `Development Debug` configuration currently propagates an unquoted
  `Configuration=Development Debug;Platform=x64` into the native FastGltf
  MSBuild command and fails `MSB1008`/`MSB3073`; this pre-existing profile issue
  is recorded but is not hidden as a modularization failure; and
- its normal Debug build exposed the missing Bootstrap edge (`CS0246`) and then
  passed cleanly after that edge was added.

## Targeted Test Baseline

The following focused Debug groups passed. Groups intentionally overlap where
one contract covers more than one subsystem.

| Area | Result | Representative fixtures |
|---|---:|---|
| Serialization | 88/88 | Phase 4/5 compatibility, XRAsset, cooked binary, YAML, prefab, animation clip |
| Assets | 47/47 | cache, packing, published/cooked, cooking, model-cache identity, graph utility |
| Physics | 42/42 | backend/gameplay boundaries, scene serialization, chain component and world lifecycle |
| Networking | 56/56 | contracts, timing, animation networking, replication policy |
| World | 58/58 | Phase 4 world compute, scene-node lifecycle/prefab, settings, execution topology |
| Input/gameplay | 10/10 | MonkeyBall input/gameplay, game-mode UI, settings persistence |
| Rendering | 31/31 | Phase 3 rendering, rendering host services/capabilities, render-object service |
| Import | 42/42 | Phase 4 ownership, FBX phases, glTF corpus/document, model transform, Unity scene |
| AOT | 6/6 | factory registrations and JSON contracts |
| Project graph | 35/35 | Phase 4/5 dependency boundaries, Phase 6 stateful boundaries, Editor dependency generation |

The new `RuntimeModularizationPhase6BoundaryTests` fixture passes 6/6. It
enforces all 358 stateful source rows, all seven stateful consumer rows, the
facade-free final destination graph, the exact legacy identity corpus,
monotonic facade-cargo shrinkage, and the final removal gate. A row marked
`Pending` requires its source/reference to exist; `Migrated` requires the old
source/reference to be absent and real destinations to exist; `Deleted`
requires an explicit `Removed` disposition. Deleting a source directory cannot
make the tests pass accidentally.

An extra, non-gating run of `WindowOwnershipContractTests` passed 24 and failed
six stale source-contract assertions that still name older Vulkan files or
source fragments. It is outside the P6.0 input/gameplay matrix and remains
separate renderer test debt; P6.0 did not weaken or rewrite it.

## Runtime Startup Baseline

### Editor OpenGL

Named session `p60-opengl-baseline` started the Unit Testing World with MCP,
reported OpenGL 4.6.0 on the NVIDIA GeForce RTX 4070 Laptop GPU (driver
581.57), answered MCP, and stopped through the session manager. The inspected
screenshot is
`mcp-captures/opengl/Screenshot_20260825_105948_913_99d9f58e2d6d4377a08e1b528f0df04c.png`
under the validation root. Editor UI, hierarchy, and statistics rendered; the
central scene viewport was black, so this is a startup/composition baseline and
not a claim of scene-image correctness. The engine returned normally and logged
`ProcessExit`.

### Editor Vulkan

Named session `p60-vulkan-baseline` selected Vulkan, initialized dynamic
rendering and VMA on the same NVIDIA GPU, answered MCP, and produced first-frame
and pipeline activity without a validation VUID, device loss, or steady-state
lifetime error. Screenshot capture failed explicitly because Vulkan could not
resolve a live transfer-readable color image; no CPU or OS-window fallback was
used. The session manager stopped only the named session. Shutdown logged
deferred image-view destruction warnings and did not record the same normal
`Engine.Run` return marker as OpenGL, so the limitation is retained rather than
reported as a clean visual capture.

The isolated session roots are:

- `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260825-105646-p60-opengl-baseline/`
- `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260825-110106-p60-vulkan-baseline/`

### Server And VRClient

The Debug headless Server entered play, passed its scheduler smoke, bound UDP
port 55060, and blocked without local rendering. Ctrl+C stopped the owned PTY
and unblocked presentationless shutdown. Its engine log is
`Build/Logs/Debug_net10.0-windows10.0.26100.0/windows_x64/xrengine_2026-08-25_11-04-43_pid57340/`.

The Debug VRClient launched with deliberately nonexistent peer name
`XREngine_P60_NoSuchPeer_7E91A6`, selected the VR-low-latency profile, warned
that the game process could not be found, and exited zero before XR
initialization. Its engine log is
`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-08-25_11-05-40_pid24716/`.

Physical OpenXR/OpenVR headset execution, supported-hardware Streamline/DLSS
feature execution, and extended performance/resize soak remain explicit
external manual acceptance lanes. No physical-device result is claimed.

## Publish Baseline

Initial no-restore Release `win-x64` publishes correctly failed `NETSDK1047`
because the RID target was not restored. After explicit `dotnet restore -r
win-x64`, framework-dependent, non-single-file Release publishes passed for all
three applications:

| Application | Files | Managed | Native libraries | Native hosts | Symbols | Config/manifest | Content |
|---|---:|---:|---:|---:|---:|---:|---:|
| Editor | 238 | 163 | 56 | 2 | 8 | 4 | 5 |
| Server | 235 | 163 | 55 | 2 | 6 | 4 | 5 |
| VRClient | 224 | 152 | 55 | 2 | 6 | 4 | 5 |

Outputs are under `temp-build/publish/{editor,server,vrclient}` in the
validation root. The checked-in 697-row manifest records every relative path,
category, byte size, and SHA-256. Each baseline output contains exactly one
`XREngine.dll`; P6.6 must prove that entry disappears while all required cargo
remains singly owned.

## P6.0 Closeout

P6.0 is complete. The accepted removal decision, repository/external
compatibility policy, full ownership/consumer/dependency/cargo inventories,
baseline builds, focused tests, startup behavior, publish layouts, and
slice-aware guardrails are now durable and reproducible. The next executable
slice is P6.1, which must use the manifest to move the lowest serialization and
asset primitives without broadening Data into a catch-all assembly.

## P6.1 Serialization And Asset Foundations

P6.1 is complete. The original 74 `Core/Files` and `Core/Engine`
asset/serialization sources have explicit owners. Sixty-seven are migrated;
the remaining seven are deliberately narrow facade adapters retained for the
prefab/import-policy work in P6.5. P6.1 created three additional temporary
adapters, so the current manifest has 77 sources in this slice: 67 `Migrated`
and ten `Pending`. The generator now inventories checked-in and untracked
non-ignored facade sources, which prevents a newly created migration adapter
from bypassing the stateful boundary test.

### Final Ownership Implemented

- Data owns format-neutral asset values and contracts, metadata, type-name
  redirects, YAML infrastructure, cooked-binary modules, MemoryPack support,
  published-cooked registries, archive packing/compression, hashing, and the
  protected-secret service contract.
- Animation owns animation clip, motion, property, state-machine, blend-tree,
  Unity-animation YAML, cooked-binary codecs, and its published registrations.
- Runtime.Core owns `AssetManager`, loading/publication/saving, project-relative
  resolution, remote loading, generic cache coordination, DirectStorage IO,
  `XRProject`, runtime YAML services, and object-lifecycle services.
- Runtime.Rendering owns render-asset YAML/cooked registrations, polymorphic
  render fallbacks, and the authoritative texture streaming cache codec.
- ModelingBridge owns model-cache identity/path policy, cache codecs, import
  option snapshots, and explicit modeling registration. The facade retains only
  prefab adapters scheduled for P6.5.
- Bootstrap composes owner registrations through `RuntimeAssetBootstrap` and
  disposes them in reverse order. Focused rendering test hosts no longer retain
  hidden process-global asset leases.
- Editor owns automatic third-party file watching and the DPAPI secret service;
  protected-value failures outside an installed authoring host identify the
  missing owner/service explicitly.

All owner installation paths use reversible leases. `RegistrationLeaseGroup`
makes multi-registration installation transactional: a later collision rolls
back earlier leases, disposal is reverse-ordered and idempotent, and disposal
failures are aggregated. Registries reject duplicate authorities instead of
silently replacing them. Missing polymorphic, serializer, cache, and protected
secret registrations report the asset path where available and name the owner
or registration that must be installed.

### Runtime Compatibility And Package Ownership

The YAML, JSON, cooked-binary, MemoryPack, scene/prefab, project, snapshot, and
legacy type-resolution paths remain compatible with the repository corpus.
The live cook initially exposed two ambiguous source-extension authorities:
Data and Animation both claimed `.anim`, and Rendering shader assets and model
imports both claimed `.mesh`. Animation is now the sole `.anim` runtime owner;
Rendering is the sole `.mesh` owner, while model inputs continue through their
actual model extensions such as `.mesh.xml`.

DirectStorage and its native cargo now belong only to Data and Runtime.Core.
`System.IO.Hashing` is direct in Data and its remaining real consumers.
`System.Security.Cryptography.ProtectedData` moved from the facade to Editor.
The regenerated [dependency inventory](../../../DEPENDENCIES.md) records these
owners without dependency-version or license changes.

The AOT generator scans Bootstrap plus explicit final owner projects: Data,
Animation, Runtime.Core, Runtime.Rendering, the integration projects, and
ModelingBridge. It no longer scans the facade. `BillboardTransform` moved to
Runtime.Rendering in P6.6, and P6.7 removed its temporary Bootstrap
registration together with the facade forwards; there is no broad facade
source root or compatibility factory.

No new lower serialization assembly was required. Data remains the lowest
serialization owner, and the reference dependency graph therefore needs no
design amendment.

### Validation

Ignored evidence is under
`Build/_AgentValidation/20260825-130100-runtime-modularization-p61/`.

- The canonical CommonAssets cook processed and packed 899/899 inputs, exited
  zero, and produced `temp-build/GameContent.pak` at 4,378,570,602 bytes
  (4,175 MiB).
- Data, Animation, Runtime.Core, Runtime.Rendering, ModelingBridge, Bootstrap,
  and UnitTests each built in Debug with zero warnings and zero errors after
  the final P6.1 source changes.
- The Phase 6 boundary and updated Phase 4 ownership fixtures passed 8/8.
- The focused serialization/asset matrix passed 159/159. It covers Phase 4/5
  compatibility, XRAsset and prefab round trips, cooked binary, YAML/JSON,
  animation, snapshots, caches and archive packing, model-cache identity, AOT,
  and missing-registration behavior.
- The transactional registration rollback fixture passed 1/1. The final
  combined fresh-binary regression pass therefore passed 168/168.
- Dependency/license generation completed. Only dependency ownership rows
  changed; fetched license texts remained unchanged.

## P6.2 Core Engine, Physics, Networking, And World Ownership

P6.2 is complete. The source ledger now contains 364 durable rows: 219 are
`Migrated`, two deleted facade globals are `Deleted`, and 143 compatibility or
later-phase sources remain `Pending`. The generator unions the checked-in
baseline with current sources, so staged moves cannot accidentally erase
historical ownership rows. Every completed destination is validated before the
manifest is written.

### Engine Member Classification

| Classification | P6.2 result |
|---|---|
| Runtime.Core service | Lifecycle state, shutdown signals, timing, worker scheduling, main-thread dispatch, memory policy, play-mode state, runtime networking, physics services, static-collider behavior, and convex-hull inputs have focused lower owners. |
| Bootstrap composition | Runtime adapter installation, networking host integration, and join-handoff endpoint composition install reversible lower service leases. |
| Application policy | Effective editor/application overrides, window pumping, local input, and launch-profile decisions remain facade/host adapters for P6.4 and P6.6; lower runtime effects no longer depend on them. |
| Diagnostics tooling | Interactive profiler capture, sender, and main-thread log presentation remain facade/editor work scheduled for the application migration. |
| Deletion | `GlobalUsings.Physics.cs` and `GlobalWorldTypeAliases.cs` were removed; callers use explicit destination namespaces and canonical types. |

The legacy static `Engine` members that applications still compile against are
compatibility forwarders, not state owners. P6.2-scope consumers use the focused
Core services; final facade/API removal remains intentionally assigned to P6.6.
No new broad friend assembly was added.

### Runtime, Networking, And Physics Ownership

- Runtime.Core now owns `RuntimeLifecycleState`, `RuntimePlayModeController`,
  `RuntimeTimingServices`, `RuntimeWorkScheduler`, `RuntimeThreadDispatcher`,
  and `RuntimeMemoryPolicy`. Core-safe runtime-setting effects are applied by
  those owners; editor preferences and application override selection stay out
  of Core.
- Networking managers, session resolution, world identity, remote-job
  transport, and the join-handoff contract moved to Runtime.Core. Bootstrap
  supplies host operations without adding InputIntegration or application
  dependencies to the lower contracts.
- Non-visual physics components and the CPU/GPU-chain simulation state moved
  from the facade to Runtime.Core. GPU soft-body and joint components whose
  behavior consumes renderer transforms/debug publication moved to
  Runtime.Rendering. GPU-chain dispatch crosses
  `IRuntimePhysicsChainRenderingBridge`; Core contains snapshots and dispatch
  inputs but has no Rendering project reference.
- JoltPhysicsSharp, MagicPhysX, System.IO.Hashing, CoACD cargo, and the CoACD
  build target moved off the facade to Runtime.Core. Dependency versions and
  license texts did not change; the regenerated inventory records only the new
  project/native-cargo owners.
- Timing, networking, physics, static-collider, and physics-chain rendering
  service installation is lease-based and test-resettable. Snapshot restore
  also resolves engine-relative assets through the migrated AssetManager path
  without probing MemoryPack or restoring native physics actors.

### Validation

Ignored evidence is under
`Build/_AgentValidation/20260825-191909-runtime-modularization-p62/`.

- Runtime.Core, Runtime.Rendering, Bootstrap, the facade, Server, Editor, and
  UnitTests built in Debug with zero warnings and zero errors. Runtime.Core's
  evaluated project references remain exactly Data and Extensions.
- The headless Server was started twice through the migrated lifecycle,
  networking, asset, and physics initialization path (ports 55062 and 55063),
  remained healthy for the observation interval, and each owned process was
  stopped explicitly.
- The final focused lifecycle, scheduler, play-mode, networking, physics,
  collision/query, cooking, world teardown, serialization, GPU-dispatch, and
  Phase 6 boundary matrix passed 291/291.
- Dependency/license generation completed. JoltPhysicsSharp and MagicPhysX are
  now solely attributed to Runtime.Core, System.IO.Hashing includes its Core
  owner, and CoACD native cargo is emitted by Runtime.Core.

The next executable slice is P6.3: decompose `XRWorldInstance` into focused
Core, Rendering, InputIntegration, Bootstrap, and Editor owners without moving
the facade aggregate unchanged.

## P6.3 XRWorldInstance Decomposition And Rendering Composition

P6.3 is complete. The cross-layer `XRWorldInstance` aggregate and its two
partial files were removed. The source ledger now contains 365 durable rows:
222 are `Migrated`, two are `Deleted`, and 141 remain `Pending` for later
Phase 6 slices. The three former `XRWorldInstance` rows name their concrete
Core, Rendering, InputIntegration, Bootstrap, and Editor destinations, and the
stateful Phase 6 boundary fixture passes 6/6.

### Focused Ownership Implemented

- Runtime.Core's `RuntimeWorld` is the sole non-visual identity stored on scene
  nodes. It owns target-world identity, scene/root membership, lifecycle,
  ticks, transform invalidation, physics state/queries, and minimum-Y reset.
- Runtime.Rendering's `RuntimeWorldRenderer` owns visual-scene publication,
  lights, render registration, render queries, physics-debug drawing, and CPU
  and GPU picking. It attaches through typed Core capability leases and the
  explicit `RuntimeRenderWorldRegistry` rather than becoming a second world
  identity.
- Runtime.InputIntegration owns controlled-pawn and input refresh behavior.
  Bootstrap's `RuntimeWorldHost` composes the Core and Rendering objects,
  initializes rendering and physics before gameplay activation, and tears down
  backends between Core node deactivation and persistent-root reactivation.
- Editor's `EditorWorldIntegration` owns the hidden `__EditorScene__`, editor
  root routing, render queries, and the policy excluding editor-only roots from
  gameplay begin/end callbacks. It is eagerly composed before initial scene
  loading and detaches itself during Core disposal.
- `RuntimeWorldRegistry`, `EngineRuntimeWorldHostServices`, and
  `EditorWorldIntegrationRegistry` provide explicit multi-world lifetime,
  retargeting, reset, and teardown. Bootstrap uses provisional publication and
  guarded two-phase creation so re-entrant activation cannot create a duplicate
  host. GPU mesh-BVH picking preferences are propagated to every attached render
  world and are rechecked at dispatch time.

The migration preserved initial-scene render registration, settings/gravity
binding, light-cache rebuilds, edit/play transitions, persistent editor roots,
physics initialization and teardown, retarget identity, and renderer disposal
ordering. Production and sample C# sources contain no `XRWorldInstance`
reference. The detailed implementation and live evidence are recorded in the
[P6.3 investigation](../../investigations/runtime/xrworldinstance-decomposition-p63-2026-08-25.md).

### Validation

Ignored evidence is under
`Build/_AgentValidation/20260825-205443-runtime-modularization-p63/`.

- Runtime.Core, Runtime.Rendering, Bootstrap, Editor, UnitTests, Server, and
  VRClient built in Debug with zero warnings and zero errors. Runtime.Core's
  project-reference boundary remains Data plus Extensions only.
- The focused world/lifecycle/rendering matrix passed 51/51. It covers canonical
  identity, initial render composition, multi-world retarget/reset, capability
  teardown, scene roots, editor-only lifecycle, physics coordination, render
  registration, GPU picking, settings, snapshots, and migrated source contracts.
- The complete OpenXR timing/pipeline contract fixture passed 57/57 after its
  moved GPU-picking source reference and one stale GTAO partial-file assertion
  were corrected.
- The regenerated Phase 6 ownership manifest passed its stateful boundary gate
  6/6. The deleted facade paths cannot silently return or lose their destination
  records.
- The Phase 4/5 dependency and physics-backend boundary set passed 35/35 after
  removing a verified-empty legacy OpenGL directory tree that otherwise looked
  like a retained backend implementation owner.
- Named OpenGL session `p63-world-opengl` produced two visually distinct,
  correct editor views after camera cuts from `(0, 2, 4)` and `(6, 4, 0)`.
  The Mitsuki hierarchy/model, editor UI, and skybox were visible; the captures
  have different hashes, and the inspected bootstrap/render/OpenGL logs contain
  no fatal or unhandled error. Only that named session was stopped.
- Named Vulkan session `p63-world-vulkan` was run and inspected from multiple
  camera/focus changes. Runtime evidence proved the P6.3 publication path: 57
  active commands, 54 opaque deferred meshes, 57 resident draws/instances, and
  55 CPU-visible draws were attached to the canonical world and active camera.
  Final presentation nevertheless stayed on the same red/blue image because
  frame 10 failed in `PresentNow` during pipeline compilation before acquire,
  record, or submit. There was no VUID or device loss. The same result reproduced
  after a warm-cache `-NoBuild` restart, and only the named session was stopped.
  This is the independently paused renderer work tracked by
  [Vulkan PresentNow frame readiness](../../todo/rendering/vulkan-present-now-frame-readiness-todo.md),
  not a missing P6.3 world/render registration path; no fallback or unrelated
  renderer rewrite was introduced here.

An extra non-gating run of `VulkanDeferredProbeGiFixesTests` passed 15/36 and
failed 21 stale or absent source-contract expectations in the still-changing
Vulkan frame-op, descriptor, and device-fault work. P6.3 changed only the moved
picking-source reference in that fixture; its focused picking contracts pass.
The broader Vulkan contract debt remains with the renderer investigation above.

The next executable slice is P6.4: move gameplay, input, startup, window, and
settings composition while keeping the headless Server profile free of local
input, rendering-window, audio, and VR services.

## P6.5B Imported-Asset Semantic Normalization

P6.5B is complete. Runtime publication now contains only converted native
behavior; serialized source evidence and intentional loss remain at the editor
import boundary.

- Removed the placeholder runtime animator metadata and avatar animation-layer
  components. `SerializedPrefabImportManifest` schema 2 now owns animator,
  controller, layer, mask, expression-menu, and parameter references. Its
  avatar graph is explicitly a staging representation for compilation into one
  native `AnimStateMachineComponent`, not a second runtime animation model.
- Animation manifest schema 3 accepts callbacks only through an explicit native
  event allowlist and typed receiver dispatch. The initial allowlist is empty,
  so arbitrary source callback names are recorded as non-blocking intentional
  conversion loss rather than invoked by reflection.
- Opaque source behavior and animation payloads no longer retain raw YAML.
  Import reports keep bounded field names, byte counts, discarded-item counts,
  and SHA-256 digests sufficient for diagnostics and cache evidence.
- Native presentation, gaze, eyelid, jaw, speech-pose, physics-chain, collider,
  and weighted-constraint behavior remains. Source grabbing, posing, solver,
  collision policy, freeze/rebake, and authority flags are diagnosed as loss;
  eventual local and replicated manipulation will enter through one native
  physics-chain interaction/authority contract.

Ignored evidence is under
`Build/_AgentValidation/20260827-235500-phase65b-import-loss/`.

- ModelAssetPipeline, Animation, and AnimationIntegration built with zero
  warnings and zero errors.
- An isolated compile of the Editor importer sources completed with zero
  warnings and zero errors. A live synthetic avatar-prefab conversion published
  `PhysicsChainComponent` and `AvatarPresentationComponent`, one import-side
  avatar graph/layer record, bounded unsupported-behavior evidence, and the
  expected loss diagnostics.
- The full Editor graph reached unrelated concurrent Bootstrap work and failed
  because `RuntimeAssetBootstrap` was absent from
  `Engine.RuntimeRenderingHostServices`. P6.5B did not alter Bootstrap or the
  concurrent renderer work.
- No unit tests were modified or run. Repository policy requires explicit user
  clearance after the live integration is functionally sound; two existing
  fixtures still reference the deliberately removed placeholder component and
  raw-YAML property and must be migrated in that later test pass.

## P6.6 Application, Sample, Test, And Tooling Migration

P6.6 is complete. Bootstrap, Editor, Server, VRClient, UnitTests, Benchmarks,
and `Samples/MonkeyBallVR` now reference their real module owners directly.
Metadata inspection of all seven built binaries reports zero `XREngine`
assembly references and zero facade type or member references. Editor assembly
discovery and generated game projects no longer name `XREngine.dll` or
`XRENGINE/XREngine.csproj`, and launcher packaging explicitly rejects stale
facade cargo.

All facade production sources were physically moved to Data, Runtime.Core,
Runtime.Rendering, Runtime.Bootstrap, or Editor. The 384-row ownership ledger
now records 378 migrated rows, two deleted rows, and only four pending P6.7
assembly metadata/type-forward files under `XRENGINE/Properties`. Bootstrap is
the application composition owner. Its headless profile uses a specialized
adapter lease that does not instantiate local window, input, audio, or VR
services, and the Server selects the `None` renderer build profile. The audited
headless publish contains no Editor, OpenGL/Vulkan backend, OpenVR, SDL/GLFW,
Vulkan, or VMA cargo.

Removing facade load side effects exposed and closed three real generated-game
dependencies: `.xrproj` extension registration now deserializes directly
without re-entering the generic loader, Data explicitly contributes its YAML
converters through a lease, and generated launchers install modular asset
services before reading startup configuration. Framework-dependent launcher
assembly identity now matches the requested executable name, so a renamed
`Game.exe` locates its managed entry assembly correctly.

Ignored evidence is under
`Build/_AgentValidation/20260828-193000-runtime-p66/`.

- All seven consumers built with zero warnings and zero errors. UnitTests also
  rebuilt after final launcher/serialization changes with zero warnings and
  zero errors.
- OpenGL-only and Vulkan-only Bootstrap builds passed, and generated AOT
  registration source contained no `XREngine.dll`. This validates static/AOT
  roots without claiming that a full NativeAOT publish was performed.
- Editor, Server, VRClient, and the generated MonkeyBall launcher passed bounded
  startup smokes. The final MonkeyBall build completed with zero warnings and
  zero errors, `Game.exe` remained running for eight seconds, and its package
  contained no facade assembly.
- The final Phase 6 boundary, launcher-generation, and packaging set passed
  14/14. The regenerated source/API manifests pass their stateful gates.
- A broader focused run passed 27/29. The two failures are stale source-contract
  expectations in concurrent renderer work:
  `BackendGeneration_IsInstalledBeforeApiWrappersAreCreated` and
  `ReplacementAcceptance_RequiresBackendValidatedFrameContent`. Neither tests
  a P6.6 consumer/facade boundary, and P6.6 did not change the concurrent
  Vulkan implementation to satisfy obsolete strings.

## P6.7 Facade Deletion, Identity Migration, And Cargo Ownership

P6.7 is complete. Option A is now the physical repository state:
`XRENGINE/XREngine.csproj`, all 103 type forwards, the facade cargo, and the
entire `XRENGINE` production directory are gone. Both solution formats contain
only the modular projects. The regenerated project graph contains 24 rows and
no facade row. Metadata inspection of Bootstrap, Editor, Server, VRClient,
UnitTests, Benchmarks, and MonkeyBall reports zero facade assembly, type, and
member references.

The durable identity migration report records all 103 forwarded identities,
their stable full names, their current assemblies, and the supported migration
policy. Repository-owned serialized type references now compare stable full
names without losing generic type syntax. The AOT metadata store first uses
current CLR resolution, then stable full-name matching over loaded assemblies
and the explicit published cooked-asset registry. This keeps YAML, JSON,
cooked, MemoryPack, prefab, scene, project, and generated-settings paths
independent of `XREngine.dll` without introducing a permanent facade-name map.
Direct external `Type.GetType(..., XREngine)` calls intentionally fail and must
migrate to current assembly-qualified names; that is the deliberate pre-v1
breaking boundary.

Package and native cargo now follow their direct owners. Runtime.Core owns
`lib_coacd.dll` and its acquisition/build path. Runtime.Rendering owns optional
RestirGI publication. NIS/Streamline licenses and shared NVIDIA SDK cargo use
`ThirdParty/NVIDIA/SDK/win-x64`. The dependency generator was rerun from
`Tools/Reports/Generate-Dependencies.ps1`; the resulting inventory has no
facade package or binary row. The temporary facade-named serialization
registrations and the `System.Remapper` compatibility shim were removed. The
remaining `Engine` partials formerly staged under Bootstrap `Compatibility`
folders were reclassified under final Bootstrap composition owners.

Validation on 2026-08-27:

- the 384-row ownership ledger reports 378 migrated and six deleted rows, with
  no pending entry;
- the 103-row identity report has 103 unique legacy identities and no current
  `XREngine` assembly owner;
- Data, Runtime.Core, Bootstrap, and UnitTests independent builds passed with
  zero warnings and zero errors;
- the full Bootstrap dependency graph, including both renderer leaves and all
  runtime adapters it composes, passed with zero warnings and zero errors;
- the facade identity and modular-boundary set passed 51/51;
- the broader asset, serialization, prefab, project, settings, and generated
  launcher set passed 150/150; and
- current product/tool outputs contain no facade binary. Fifteen exact stale
  facade binaries/symbols from ignored pre-migration output folders were
  deleted; historical validation evidence remains disposable and is not an
  active build or package input.

The only remaining exact `XREngine.dll` source reference outside historical
evidence and negative tests is the generated-launcher packaging rejection in
`XREngine.Editor/ProjectBuilder.cs`; it prevents copying the deleted facade and
does not probe, load, or require it. Legitimate product, namespace, solution,
and modular assembly names beginning with `XREngine` remain unchanged. The
reference architecture now records the implemented end state and the one
deliberate aggregate: ModelAssetPipeline continues to compose its tightly
coupled format-to-runtime publication pipeline.

## P6.8 Final Validation And Closeout

P6.8 is complete. It closed five issues exposed only by the exhaustive matrix:

- Bootstrap build artifacts are isolated by `All`, `OpenGL`, `Vulkan`, and
  `None` renderer variants, so differently composed builds no longer share
  generated source, NuGet state, IL, or NativeAOT inputs.
- `RuntimeAssetBootstrap` is a shared reference-counted installation. Nested
  application and test composition roots can lease the same cooked-asset
  registrations without duplicate registration or premature teardown.
- The headless application profile installs asset services plus the headless
  adapter set, while Server publish removes concrete renderer leaves and native
  local window, input, audio, VR, FFmpeg, and shared NVIDIA cargo. Managed
  adapter/wrapper assemblies that Bootstrap or Runtime.Rendering references as
  metadata remain present; they are not installed as local headless services.
- Four obsolete exact `InternalsVisibleTo("XREngine")` declarations and stale
  UnitTests reads of deleted facade source paths were removed or retargeted to
  their current owners.
- Generated launcher project references propagate
  `XREngineRendererBackends` into Bootstrap. Non-AOT selected-backend builds
  therefore use the same isolated dependency graph/output variant as their
  launcher instead of silently compiling and packaging `renderer-All`.

The closeout evidence root is
`Build/_AgentValidation/20260827-215638-runtime-p68/`. Durable reports are the
24-project graph, seven-consumer metadata audit, 384-row ownership ledger, and
890-row publish manifest under `docs/work/progress/runtime/`.

### Build, Regression, And Runtime Evidence

- The independent Data/lower-library/Runtime.Core/rendering/backend/adapter/
  Bootstrap/application/test/benchmark/sample matrix and a final serialized
  `dotnet build XRENGINE.slnx --no-restore` completed with zero warnings and
  zero errors after facade deletion. After unrelated concurrent Vulkan work
  entered the working tree, fresh Bootstrap `All`, `OpenGL`, and `None` variant
  builds and the UnitTests build again completed with zero warnings/errors by
  consuming the last clean dependency outputs. The concurrent Vulkan branch
  currently has compile errors in its new
  `VulkanAdvancedSceneResourceRuntime` work; no Phase 6 file is implicated, so
  this record does not claim the entire subsequently modified tree is green.
- The final profile plus Phase 3/4/5/6 boundary/identity/serialization set
  passed 79/79. Publish/launcher coverage passed 16/16. Repeated collectible
  backend registration/unload coverage passed 13/13. The prior broad subsystem
  run completed 4,625 tests: 4,077 passed, 541 failed, and seven skipped. Its
  failures are the existing animation, cooked-schema, renderer/backend, and
  source-contract backlog, not facade-removal regressions.
- After the launcher backend propagation fix, Editor Release and UnitTests
  Debug rebuilt with zero warnings/errors and the exact generated-project
  contract passed 1/1.
- The fresh exhaustive UnitTests run after the shared asset-lease fix completed
  5,255 console-reported tests in 20 minutes 23 seconds: 4,711 passed, 537
  failed, and seven skipped. The console log and TRX are
  `logs/p68-full-after-shared-assets.log` and
  `logs/p68-full-after-shared-assets.trx` under the evidence root. Focused
  Phase 6 and application-profile tests are green. That run exposed one final
  deleted rendering-root fixture and several silently vacuous source-contract
  paths; after retargeting them, UnitTests rebuilt with zero warnings/errors and
  the Phase 3/4/5/6, application-profile, and naming set passed 83/83. The
  retargeted shadow/forward source contracts now execute instead of skipping:
  78 passed and 16 failed, with zero skipped. Those 16 assert current
  renderer/shadow behavior and are part of the existing renderer contract
  drift, not facade identity, ownership, or application composition.
- Named isolated Editor sessions reached MCP readiness on canonical OpenGL and
  Vulkan Unit Testing World paths. Five changing captures under
  `mcp-captures/` prove live rendering rather than a stale readback. Vulkan
  emitted one recovered texture-backend initialization error and showed visual
  artifacts attributable to the concurrent renderer work; session teardown
  was owned and bounded.
- Framework-dependent Release publishes contain 355 Editor files, 210 Server
  files, and 325 VRClient files. None contains `XREngine.dll`. The final pruned
  Server package reached world/network startup and bound UDP ports 5000 and
  47777 before its owned process was stopped. The final published VRClient
  no-peer path exited with code 0. A physical XR headset was unavailable, so
  headset rendering, tracking, input, and local/remote avatar interaction are
  explicitly not claimed.
- The NativeAOT MonkeyBall launcher published and ran its live scripted smoke:
  36,927 metadata types, 12 registered runtime asset types, two content assets,
  484 common assets, 300 update ticks, and 399 physics steps. Renderer input
  hashes matched the just-built rendering assemblies. Strict trimming remains
  release debt: 913 IL2xxx/IL3xxx warnings (424 cooked-binary, 268 general
  first-party reflection/dynamic-code, 89 third-party/runtime, 88 editor/dev,
  and 44 first-party runtime). The warnings are recorded rather than treated
  as a successful zero-warning NativeAOT publish.

### Final Audits And External Lanes

- Metadata reports show zero facade assembly, type, or member references in all
  seven former consumers. Exact build-input and friend-assembly searches find
  no active facade dependency; remaining `XREngine.dll` strings are the
  generated-launcher rejection guard, absence tests, or historical ownership
  classification.
- The allocation audit found no new steady-state allocation in Phase 6 hot
  paths. It separately records 65 pre-existing OpenXR formatted-log candidates
  for later performance work.
- `git diff --check` is clean. Dependency/license data was regenerated after
  cargo ownership changes, and the final relative documentation links resolve.
- Supported-hardware Streamline/NVIDIA validation and physical-headset
  validation remain external manual acceptance lanes. They are not claimed and
  do not change the completed dependency/ownership result.
- No commit, merge, push, or branch promotion was requested or performed.

Phase 6 is closed. Future physics-chain grabbing/posing, avatar animation-state
machine conversion, and expression menu/parameter conversion extend the new
module boundaries; they are not deferred facade-removal work.
