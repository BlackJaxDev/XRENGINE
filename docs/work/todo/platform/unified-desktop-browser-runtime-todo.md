# Unified Desktop And Browser Runtime TODO

[<- Work docs index](../../README.md) · Design: [Unified desktop and browser runtime](../../design/platform/unified-desktop-browser-runtime-design.md) · Prerequisite: [Native subsystem integration debugging and validation](native-subsystem-project-split-todo.md) · Backend detail: [Browser renderer module design](../../design/rendering/browser-wasm-renderer-design.md) · Device and delivery validation: [Mobile WebGPU runtime TODO](../rendering/mobile-webgpu-runtime-todo.md)

Status: implementation resumed at the owner's request on 2026-10-01 from commit `11ef1f64663e631f969b36921eb5a02affabf9e5`, after the reference-harness and portable-host checks. The current source passes Editor, Server, VRClient, desktop WebGPU, all 19 portable-project compile rows and fresh browser interpreter/Jolt publication with zero compiler warnings/errors, including the browser platform leaf and corrected startup profile defaults. Chromium/SwiftShader qualifies bounded shared rendering profiles, and the complete compiled Editor publisher has activated the canonical RollingBall browser bundle. **53 of 110 items are checked; 57 remain open.** New evidence closes only the rows whose complete wording is satisfied. The [current checkpoint](../../progress/platform/unified-browser-checkpoint-2026-10-01.md) records the evidence and limits. At the owner's request, implementation proceeds in coherent engine/app pieces with narrow compile checks; broad browser and regression qualification occurs at end-to-end milestones rather than gating each small feature.

Created: 2026-09-29. Updated: 2026-10-02.

Owner: Runtime architecture / rendering / platform.

## Goal

Run the same engine, worlds, and C# game code in the browser (WebGPU, .NET 10 WebAssembly) as on desktop, following the engine model used by Unity and Godot web builds:

- **Same assets:** one serialized world, prefab, and component format.
- **Same code:** one set of engine and game assemblies.
- **Per-platform differences are limited to** platform leaves (renderer backend, audio, input, windowing, physics native build, transports) and cooked GPU/format variants (shaders, textures, audio codecs).

The editor stays a desktop application and gains an honest browser publish target on this path. The separate browser runtime on the `codex/webgpu-readiness-audit` branch is stabilized as a reference harness and retired once this path reaches parity.

## Current State (2026-10-02)

The published source at `4a801dba8aa7543b79b6c4fc44506a0115dcb54d` passes
Editor, Server, VRClient, desktop WebGPU, all nineteen portable compile rows and
fresh interpreter/native-Jolt browser publication with zero compiler warnings or
errors. The count is **53 of 110 named items complete, 57 open**. The genuine
Windows Editor CLI also passes for this snapshot in
[run 37005875660](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37005875660).
The game check stops at the pause capture before resize: the timed canvas capture
is green, while the subsequent full-page failure capture shows the yellow paused
HUD. Presentation/capture timing remains under investigation; the baseline
browser/rendering/physics job passes.

The preceding exact Editor bundle renders the real RollingBall course, ball,
obstacles and authored HUD in Chromium/SwiftShader. The mapped pause/resume HUD
transition is visible. Capture review found that the first resize check had
accepted only a CSS outline despite a green job. Current source corrects
output-generation readiness and strengthens that existing check to require
interior game/HUD pixels. Neither generic image changes nor a green job alone
establish full gameplay or desktop parity. The [current checkpoint](../../progress/platform/unified-browser-checkpoint-2026-10-01.md)
records exact captures, corrections, source groups and their separate limits.

| Area | Current evidence and remaining boundary |
| --- | --- |
| Shared host/platform | Real `Engine`, `RuntimeWorld`, game assemblies, caller-thread jobs/frame callbacks and the browser platform leaf compose successfully. Surface generations, timing resets and scoped pipeline selection are implemented; desktop pacing and the full blocking-site closure remain open. |
| Game and publishing | The unchanged authored RollingBall project passes genuine Windows Editor CLI publication and renders through the shipping browser player. Portable gameplay, repeated published-WASM lifecycle and scoped rollback/retry pass. Broader gameplay-state, resize and desktop comparison remain acceptance work. |
| Rendering | Bounded shared lit/HDR/tonemap, directional shadow and debug-overlay profiles have known-value Chromium evidence. Textured PBR, GPU deformation, sky/local shadows, coverage, engine UI/text and bounded post effects are implemented and compiled/cooked; their complete production and authored-world qualification remains open. |
| Assets/input/audio | Hash-verified shared cooked assets, font atlases, mapped transient input, touch controls, spatial/queued audio and composed activation are implemented. Default RollingBall cache v5 bytes remain identical; explicit required audio uses the backward-readable v6 extension. Queued-stream automatic looping, complete I/O closure, broader codec/IME/accessibility and device checks remain open. |
| Physics/networking | Native browser Jolt lifecycle/contact/query checks pass; a bounded matched native/WASM micro-scene has tolerance evidence. Canonical-game physics traces, physical-device budgets and real-server throttling/disconnect qualification remain open. |
| Toolchain/cleanup | The approved SDK/workload and Slang pins are retained, path casing is corrected, and clean Linux/Windows publication works. The separate browser reference runtime stays frozen until genuine parity permits retirement. |

Software WebGPU proves API/shader behavior for the recorded cases, not hardware
performance. Local Chromium is blocked by this host's Unix-socket policy; the
authorized Actions lanes provide live browser evidence. Historical build and
unit-test results below apply only to their recorded snapshots and do not imply
that the remaining verification rows have passed.

## Rules

- **No second runtime.** New browser functionality goes through engine components, the engine renderer contract, and engine pipelines. Do not add features to the separate browser scene, component, animation, or pipeline types. Do not reimplement the engine facade or host services inside `XREngine.Browser`.
- **Compiled before complete.** An item is complete only when its code builds under the [build gate](#build-gate). At the owner's 2026-10-02 direction, implement coherent groups with narrow compile/cook checks and run the full gate at end-to-end milestones; do not serialize every small feature behind the broad suite. Source that has not been compiled is not a completed deliverable.
- **Item kinds.** Every item carries one tag:
  - `impl`: source work that an implementation pass can finish and compile.
  - `verify`: needs a live run, capture, measurement, or physical device.
  - `owner`: needs an owner decision or approval before work proceeds.

  An implementation-only pass completes `impl` items, leaves `verify` items unchecked with a note of what is ready to validate, and stops at `owner` items. It never resolves an [owner decision](#owner-decisions) by guessing.
- **Named failures.** A required service, pass, or physics feature that a platform lacks fails with a named diagnostic. No silent CPU fallback for a requested GPU path, no WebGPU-to-WebGL2 switch, no dropped components.
- **Hot paths.** One managed-to-JavaScript crossing per frame for rendering; no per-draw interop; no per-frame heap allocations in recording, visibility, simulation, or submission.
- **No blocking on the browser path.** Asynchronous I/O and readback only; the browser runs frame-stepped on one thread.
- **Desktop preservation.** Validate every shared-contract change on OpenGL and Vulkan per the [pipeline invariants](../../../architecture/rendering/default-render-pipeline-notes.md) and [mesh submission contracts](../../../architecture/rendering/mesh-submission-strategies.md).
- **Validation order.** Validate each feature through its live path (desktop editor, browser page) before adding regression tests, following repository policy. Record browser evidence under `Build/_AgentValidation/<run>/` and durable findings in `docs/work/investigations/<subsystem>/`.
- **Approvals.** Toolchain pins, new dependencies (repository-built `joltc`, headless-browser test tooling), and supply-path changes need owner approval and license review.
- **No todo IDs in code.** Keep task IDs out of code, comments, type names, and diagnostics.

## Qualified implementation snapshot

The exact published commit `047bb7f1126f9fa6272325ace84446b3c9f7e1b9` passed
[real Chromium CI](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36947665282)
on 2026-10-02. Actual Chromium on Google SwiftShader passed the fourteen-case
`StandardLitColor`/HDR/tonemap diagnostic and five resizes without console errors.
The supported profile is shared `DefaultRenderPipeline`, direct GGX/Fresnel
lighting with at most four directional, eight point and eight spot lights,
RGBA16F scene HDR plus depth32, and Mobius/gamma output to a non-sRGB canvas.
Known HDR captures retained values above one and authored opacity. The 30 live
GPU resources stayed stable through each case and resize; retiring resources
drained to zero. Depth and texture/sRGB replacement, engine world play/stop,
asset lifetime, native Jolt and offline audio also passed. See the [lit-profile
acceptance record](../../progress/rendering/unified-webgpu-shader-cooking.md#lit-profile-live-acceptance)
for exact samples and artifacts. This is software-WebGPU correctness evidence,
not hardware performance or complete production/gameplay acceptance.

The same current checkpoint records five-cycle canonical RollingBall lifecycle
in both headless and canvas-composed published WASM: 35 registered objects at
each cycle boundary and 120 warm frames plus 600 measured frames per cycle. A
separate injected BeginPlay failure rolled back cleanly and an immediate retry
passed. Canvas composition initializes no GPU. A separate
published-WASM versus native Linux Jolt probe compared a bounded single-ball,
tilting-course micro-scene at 18 sampled checkpoints and found zero sampled
position, velocity, contact/onset, or ray deltas. Quaternion normalization noise
was at most `4.3e-8` radians. This is not a full canonical-game physics trace,
rendered browser play, or a universal determinism guarantee; UR07.03 and U3
acceptance remain open.

The checked implementation/approved-decision rows remain `UR01.01`, `UR01.04`, `UR01.06`, `UR02.02`, `UR03.01`, `UR05.01`, `UR06.03`, `UR06.04`, `UR07.01`, `UR07.02`, `UR07.06`, `UR07.07`, `UR08.04`, `UR10.01`, `UR10.04`, `UR10.06`, `UR10.07`, `UR11.03`, `UR15.01`, `UR15.02`; the newer partial runtime and renderer evidence does not itself check additional rows.
Static registrations are supplied by the unified C# path; frozen reference
Python scripts remain for the separately gated retirement work. Canonical
RollingBall world/gameplay/Jolt proof uses published WASM under Node, not a
rendered game: five-cycle callbacks, teardown and late BeginPlay failure/retry
pass, but the diagnostic package does not exercise Editor BrowserWebGPU
publishing and initializes no GPU.

The remaining 57 rows are still open; current source progress and partial live
qualification do not change the checked-row count. Shadow-enabled rendering,
engine UI, the full production tier, editor-published playable RollingBall,
memory/performance bounds, physical-device checks, recovery and broader
desktop/browser/device verification remain open.
The detailed [checkpoint record](../../progress/platform/unified-browser-checkpoint-2026-10-01.md)
preserves evidence and limitations.

### Compiled implementation closure (2026-10-02)

The coherent compute/texture/coverage/networking source group passes the desktop
WebGPU, Editor, Server and VRClient Release builds, all eighteen portable browser
compile rows, and a fresh browser interpreter/Jolt publication. ShaderCooker and
BrowserContentCooker also build cleanly; twenty-four raster recipes and the
canonical compute kernel cook successfully. Compiler results contain zero
warnings/errors. This closes the authored unsupported-physics cook audit
(UR07.04), transport/gateway and bounded browser networking composition
(UR12.01–UR12.02), and stale-completion rejection (UR14.02). That source snapshot reached
**45 checked and 65 open**.

Networking consumes a fresh trusted in-memory handoff and independently verified
loaded package identity; suspension discards admission and requires a new manager
and baseline. Optional voice remains explicitly unavailable. The opt-in shared
package has a conservative self-contained base-world profile. Its unmodified
server-generated world now passes the compiled Editor publication chain and the
browser package validator with byte-identical native world bytes. Real-server
play, new GPU deformation and
coverage pixels, layered textures, UI/IME behavior, custom-font cooking, full
recovery and production/device budgets remain open. Focused text accessibility
and a finite blocked-API list do not by themselves close broader UI or game-API
coverage rows.

### Compiled font and touch implementation (2026-10-02)

Authored FreeType bitmap-font cooking and shared virtual stick/button controls
now satisfy UR09.03 and UR09.05. Editor, native-Jolt Browser, Server, VRClient,
desktop WebGPU, all eighteen portable compile rows and fresh browser publication
pass with zero warnings/errors for the coherent source group. Glyph coverage is
cooked offline; browser runtime binds hash-verified owned atlases before world
activation. Virtual controls are ordinary engine UI feeding existing player
mappings with bounded multi-touch, physical-input preservation and ownership
cancellation. That source snapshot reached **47 checked and 63 open**. UI/IME pixels, device
input acceptance, broader accessibility and the complete quality profile remain
separate open work. The new opaque textured material projection exposed a normal
YAML alias-reconstruction issue during its targeted real cook. The corrected
saved-world path now passes production cooking, post-hydration capability audit
and packaging with strict runtime aliases and unchanged authored-file bytes.

### Compiled browser platform lifecycle (2026-10-02)

The platform-leaf extraction and surface/lifecycle timing work now satisfy
UR02.04 and UR02.05. The integrated Editor, Server, VRClient and desktop WebGPU
builds, all nineteen portable rows and fresh native-Jolt browser publication pass
with zero warnings/errors. The relocated host script is byte-identical at its
unchanged published URL. The same source group repairs canvas startup's missing
AA/exposure defaults and preserves explicit authored rejection diagnostics.
That source snapshot reached **49 checked and 61 open**. Physical lifecycle, desktop pacing,
and actual rendered RollingBall acceptance remain open; these implementation
closures do not imply those runtime checks passed.

### Compiled audio activation and hosting contract (2026-10-02)

The reviewed audio activation/lifecycle implementation now satisfies UR08.02:
page and surface blockers compose, stale output completions are rejected, and
only an explicitly audio-required world gates simulation while presentation
continues. Canonical optional-audio RollingBall cache bytes remain unchanged;
required audio uses a backward-readable extension. The shared managed ownership
probe passes eleven cases. UR15.03 is covered by the new
[static hosting guide](../../../developer-guides/runtime/browser-static-hosting.md),
without applying a deployment or claiming production-header/device validation.
Editor, Server, VRClient, desktop WebGPU, all nineteen portable compile rows and
fresh native-Jolt Browser publication pass with zero warnings/errors for this
coherent source group. The count is **51 checked and 59 open**. Queued-stream
automatic looping, real new-audio output/gesture qualification and broader audio
coverage remain open. The same group repairs resize readiness and rejects the
CSS-border-only image false positive; its fresh runtime acceptance is pending.

### Reconciled shader-cook and output implementation (2026-10-02)

A row-by-row review of the same compiled `4a801dba` snapshot closes UR05.02
and UR04.05, bringing the current count to **53/110**. The pinned Slang cooker
produces and validates schema-3 WGSL engine artifacts; full shader-profile
coverage and semantic pixel checks remain separate rows. Canvas presentation
uses `RenderFrameOutputDescription`, reacquires the current output each frame,
and rejects obsolete generations/properties in both recording and submission.
The current-output readiness correction is built, but the fresh game run stops
before resize, so this implementation closure does not claim live resize
acceptance. Browser composition still needs every unavailable host-service slot
explicitly accounted for and remains open.

### Compiled service and queued-audio completion (2026-10-02)

Explicit browser host-service composition and the complete bounded queued-audio
transport now satisfy UR01.02 and UR08.01, bringing the current count to
**55 checked and 55 open**. The isolated coherent source snapshot passes Editor,
Server, VRClient, desktop WebGPU, all nineteen portable compile rows and fresh
native-Jolt Browser publication with zero compiler warnings/errors. Its actual
published WebAssembly also passes three XRWorld/Jolt cycles and 360 shared frames
under Node with file-backed content fetches and no GPU. The audio implementation
has independent ownership/scheduling review and three production-JavaScript
boundary probes; exact-commit offline audio sample checks and real-device audio
acceptance remain pending. Browser startup still takes Development-mode assembly
scans for replication and type redirects; the separate published-metadata audit
and implementation remains open under UR01.03.

## Build Gate

Run narrow compile/cook checks during coherent implementation groups, and the full gate at end-to-end milestones and before checking an `impl` item. Fix failures caused by the change; list unrelated failures separately instead of working around them.

| Check | Command (repository root) |
| --- | --- |
| Shared closure, desktop | `dotnet build XREngine.Runtime.Rendering.WebGPU/XREngine.Runtime.Rendering.WebGPU.csproj` |
| Editor | `dotnet build XREngine.Editor/XREngine.Editor.csproj` |
| Server | `dotnet build XREngine.Server/XREngine.Server.csproj` |
| VRClient | `dotnet build XREngine.VRClient/XREngine.VRClient.csproj` |
| Shared closure, `browser-wasm` | `pwsh Tools/Test-PortableBrowserCompile.ps1 -Configuration Release` |
| Browser publish | `dotnet publish XREngine.Browser/XREngine.Browser.csproj -c Release -m:1` |

The two browser checks need the `wasm-tools` workload. A workstream that touches only desktop leaves may skip them; a workstream that touches any project in [PortableProjects.tsv](../../../../Build/Portable/PortableProjects.tsv) may not. Run repository PowerShell tools with PowerShell 7; Windows builds do not need it.

Historical result: all six checks passed with no warnings on 2026-09-30 for that source snapshot. On 2026-10-01, the current-tree portable compile lane (18 rows) and fresh browser publish also passed using the supported in-process MSBuild task-host override. The exact published 2026-10-02 commit additionally passed the actual Chromium CI build/publish and smoke path linked above. The [build stabilization record](../../progress/platform/unified-runtime-build-stabilization.md) has historical toolchain versions and payload sizes; the [current checkpoint](../../progress/platform/unified-browser-checkpoint-2026-10-01.md) records resumed-build and live qualification evidence.

## Owner Decisions

These block the listed items. Record each decision here with its date when it is made.

| ID | Decision | Blocks | Notes |
| --- | --- | --- | --- |
| D1 | Where the portable engine host lives. | UR17, UR01.06, UR02, UR10 | Approved 2026-09-30: `XREngine.Runtime.Host`, a portable `net10.0` project owning the facade, timer, tick lists, world host, settings and shared host services. Bootstrap retains desktop composition. |
| D2 | The U3 parity target: port the existing rolling-ball sample to portable code. | UR10.06–UR10.08, UR11.05 | Approved 2026-10-01: retain the existing gameplay as the parity target, isolate the VR-specific host, and rebrand the active project to **Rolling Ball** (`Samples/RollingBall`). The descriptive working name and source-only asset audit do not establish legal clearance. |
| D3 | Jolt as the browser physics backend and later desktop default promotion. | UR07 | Approved 2026-10-01: implement browser Jolt and tolerance-based parity; preserve the current desktop physics default until parity is proven. No default promotion is approved by this decision. |
| D4 | Jolt native supply and toolchain plan. | UR07.01 | Approved 2026-09-30: browser-only static archives from the recorded `joltc`/Jolt commits, pinned Emscripten 3.1.56 and the single-threaded wrapper; qualify linkage, callbacks, stepping and teardown. Existing desktop NuGet supply is preserved. See the [native supply proposal](../../design/platform/jolt-browser-native-supply.md). |
| D5 | Pin the .NET SDK and `wasm-tools` workload (`global.json` and recorded workload version). | UR00.02, UR15.01 | Approved 2026-09-30 and implemented: SDK 10.0.401, workload set 10.0.401.1. Emscripten 3.1.56 matches the separately approved browser Jolt supply. |
| D6 | Static registration mechanism for the browser host: the existing script-based generator or the C# source generator the design names. | UR01.04, UR17.05 | Approved 2026-09-30: extend [Generate-AotFactoryRegistrations.ps1](../../../../Tools/Generate-AotFactoryRegistrations.ps1) for both hosts with separate portable/desktop input sets, retiring the browser Python generator. |
| D7 | Authoring route for web-tier shaders. | UR05.02, UR05.07 | Approved 2026-10-01: additive Slang sources for the browser-compatible engine raster passes, reuse the existing WGSL compute kernels, and initially preserve desktop GLSL unchanged. See the [source inventory](../../progress/rendering/unified-webgpu-shader-inventory.md). |
| D8 | Normalize the Core project's directory casing in git. | UR00.08, UR15.01 | Approved and implemented 2026-09-30: 244 case-only index renames, preserving file contents and modes. |
| D9 | Headless-browser smoke tooling. | UR15.02 | Approved 2026-10-01: official Playwright local smoke tooling (1.63.0, Apache-2.0) for the build machine. The installed Chromium cannot start under this executor's Unix-socket policy; no browser or GPU result is implied. |
| D10 | Cross-platform physics determinism. | UR07.03 | Approved 2026-10-01: use tolerance-based parity first. Strict determinism and repository-built `joltc` on every platform remain a measurement-driven later decision. |
| D11 | Shipping runtime mode (interpreter or AOT). | UR13.02 | Approved 2026-10-01: continue the untrimmed interpreter path; shipping AOT/trimming selection remains dependent on measured performance and serialization qualification. |
| D12 | Retire the separate browser runtime after shared-engine parity. | UR16 | Approved 2026-10-01: retire it after the shared engine reaches parity. Preserve the frozen reference harness until then. |
| D13 | Whether Server and VRClient may reference the model asset pipeline, and whether Bootstrap's registration generator may scan it. | UR00.12 | Approved 2026-09-30: retain the application-root references and Bootstrap model-pipeline scan; update the graph and documentation. Approved 2026-10-01: also retain the existing desktop ModelingIntegration factory scan and correct the stale dependency-boundary assertion; this adds no native dependency. |
| D14 | Browser managed Jolt binding supply after the unchanged package fails static linking. | Browser native proof, UR01.06, UR07.01/UR07.02 | Approved 2026-10-01: a reviewed browser-only source build correcting `JoltPhysicsSharp` 2.22.0's conflicting `JPH_ContactListener_SetProcs` overload (`void` is the pinned native signature). Keep desktop package supply unchanged. Source pin/license review and the exact native/managed spike publish passed on 2026-10-01. Browser execution proof remains required. The owner separately approved Python only as an internal Emscripten dependency; implementation and editing helpers remain C#/PowerShell/JavaScript. See the [native supply record](../../design/platform/jolt-browser-native-supply.md#managed-linkage-findings). |

## Remaining Work

As of this 2026-10-02 update, 53 of 110 items here are checked, and the [prerequisite checklist](native-subsystem-project-split-todo.md) has 35 of 36 items open. The reference/runtime-host checks, Editor/Server/VRClient builds and smokes, browser CI and current bounded live qualification are recorded in their dated reports. New source implementation and partial profile evidence do not close an item without its own required build or acceptance evidence. The [unified browser checkpoint](../../progress/platform/unified-browser-checkpoint-2026-10-01.md) distinguishes what now runs from the remaining full production/game, physical-device, performance, recovery and networking qualification.

Sizes are rough planning estimates for one engineer: **S** is days, **M** is one to two weeks, **L** is several weeks, and **XL** is a month or more. Revise them once U1 is reached.

| Stage | Workstreams | Open items | Size | Blocked by |
| --- | --- | --- | --- | --- |
| U0 reference checks | UR00 | 0 | Complete locally | Historical evidence and limits recorded |
| Prerequisite integration | [Native subsystem checklist](native-subsystem-project-split-todo.md) | 35 | L | Remaining native subsystem acceptance; broader physics parity before any default-promotion consideration |
| U1: engine boots | UR17, UR01, UR02, UR03 | 11 | L each; UR01 M | Complete full U1 world/component, asset and frame acceptance beyond the passing lifecycle probes; D1 and D6 are approved |
| U2: engine renders | UR04, UR05, UR06 | 19 | UR04 XL, UR05 XL, UR06 L | Extend the qualified depth and lit/HDR profile to remaining production passes, shadows and UI; compare supported worlds and preserve desktop rendering; D7 is approved |
| U3: project plays | UR07, UR08, UR09, UR10, UR11 | 13 | UR07 L, UR08 M, UR09 L, UR10 M, UR11 M | Complete browser physics/audio/input/UI and authored-project play evidence; D2, D3 and D4 are approved |
| U4: production | UR13, UR14, UR15, UR16 | 15 | M each | Complete measurements, recovery, CI/hosting and device evidence; D9, D11 and D12 are approved |
| U5: networked client | UR12 | 1 | L | U3 |

The current critical path follows the passing build, world-lifecycle, depth/texture and bounded lit/HDR gates: extend the engine renderer to shadows and UI, qualify the remaining production pipeline, then render and play the editor-published RollingBall project. D1, D6, D7 and D14 are approved. Jolt, audio, and portable sample implementation can continue in parallel where the prerequisite checklist permits, while full-game and other row-level acceptance stays explicit.

## Historical Pause Record (2026-09-30; implementation resumed 2026-10-01)

The following records the state and decisions as of the 2026-09-30 pause. It is retained as historical evidence; use the [2026-10-01 checkpoint](../../progress/platform/unified-browser-checkpoint-2026-10-01.md) and Current State above for the resumed implementation.

**Completed workstreams:** UR00's 12 reference checks and UR17's seven portable-host items. Host owns the real `Engine` facade, timer, settings and world services; Bootstrap owns desktop composition. The final full solution, WebGPU closure, all 15 portable browser projects, fresh browser publish and RollingBall Development Debug build pass with zero warnings/errors. OpenGL/Vulkan Play→Edit camera/UI restore and Server/VRClient local unit-world startup have recorded live evidence. The [host validation record](../../investigations/platform/portable-engine-host-validation.md) preserves the limits: VRClient's existing shutdown queue stall, physical HMD/proxy operation and broader unit-suite acceptance remain open.

**Partial preparation; no additional checklist items completed:**

- UR01 references/audit: Browser references Host and evaluates to 13 full portable projects, including animation/audio/input adapters. The reviewed compile manifest has 15 projects. Its entry point still initializes the frozen reference scene host; real engine composition, complete component/serializer registration and startup audit are unfinished.
- UR01.05 live interpreter checks: a disposable page fetched the real authored `XRWorld` YAML. `XRPrefabSource`/camera component YAML and cooked-binary payloads in a MemoryPack envelope passed. The representative world failed by name on the external shader reference `Shaders/Common/UnlitColoredForward.fs`; its YAML and binary round-trips are not qualified. A fetch-backed real asset-reference owner is still required. The [browser boot qualification record](../../progress/platform/portable-browser-engine-boot.md) describes the exact scope.
- Approved D4 native prerequisite: clean exact Jolt/joltc pins build both archives with Emscripten 3.1.56. The initial spike publish passed, but live `Foundation.Init` failed on `JPH_Init` because archive basename admission created an empty `libjoltc` table. Staging the byte-identical `joltc.a` fixes that naming issue and exposes the managed signature conflict recorded in D14. Browser linkage, callbacks, 120 steps, raycast, teardown and absence of worker creation are **not qualified**. Jolt default promotion and determinism remain separate decisions.

**Resume in this order:** resolve D14 and complete the approved native spike; finish UR01.01–UR01.05 with real composition/registrations and supported external asset loading; boot an actual fetched/cooked `XRWorld` through `RuntimeWorld` for UR01.06; then implement and validate UR02 and UR03 before rendering work. Repeat the build gate after each workstream. Keep the separate runtime frozen and preserve every unchecked verification/owner item until its evidence or decision exists.

Remaining workstreams are UR01–UR16: live browser-world boot; shared frame stepping, caller-thread scheduling and production surface lifecycle; asynchronous assets/cooking; the engine WebGPU renderer, shaders and web pipeline; browser physics/audio/input/UI/text and portable game qualification; editor publishing; performance, recovery, CI/hosting/device qualification; networking; and retirement of the separate browser runtime. Decisions D1–D14 are recorded above; browser behavior or production evidence remains open where specified. No physical mobile-device, shipping AOT, Linux CI or production-performance qualification has been completed.

## Execution Order

1. **UR00** until the build gate passes and the harness has recorded evidence.
2. **UR17** and **UR01.01–UR01.05**. These are independent and may proceed together.
3. **UR01.06**, then **UR02** and **UR03**.
4. **UR04**, **UR05**, **UR06** in that order. The UR05.01 shader inventory can start as soon as UR00 is done.
5. **UR07**, **UR08**, **UR09**, **UR10**. These do not depend on each other; UR07 and UR10 do not depend on rendering.
6. **UR11**.
7. **UR13–UR15**, **UR12**, then **UR16**. Start the UR13.01 interpreter measurement as soon as U1 boots.

## Gates

| Gate | Result | Main workstreams |
| --- | --- | --- |
| U0 — Reference harness | The build gate passes. The branch's browser app and editor browser target build, run, and have recorded evidence. The separate runtime is frozen. | UR00 |
| U1 — Engine boots in the browser | The real portable assemblies load a real `XRWorld` asset through fetch, construct scenes, the game mode, and components, and tick fixed/variable updates without rendering. | Native subsystem integration acceptance; UR17; UR01–UR03 |
| U2 — Engine renders in the browser | Engine cameras, `ModelComponent`, and engine materials render through the WebGPU backend and the web tier of `DefaultRenderPipeline`, with resize and device-loss reporting. | UR04–UR06 |
| U3 — Published project plays | Editor Build Project with the browser target produces a site that runs the startup world with the project's game code, Jolt physics, audio, input, and UI. The parity target chosen in D2 plays. | UR07–UR11 |
| U4 — Production qualification | Physical mobile devices, budgets, runtime mode (interpreter or AOT), recovery, hosting, and CI pass. The separate runtime is retired. | UR13–UR16 |
| U5 — Networked browser client | The browser client joins the real server path. | UR12 |

## UR00 — Stabilize The Branch As A Reference Harness

- [x] **UR00.01** `impl` Repair the portable source guard so it compiles as an inline build task, then validate the implemented source policy: permit `System.Drawing.Primitives` value types and deny Windows bitmap APIs. Resolve any remaining guard failures in the shared closure. Done 2026-09-30: deny rules narrowed to actual vendor, registry, and socket API use; native callback entry points moved out of Rendering into the desktop platform module.
- [x] **UR00.02** `owner` Pin the .NET SDK and install the `wasm-tools` workload per [the browser README](../../../../XREngine.Browser/README.md) (decision D5). Done 2026-09-30 after owner approval: SDK 10.0.401, workload set 10.0.401.1, `wasm-tools` 10.0.112, WebAssembly pack 10.0.12 and Emscripten 3.1.56; normal browser publish passes.
- [x] **UR00.03** `verify` Serve the published output locally. Done 2026-09-30: WebGPU reference fixture rendered in split and single-camera views, both PNGs viewed, counters captured and audio activation observed. Results, newline/hash failure and limits are in the [harness investigation](../../investigations/rendering/desktop-browser-reference-harness.md).
- [x] **UR00.04** `verify` Build the editor and run Build Project with `BrowserWebGPU` on a minimal supported world. Done 2026-09-30: the actual editor CLI publishes a saved camera/indexed-unlit-quad world; served output renders that world with no demo controls. PNG viewed and console errors checked; the settings-save reference expansion is recorded separately.
- [x] **UR00.05** `impl` Freeze feature work in the separate runtime (`Browser*` scene, component, animation, collision, and pipeline types, and `BrowserWorldPublishExporter`); allow fixes only to keep the harness running. Done 2026-09-30: direction recorded in the browser README and mobile tracker; no separate-runtime feature additions made.
- [x] **UR00.06** `impl` Reconcile the [mobile TODO](../rendering/mobile-webgpu-runtime-todo.md) code-completion rows with build/run evidence. Done 2026-09-30: compiled project closure and shader-cooker build distinguished from recipe, feature, device and performance qualification. No row is labelled never compiled without exclusion evidence.
- [x] **UR00.07** `impl` Stop shipping developer harness controls in editor-published output. Done 2026-09-30: staged player entrypoint requires a cooked startup world, hides the fixture overlay and contains no demo/diagnostic controls. Missing world fails by name; stale compressed entrypoint/descriptor variants are removed.
- [x] **UR00.08** `owner` Normalize the Core project's directory casing (decision D8). Done 2026-09-30 after approval: all 759 index paths use `XREngine.Runtime.Core`; 244 case-only renames preserve modes and blobs. Linux execution remains open.
- [x] **UR00.09** `impl` Make the [build gate](#build-gate) pass: the shared closure, Editor, Server, VRClient, the unit-test project, the `browser-wasm` compile lane, and the browser publish. Fix failures that come from the extraction and retargeting; record unrelated failures separately. Done 2026-09-30, with the full solution also building; unit-test execution failures are recorded in the [build stabilization record](../../progress/platform/unified-runtime-build-stabilization.md).
- [x] **UR00.10** `verify` Run the full unit-test suite and triage with the [prerequisite checklist](native-subsystem-project-split-todo.md#build-dependency-and-publish-boundaries). Done 2026-09-30: repeat run completes without host abort, with 4,804 passed, 669 failed and seven runner skips. The [investigation](../../investigations/rendering/desktop-browser-reference-harness.md) separates source contracts, fixtures, behavioral failures and the CUDA requirement, compares baseline names, and records the TRX skip-accounting discrepancy. Broader suite acceptance remains unqualified.
- [x] **UR00.11** `verify` Exercise the relocated native callback entry points. Done 2026-09-30: live Vulkan ImGui creates/resizes/removes a detached viewport and completes native window quarantine; installed clipboard get/set callbacks pass null, sentinel and throwing-provider smokes without changing the OS clipboard. Native debug-utils marker and Streamline messages reach the relocated callbacks. A separate production RenderBench host process with entry points deliberately absent emits the named startup error. Implicit desktop composition and independent GPU-fixture scheduler limits are recorded in the [investigation](../../investigations/rendering/desktop-browser-reference-harness.md).
- [x] **UR00.12** `owner` Decide D13. Approved 2026-09-30: retain Server/VRClient model-pipeline references and Bootstrap's model-pipeline generator scan. Application reference checks and organization docs are updated; the separate ModelingIntegration scan is still a pending decision.

**Acceptance (U0):** the build gate passes, and there is reproducible build, publish, and run evidence for the branch as it stands, with its limits recorded.

## UR17 — Portable Engine Host And Facade

Runs after UR00 and before UR01.06, UR02, and UR10. Blocked on decision D1.

**Starting point.** `XREngine.Runtime.Bootstrap` targets `net10.0-windows7.0`, references every desktop leaf, and owns:

- the `Engine` static facade: about 40 source files under `Engine/`, `SubsystemHost/`, and `RenderingHost/`, including `Engine.Time`, `Engine.TickList`, `Engine.Worlds`, `Engine.State`, `Engine.Lifecycle`, `Engine.PlayMode`, `Engine.Settings`, and `Engine.Threading`;
- `EngineTimer` (`Core/Time/`), which owns the update, collect-visible, and fixed-update workers and the render-thread wait;
- `RuntimeWorldHost` and the world-host composition services (`WorldHost/`);
- `GameStartupSettings` and the other settings types (`Settings/`);
- the implementations installed into about 30 host-service slots declared by the shared projects: `RuntimeTimingServices`, `RuntimeWorldHostServices`, `RuntimeWorldRegistryServices`, `RuntimeWorldObjectServices`, `RuntimeGameModeHostServices`, `RuntimePawnHostServices`, `RuntimePlayerControllerServices`, `RuntimeInputServices`, `RuntimeInputCaptureServices`, `RuntimeAnimationHostServices`, `RuntimeAudioIntegrationServices`, `RuntimeRenderingHostServices`, `RuntimeRenderObjectServices`, `RuntimeShaderServices`, `RuntimePhysicsServices`, `RuntimeThreadServices`, `RuntimeTransformServices`, `RuntimeSceneNodeServices`, `RuntimeSceneStreamingHostServices`, `RuntimeNetworkingHostServices`, `RuntimeNetworkDiscoveryHostServices`, `RuntimeMaintenanceServices`, `RuntimeDebugHostServices`, `RuntimeWindowApplicationServices`, `RuntimeVideoStreamingServices`, `RuntimeVrRenderingServices`, `RuntimeVrStateServices`, `RuntimeVrInputServices`, `RuntimeCharacterMovementVisualizationServices`, `RuntimeStaticColliderAuthoringServices`, and `RuntimeApplicationCapabilityServices`.

`XREngine.Browser` installs none of these slots. Game code calls the facade directly (`Engine.Delta`, `Engine.FixedDelta`, `Engine.Assets`, `Engine.ShutDown`), so the same game assembly cannot load in the browser until the facade is portable.

**Constraint.** A C# partial type cannot span assemblies. When `Engine` moves, its desktop-only members (windows, VR lifecycle, network discovery, profiler transport) cannot stay behind as Bootstrap partials; they move behind host-service contracts or onto a separate desktop type, and their call sites are updated.

- [x] **UR17.01** `owner` Decide the project boundary (decision D1) and the portable project's name. Approved 2026-09-30: `XREngine.Runtime.Host`; the preceding local reference gate is complete.
- [x] **UR17.02** `impl` Inventory Bootstrap file by file as portable as-is, portable once a leaf reference is replaced by an existing contract, or desktop-only. Done 2026-09-30: the [ownership inventory](../../progress/platform/portable-engine-host-ownership.md) contains all 165 source files and 37 service rows, including the desktop provider and lifetime cuts. Inventory changed no production source; existing build evidence applies.
- [x] **UR17.03** `impl` Move the portable set into the portable project, keeping namespaces and type names (`XREngine.Engine`, `XREngine.Timers.EngineTimer`). Done 2026-09-30: 121 reviewed source files moved to `XREngine.Runtime.Host`, registered in the portable manifests, properties and solution. The [compiled identity audit](../../progress/platform/native-subsystem-type-identities.md#shared-engine-host-identities) preserves all 152 baseline public names: 117 moved, 35 stayed, none missing. Desktop builds, full browser compile lane and publish pass; live qualification remains below.
- [x] **UR17.04** `impl` Move desktop-only facade members behind host-service contracts or a desktop type. Done 2026-09-30: platform initialization belongs to desktop composition; startup policy, display extent, renderer/pipeline catalogs and headless physics factories are explicit contracts. VR providers and their leases remain desktop-owned. Missing startup policy and explicit unavailable VR operations fail by name; shared networking admission remains before transport startup.
- [x] **UR17.05** `impl` Give static factory generation an owner that both hosts can use (decision D6). Done 2026-09-30: shared PowerShell/MSBuild generation uses explicit portable/desktop source sets and a Browser bridge manifest. Isolated deltas preserve the 67 shared factories and Rendering's 124 commands, removing command duplicates. Browser Python and checked-in generated source are retired; compile/publish checks pass.
- [x] **UR17.06** `impl` Reduce Bootstrap to desktop composition: leaf installation, window and VR startup, and desktop launch profiles. Editor, Server, VRClient, benchmarks, and samples build against the new boundary. Done 2026-09-30: final full solution, WebGPU closure, 15-project Release browser compile, fresh browser publish and RollingBall Development Debug build pass with zero warnings/errors. Native VMA configuration follows the managed Debug/Release suffix; the sample manifest uses shared OpenVR contract types.
- [x] **UR17.07** `verify` Launch the Editor, Server, and VRClient and load the unit-testing world. Confirm startup, play mode, and frame pacing match the pre-move baseline. Done 2026-09-30: fresh OpenGL/Vulkan Editor Before→Play→Edit→After runs preserve canonical/active camera identity and visible ImGui UI; Server runs a playing headless world; VRClient's local unit-world entry advances fixed updates and measures 89.808 Hz variable updates across 30 ready samples. All three timer source files match their pre-move contents; Editor timing and inherited rendering diagnostics are compared in the [validation record](../../investigations/platform/portable-engine-host-validation.md). VRClient's unrestricted render-dispatch counter is not physical FPS, and its inherited shutdown queue stall remains open. Physical HMD/proxy operation and broader suite acceptance are not established by these smokes.

**Acceptance:** the facade, timer, and host services compile for `browser-wasm` from the same assembly desktop uses, and Bootstrap contains only desktop composition.

## UR01 — Portable Engine Assemblies In The Browser Host

Shared projects already target `net10.0` with whole-project checks. Depends on their [integration acceptance](native-subsystem-project-split-todo.md#build-dependency-and-publish-boundaries); source completion alone does not establish browser startup.

- [x] **UR01.01** `impl` Verify the browser host references the full portable assemblies and include the integration adapters needed for real-world boot. The host currently references Core, Rendering, the WebGPU module, and Animation only. Source-subset profiles and the old portable build property are already removed; qualify the evaluated closure and full API surface.
- [x] **UR01.02** `impl` Make `XREngine.Browser` a composition root that installs, explicitly and as they land:
  - the host services from UR17, each either implemented or installed as a named unsupported service;
  - the browser leaves: WebGPU renderer, browser platform, Jolt, Web Audio, browser input, fetch asset source, WebSocket transport.

  Unavailable required services fail by name.
- [ ] **UR01.03** `impl` Audit static constructors, module initializers, and reflection scans reachable at browser startup. Qualify the implemented published-metadata lookup and desktop service boundaries, then move any remaining browser-reachable desktop initialization into leaves.
- [x] **UR01.04** `impl` Generate static registrations for browser component, transform, serializer, and module registration with the mechanism chosen in D6. Retire the branch's Python registration generator.
- [ ] **UR01.05** `verify` Load real YAML/MemoryPack assets in the interpreter and round-trip representative worlds, prefabs, and components.
- [x] **UR01.06** `impl` Boot a real `XRWorld` fetched from a cooked bundle through `RuntimeWorld` and the world host, replacing the host's use of `RuntimeSceneHost`. Construct scenes, the game mode, pawns, and components; run fixed and variable updates; report lifecycle state to the page. Depends on UR17.

**Acceptance:** the browser runs the engine's own world, scene, and component lifecycle from the same binaries desktop uses.

## UR02 — Platform Host, Frame Stepping, And Scheduling

Depends on UR17.

- [ ] **UR02.01** `impl` Extract a single-frame engine step (fixed-step simulation with bounded catch-up, variable update, visibility, render recording, submission) that desktop and browser hosts both call. The loop lives in `EngineTimer`: `RunGameLoop`, `BlockForRendering`, the `DispatchUpdate`/`DispatchCollectVisible`/`DispatchSwapBuffers`/`DispatchRender` methods, and the fixed-update worker. `RuntimeRenderThreadHost` in Rendering only wraps the render-thread side. Start from `EngineTimer.BeginExplicitFrame`, which already runs one deterministic frame on the calling thread with the workers stopped, and extend it to real elapsed time.
- [x] **UR02.02** `impl` Support rendering on the calling thread: update, swap, collect, and render run in sequence within one step. Verify the engine's double-buffered render state works without a dedicated render thread.
- [ ] **UR02.03** `impl` Add a caller-thread executor for the job system (`JobManager`). Inventory every blocking and thread-creating site in the shared closure and make each asynchronous, move it to a desktop leaf, or confine it to cook and editor code. Regenerate the inventory with:

  ```sh
  git grep -l -E "\.Wait\(|\.WaitOne\(|\.Result\b|GetAwaiter\(\)\.GetResult\(\)|Thread\.Sleep|\.WaitAll\(|\.WaitAny\(|\.Join\(" -- ":(icase)<project>/*.cs"
  git grep -l -E "new Thread\(" -- ":(icase)<project>/*.cs"
  git grep -l -E "Task\.Run\(|Parallel\.(For|ForEach|Invoke)|ThreadPool\." -- ":(icase)<project>/*.cs"
  ```

  File counts on 2026-09-30 (lexical matches, so they include false positives such as `string.Join`):

  | Project | Blocking waits | `new Thread` | Task/parallel/pool |
  | --- | --- | --- | --- |
  | `XREngine.Runtime.Core` | 29 | 4 | 8 |
  | `XREngine.Runtime.Rendering` | 39 | 2 | 9 |
  | `XREngine.Data` | 9 | 0 | 7 |
  | `XREngine.Extensions` | 2 | 0 | 4 |
  | `XREngine.Animation` | 1 | 0 | 0 |
  | `XREngine.Runtime.AudioIntegration` | 3 | 0 | 2 |
  | `XREngine.Runtime.AnimationIntegration` | 3 | 0 | 0 |
  | `XREngine.Runtime.InputIntegration` | 1 | 0 | 1 |
  | `XREngine.Browser` | 1 | 0 | 0 |

  The project that UR17 creates adds `EngineTimer` and `Engine.Threading` to this list.
- [x] **UR02.04** `impl` Create `XREngine.Runtime.Platform.Browser` from the branch's canvas host and surface contracts: `browser-canvas-host.js`, `IRuntimeSurfaceHost`, `RuntimeSurfaceState`, `BrowserCanvasRenderTarget`. It covers CSS/backing size, DPR caps, orientation, safe area, detach/reattach, and output generations.
- [x] **UR02.05** `impl` Handle page visibility, freeze/resume, and `pagehide`/`pageshow`; reset timing and invalidate temporal history after suspension or large gaps.
- [ ] **UR02.06** `verify` Confirm desktop editor and VRClient frame pacing is unchanged.

**Acceptance:** one engine frame step drives desktop and browser, and no browser-reachable code blocks.

## UR03 — Asset I/O And Per-Platform Cooking

- [x] **UR03.01** `impl` Route `AssetManager` loading through the asynchronous members of the asset-source contract and implement the browser source with same-origin, credential-free, hash-verified fetches, reusing `content-loader.js` and `content-manifest.js`. The contract is `IRuntimeAssetSource` with `IAssetReadBatch` (`XREngine.Runtime.Core/Assets/IO/`), installed through `DirectStorageIO.Source`; its only implementation is the DirectStorage leaf. It also exposes synchronous members (`Exists`, `ReadAllBytes`, `ReadRange`, `TryReadInto`, `TryReadFileInto`, `IAssetReadBatch.Execute`). Either remove those from the contract or make the browser source fail them by name; do not block on a fetch.
- [ ] **UR03.02** `impl` Remove synchronous load wrappers from runtime-reachable paths. On 2026-09-30 the asset manager had 10 sync-over-async sites, and Core and Rendering had 136 direct `File`/`Directory`/`FileStream` call sites across 39 files that bypass the asset source.
- [ ] **UR03.03** `impl` Add a platform target to cooking (`CookContent` in `XREngine.Editor/ProjectBuilder.cs`). Web cooking produces:
  - WGSL shader artifacts;
  - ASTC 4×4 and ETC2 texture variants with RGBA8 fallbacks, reconciled with the [texture compression TODO](../texturing/texture-compression-and-cooked-cache-todo.md);
  - web-decodable audio;
  - per-asset capability requirements.
- [ ] **UR03.04** `impl` Generalize the branch's `BrowserContentPackageBuilder` rules (immutable SHA-256 payload URLs, revalidated manifest, dependency closures, essential and streamed splits, strict limits) to the real asset graph instead of browser-only DTOs.
- [ ] **UR03.05** `impl` Keep bounded download concurrency, cancellation, retry, progress, and per-frame integration budgets, reusing the branch's delivery code. Track retained, staging, and estimated GPU bytes.
- [ ] **UR03.06** `verify` Validate cold and warm cache, throttled, failed, corrupt, and missing payloads, and cancel/restart. This carries over the mobile TODO's MW07 acceptance.

**Acceptance:** the browser loads the same world assets as desktop, with only cooked platform variants differing.

## UR04 — WebGPU Renderer Backend For The Engine

- [ ] **UR04.01** `impl` Implement `AbstractRenderer` and its API-object wrappers in `XREngine.Runtime.Rendering.WebGPU`. The base class is about 2,100 lines with 66 abstract members, and the module currently implements only `IBrowserRendererHost`. Implement coherent groups with narrow compile checks; qualify rendering through the complete engine path at end-to-end milestones before closing the corresponding coverage:
  - [ ] Renderer skeleton registered in the backend catalog: every abstract member is implemented or fails with a named unsupported diagnostic; clear and present work.
  - [ ] Data buffers and views, programs, mesh renderers, and vertex layouts: one unlit `ModelComponent` renders through an engine camera.
  - [ ] 2D, array, and cube textures and samplers: a textured material renders.
  - [ ] Framebuffers and render buffers: offscreen targets and resolves work.
  - [ ] Materials and uniform data: the engine's lit material renders.
  - [ ] Compute dispatch.
- [ ] **UR04.02** `impl` Track GL-shaped state in C# and resolve it into cached immutable render pipelines, layouts, and bind groups with complete keys and bounded caches, following the Vulkan backend's approach.
- [ ] **UR04.03** `impl` Record commands into reusable C# arenas and flush one packet per frame to the JavaScript executor. Reuse the branch's resource, command, readback, usage-scope, limits, and pipeline-cache executors. Move the policy logic in `browser-render-pipeline.js` into C#.
- [ ] **UR04.04** `impl` Clean up the renderer contract for non-blocking backends:
  - neutral `RuntimeImage` readbacks (implemented shared contract; browser renderer support still required);
  - asynchronous-only screenshots, pixel reads, and luminance;
  - `WaitForGpu` rejected with a named error on non-blocking hosts;
  - truthful capability probes: no indirect-count draw, no mesh shaders, no bindless textures, bounded bind groups.
- [x] **UR04.05** `impl` Present to the canvas through `RenderFrameOutputDescription` with surface generations, re-acquiring the output each frame and rejecting obsolete plans after resize.
- [ ] **UR04.06** `impl` Handle pending, ready, failed, and lost states. Recovery reconstructs device resources from CPU-side and cooked sources, carrying over the mobile TODO's MW10.01–MW10.05.
- [ ] **UR04.07** `impl` Add error scopes, debug labels, and bridge counters. Recording and submission must not allocate per frame.
- [ ] **UR04.08** `verify` Validate every shared renderer-contract change on OpenGL and Vulkan with editor captures from multiple positions.

**Acceptance:** the engine's renderer runs on WebGPU through the same contract as OpenGL and Vulkan, with one JavaScript crossing per frame.

## UR05 — Shaders And Materials

**Starting point.** `Build/CommonAssets/Shaders` holds 552 GLSL sources: 222 fragment, 148 compute, 109 includes, 40 vertex, 20 geometry, 9 mesh/task, and 4 tessellation. Three of them (`Common/MaterialTable.glsl` and the two `Graphics/BindlessMesh` stages) require bindless-texture or 64-bit integer extensions. The same tree has 2 Slang pilots under `FrontendPilots/`. The only other Slang source and all 10 WGSL sources belong to the separate runtime. Slang does not ingest desktop GLSL 4.6 unchanged, so the web tier's shaders must be ported, not only cooked.

- [x] **UR05.01** `impl` Inventory the shaders the web tier of `DefaultRenderPipeline` needs. Classify each as portable through Slang, needing a WGSL rewrite, or desktop-only. Record the list, grouped by pass, in a progress doc; it is the work list for UR05.07.
- [x] **UR05.02** `impl` Cook engine shaders to WGSL with the pinned Slang route and the shader artifact format (reusing `Tools/ShaderCooker`, the `ShaderCompileTarget.WebGPUWgsl` target, and artifact schema checks). Follow the [Slang cross-compile plan](../../design/scripting/slang-shader-cross-compile-plan.md). Depends on decision D7.
- [ ] **UR05.03** `impl` Extend the engine's material shader generation with a WGSL target, replacing the separate browser material generator. Support the engine's lit material model, not only unlit and Lambert.
- [ ] **UR05.04** `impl` Implement the WebGPU encoding of logical material and texture references with bounded bind groups, texture arrays for qualifying content, and material batching. No desktop bindless handle reaches WGSL.
- [ ] **UR05.05** `impl` Report web-unsupported shaders and material features at cook time with material, pass, source location, and reason.
- [ ] **UR05.06** `verify` Verify coordinate conventions (clip depth range, texture Y, winding, matrix layout, reversed-Z where the engine uses it) with known-value renders.
- [ ] **UR05.07** `impl` Port the hand-written web-tier shaders from the UR05.01 list by the route chosen in D7: depth and shadow casters, forward lit surfaces, sky and environment, tonemapping and the bounded post-process set, then UI and text. Implement coherent groups with narrow cook/compile checks and run known-value rendering at end-to-end milestones; each group's known-value result must be recorded before this coverage is closed, not before work on another group begins. Desktop GLSL behavior is unchanged.

**Acceptance:** engine materials cook to WGSL and render with correct interpretation; unsupported ones fail by name.

## UR06 — Render Pipeline Web Tier

- [ ] **UR06.01** `impl` Define the WebGPU capability profile of `DefaultRenderPipeline`: depth, forward lighting with the engine material model, directional/spot/point shadows within limits, sky and environment, alpha-masked and sorted transparency, HDR with tonemapping, a bounded post-process set, and engine UI.
- [ ] **UR06.02** `impl` Select passes from capabilities and report excluded passes explicitly; never discover unsupported passes by failing at runtime.
- [x] **UR06.03** `impl` Make `AdvancedRenderPipeline` report unsupported for WebGPU outputs through its existing `Available` policy (explicitly unbound, with a reason) and fail under `Required`.
- [x] **UR06.04** `impl` Resolve `CpuDirect` mesh submission for WebGPU. Later, and only after measurement, add GPU culling that writes fixed indexed slots with zero-instance culled draws, porting the branch's WGSL culling, BVH, and Hi-Z kernels into the engine's GPU scene path.
- [ ] **UR06.05** `impl` Run engine skinning and blendshapes through WebGPU compute (reusing the branch's WGSL skinning kernel as a canonical port) or CPU, with CPU/GPU parity checks.
- [ ] **UR06.06** `impl` Add mobile quality tiers to engine settings: backing resolution and DPR caps, shadow sizes and cadence, light counts, texture tiers, and post effects. Disabled effects must not allocate resources.
- [ ] **UR06.07** `verify` Render the same test worlds on OpenGL, Vulkan, and WebGPU; compare tolerant captures and document deliberate differences.

**Acceptance (U2):** a representative engine world renders in the browser through engine objects and the web tier, with documented differences from desktop.

## UR07 — Jolt Physics In The Browser

Depends on the [Jolt browser proof and default-promotion gates](native-subsystem-project-split-todo.md#jolt-browser-proof-and-default-promotion-gates) and decisions D3 and D4. The desktop Jolt module already exists.

- [x] **UR07.01** `owner` Productize the `joltc` Emscripten archive build, pinned to the runtime pack's Emscripten version. Ship it as the Jolt leaf's `browser-wasm` native asset, with a `.props` file that adds the `NativeFileReference`.
- [x] **UR07.02** `impl` Install the Jolt module in the browser composition with single-threaded job execution and a truthful capability report.
- [ ] **UR07.03** `verify` Compare desktop and browser results on matched scenes. If the approved native supply enables cross-platform determinism (decision D10), match state hashes; otherwise match within documented tolerances.
- [x] **UR07.04** `impl` Fail browser publishing of worlds that require PhysX-only features with the component path and feature name.
- [ ] **UR07.05** `verify` Measure physics step time on the reference devices and set budgets.
- [x] **UR07.06** `impl` Make the Jolt leaf buildable for the browser. `XREngine.Runtime.Physics.Jolt` targets `net10.0-windows7.0` and references `JoltPhysicsSharp` 2.22.0, whose native package has no `browser-wasm` asset. Retarget it to `net10.0`, keep desktop native resolution working, and admit the project and package to the portable policy files.
- [x] **UR07.07** `impl` Add a reviewed native-asset allowance to the portability guard. [PortableRuntime.targets](../../../../Build/Portable/PortableRuntime.targets) rejects every `NativeFileReference`, native copy item, and resolved native runtime asset in a portable project, and the browser host is one. Allow named files per project and runtime identifier with a recorded reason, as the package policy does; do not disable the check. Update the [portable project rules](../../../developer-guides/runtime/portable-projects.md).

**Acceptance:** engine physics components behave the same on desktop and web through Jolt.

## UR08 — Audio In The Browser

- [x] **UR08.01** `impl` Implement `XREngine.Audio.WebAudio` against the audio contracts (sources, listener, spatialization, gain, looping, streaming), reusing `browser-audio.js`. The name follows the existing audio leaves (`XREngine.Audio.OpenAL`, `XREngine.Audio.NAudio`, `XREngine.Audio.SteamAudio`). Engine audio components are unchanged.
- [x] **UR08.02** `impl` Add gesture-driven activation, suspension, and resume. Gate simulation only when a world declares audio as required.
- [ ] **UR08.03** `verify` Validate cooked audio codecs on the browser matrix, including Safari.
- [x] **UR08.04** `impl` Report Steam Audio features as unsupported on web unless a WebAssembly build is separately evaluated and approved.

**Acceptance:** engine audio components play in the browser with the same authored data.

## UR09 — Input, UI, And Text

- [ ] **UR09.01** `impl` Implement a browser input leaf that feeds `XREngine.Input` devices from pointer, touch, keyboard, IME, wheel, and Gamepad API events, reusing `browser-input.js`. Player controllers and input mappings are unchanged.
- [ ] **UR09.02** `impl` Render engine UI through WebGPU with hit testing in the same coordinate convention as rendering.
- [x] **UR09.03** `impl` Cook glyph atlases with FreeType at cook time and render cooked fonts at runtime. Desktop shipping builds may use the same path.
- [ ] **UR09.04** `impl` Bridge text entry, IME, and accessibility to DOM elements.
- [x] **UR09.05** `impl` Express mobile touch controls (virtual sticks, buttons) as engine input mappings and UI, not page-specific script.

**Acceptance:** a user can play with touch, keyboard/mouse, or gamepad through the engine's own input and UI.

## UR10 — Game Code And Components

Depends on UR17: game code reaches the engine through the `Engine` facade.

- [x] **UR10.01** `impl` Make project templates and game projects target `net10.0` and reference only portable engine assemblies (plus leaf contracts where needed). The generated target framework is `net10.0-windows7.0` today, set in `XREngine.Editor/CodeManager.cs` and `XREngine.Editor/EditorProjectInitializer.cs`.
- [ ] **UR10.02** `impl` Report at build time which game-assembly references block browser publishing (desktop-only leaves or APIs), with type and member names.
- [x] **UR10.03** `impl` Link the project's game assemblies into the browser publish, with generated static registrations. Editor hot reload remains desktop-only. Done 2026-10-02: the complete production publisher generated the typed game bootstrap/static registrations and activated a browser bundle containing the canonical game WebCIL and bootstrap. See the [publisher evidence](../../progress/rendering/browser-project-publishing.md#same-assets-and-game-code); runtime gameplay acceptance remains separate.
- [x] **UR10.04** `impl` Keep editor-only code out of game builds, as today.
- [ ] **UR10.05** `verify` Load serialized game components, game modes, and pawns in the browser from the same assets as desktop.

The remaining items make the parity target portable. They apply to RollingBall unless decision D2 selects another sample. `Samples/RollingBall` currently targets `net10.0-windows7.0`, references `XREngine.Runtime.Bootstrap`, runs on PhysX (`XREngine.Scene.Physics.Physx`), uses VR components and the OpenVR action manifest, and ships its own cooked-world serializer.

- [x] **UR10.06** `impl` Move the sample's PhysX-specific calls onto the backend-neutral physics contracts so it runs on Jolt. Otherwise UR07.04 rejects it at publish.
- [x] **UR10.07** `impl` Separate the sample's VR rig, OpenVR manifest, and startup-settings generation from its gameplay code, so the gameplay assembly references only portable projects and the desktop build adds the VR part.
- [ ] **UR10.08** `impl` Bring the sample's cooked-world serializer under the platform cook target from UR03.03, or replace it with the engine's cooked format, so desktop and web load the same world asset.

**Acceptance:** the same compiled game code runs on desktop and in the browser.

## UR11 — Editor Browser Publishing On The Unified Path

- [x] **UR11.01** `impl` Replace `BrowserWorldPublishExporter` with this flow. Done 2026-10-02: the compiled production `BuildCurrentProjectSynchronously` chain completed all steps below and activated the canonical RollingBall bundle atomically through a portable Linux invocation. This is complete publisher execution, not Windows CLI or gameplay acceptance; see the [publisher evidence](../../progress/rendering/browser-project-publishing.md#same-assets-and-game-code).
  1. Cook the startup world's web closure.
  2. Build the game assemblies.
  3. Publish the browser host with the engine assemblies, game assemblies, and selected leaves.
  4. Write the launch descriptor.
  5. Activate the output atomically, reusing the branch's staging and rollback.
- [ ] **UR11.02** `impl` Before publishing, report web-unsupported components and features per world with scene paths and reasons. Unsupported required features block the publish; optional ones are listed.
- [x] **UR11.03** `impl` Ship a player shell page (canvas, loading progress, errors, audio unlock) separate from the developer harness page.
- [ ] **UR11.04** `impl` Keep the CLI entry point (`--build-project <project> --build-platform BrowserWebGPU`) and the editor Build Project action stable. Browser publishing must also work from a packaged editor, not only a source checkout.
- [ ] **UR11.05** `verify` Publish the parity target and a lit, textured, animated test world; play them in the browser and compare with desktop.

**Acceptance (U3):** a published project plays in the browser from the editor's normal build action.

## UR12 — Networked Browser Client

- [x] **UR12.01** `impl` Implement a WebSocket transport leaf over the transport contract (`INetworkTransportBackend` and `IDatagramTransport`, already separated from the desktop socket implementation in `XREngine.Runtime.Net.Sockets`), plus the matching server gateway.
- [x] **UR12.02** `impl` Carry over the mobile TODO's MW11 requirements: bounded queues, backpressure, reconnect/resync, suspension, authentication, origin and credential policy, and optional voice.
- [ ] **UR12.03** `verify` Validate against the real server path under throttling, disconnects, and app switching.

**Acceptance (U5):** the browser client joins and plays through the production server path.

## UR13 — Performance, Runtime Mode, And Size

- [ ] **UR13.01** `verify` Measure the interpreter with representative worlds (CPU per frame, startup, memory) early. Do not wait until feature completion.
- [ ] **UR13.02** `owner` Qualify AOT: generated serialization and registration metadata, trimming roots, build time, download size, runtime cost. Choose the shipping mode from measurements (decision D11).
- [ ] **UR13.03** `impl` Reduce download size through trimming, lazy assembly loading, and streamed content, against the [readiness budgets](../../progress/rendering/mobile-browser-readiness.md#devices-and-measurable-budgets).
- [ ] **UR13.04** `verify` Demonstrate steady-state frames with no recurring managed allocations in simulation, visibility, recording, and submission; record unavoidable browser/API allocations separately.
- [ ] **UR13.05** `verify` Bound linear-memory use across the .NET heap, native Jolt heap, staging, and retained content; test repeated world load and unload.

## UR14 — Lifecycle And Recovery

- [ ] **UR14.01** `verify` Carry over the mobile TODO's MW10 recovery and lifecycle matrix to the unified runtime: device loss and reconstruction, hide/show, lock/unlock, orientation during loading, canvas removal, repeated load/unload, and explicit restart.
- [x] **UR14.02** `impl` Reject stale asynchronous completions after teardown or device replacement.

## UR15 — CI, Hosting, And Evidence

- [x] **UR15.01** `impl` Extend the browser compile lane into a build and publish lane next to the Windows desktop lane. [portable-browser-compile.yml](../../../../.github/workflows/portable-browser-compile.yml) already runs [Test-PortableBrowserCompile.ps1](../../../../Tools/Test-PortableBrowserCompile.ps1) on `ubuntu-latest`; it cannot pass there until UR00.08 fixes the directory casing, and it should use the SDK pin from D5.
- [x] **UR15.02** `owner` Propose a headless-browser smoke harness (decision D9) that boots a cooked world and checks startup, rendering, and diagnostics.
- [x] **UR15.03** `impl` Document production hosting: HTTPS, MIME types, compression, immutable caching, bootstrap revalidation, and CSP. Add cross-origin isolation only if threads are adopted.
- [ ] **UR15.04** `verify` Run the physical-device matrix from the mobile TODO (MW00.06, MW12.07) with the evidence template in its completion section.
- [ ] **UR15.05** `impl` Publish user-facing build, publish, hosting, support-matrix, and troubleshooting docs after validation.

**Acceptance (U4, together with UR13, UR14, and UR16):** reproducible clean publish and recorded device evidence.

## UR16 — Retire The Separate Browser Runtime

After U3, subject to decision D12:

- [ ] **UR16.01** `impl` Remove `BrowserMeshComponent`, `BrowserSpinComponent`, `SceneBootComponent`, and the browser registration manifest and generator.
- [ ] **UR16.02** `impl` Remove `BrowserCooked*` scene and instance DTOs and the `BrowserSceneSession` content, motion, collision, and animation paths.
- [ ] **UR16.03** `impl` Remove `BrowserRenderPipeline`, its packet types, and `browser-render-pipeline.js` once the engine pipeline covers their cases.
- [ ] **UR16.04** `impl` Remove `BrowserCpuAnimator`, `BrowserCookedAnimationPlayer`, and `BrowserKinematicCharacter`.
- [ ] **UR16.05** `impl` Remove `BrowserWorldPublishExporter`, `Tools/BrowserContentCooker`'s browser-only recipe format (keeping the generalized packager), and the Python scripts (`Tools/Generate-BrowserRegistrations.py`, `Tools/Reports/audit_browser_dependencies.py`, `Tools/Shaders/cook_browser_shaders.py`).
- [ ] **UR16.06** `impl` Keep the developer harness page only if it still exercises the unified runtime; otherwise remove it.
- [ ] **UR16.07** `impl` Close or rewrite superseded mobile TODO rows and progress docs; move durable content into stable docs.

## Mobile TODO Carry-Over

| Mobile TODO item | Disposition on the unified path |
| --- | --- |
| MW00 (scope, budgets, devices) | Still applies; budgets and device matrix are reused. |
| MW01 (same-identity portable profiles) | Superseded by the implemented whole-project layout; remaining qualification is in the native subsystem integration checklist and UR01. |
| MW02 (browser host) | Host contracts and canvas code carry into UR02. |
| MW03 (batched bridge) | Carries into UR04; its acceptance evidence still applies. |
| MW04 (WGSL artifacts) | Artifact format and Slang route carry into UR05; the separate browser material generator is superseded. |
| MW05 (WebGPU resources and submission) | Carries into UR04; acceptance still applies. |
| MW06 (focused browser pipeline) | Superseded by UR06; the branch pipeline is reference only. |
| MW07 (cooked content) | Delivery code carries into UR03; browser-only DTOs are superseded. |
| MW08 (interaction and services) | JavaScript services carry into UR08 and UR09; bounded animation and collision profiles are superseded by engine components. |
| MW09 (compute/indirect) | WGSL kernels become engine pipeline ports in UR06. |
| MW10 (recovery, budgets) | Carries into UR13 and UR14. |
| MW11 (multiplayer) | Carries into UR12. |
| MW12 (publish, hosting, validation) | Carries into UR11 and UR15. |
| MW-D01–MW-D06 (deferred) | Still deferred. |

## Deferred

- **Browser-hosted editor:** a separate design after the runtime ships. It needs ImGui for WebAssembly with a WebGPU backend, a compilation service, server-side import, and a virtual file system.
- **WebXR:** a separate presentation and input leaf.
- **WebGL2:** a separate backend under the [browser renderer design](../../design/rendering/browser-wasm-renderer-design.md).
- **Multithreaded browser runtime:** after .NET WebAssembly threading with native relinking is reliable and measurements justify it.
- **PWA/offline packaging and native mobile applications.**

## Validation Matrix

| Area | Required evidence |
| --- | --- |
| Build | The build gate after every workstream; clean publish |
| Boot | Engine world lifecycle in the browser through the portable engine host; absent desktop services; named failures for missing required services |
| Rendering | OpenGL/Vulkan/WebGPU captures of the same worlds; orientation, depth, linear/sRGB, alpha, transparency, shadows, tonemapping; resize and device loss |
| Content | Same world assets; cooked variant selection; cold/warm cache; failure and cancellation cases |
| Physics | Desktop/browser Jolt parity on matched scenes; PhysX-only rejection |
| Audio/input/UI | Gesture activation, spatial audio, touch/keyboard/gamepad, IME, UI hit testing |
| Game code | Same game assemblies on desktop and web; blocked-reference report |
| Performance | Interpreter/AOT comparison; frame-time percentiles; allocations; download size; memory peaks on reference devices |
| Desktop preservation | OpenGL/Vulkan editor smokes and targeted tests after each shared-contract change |

## Definition Of Done

- The browser runs the engine's own worlds, components, renderer, pipelines, physics, audio, input, and UI from the same assemblies as desktop, through the same engine facade and host services.
- Editor browser publishing produces a playable site from the project's real assets and game code, with named reports for unsupported features.
- Only platform leaves and cooked GPU/format variants differ between desktop and browser.
- The separate browser runtime is removed, and superseded mobile TODO rows are closed.
- Device, performance, recovery, hosting, and CI evidence is recorded.
