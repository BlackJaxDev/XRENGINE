# Browser Jolt native supply proposal

Status: the recorded browser-only native supply was approved on 2026-09-30, and a browser-only managed source build correcting reviewed ABI and ownership errors was approved on 2026-10-01. The corrected managed source and current pinned native archives compile and statically relink successfully. The published WebAssembly module now passes native simulation, managed callbacks, raycast, teardown and foundation reinitialization under Node; the corrected Chromium run remains required. Desktop package supply and defaults remain unchanged.

Related: [native subsystem debugging and validation](../../todo/platform/native-subsystem-project-split-todo.md).

Keep the current desktop NuGet supply, `JoltPhysicsSharp` 2.22.0 and `JoltPhysics.Native` 1.1.0. Add a browser-only static archive build using the .NET-pinned Emscripten 3.1.56 toolchain. The checked-in [pin](../../../../Tools/Dependencies/JoltBrowser.lock.json) identifies `joltc` commit `886e088675bae3a086f8318c7803f8ee962c2f2c` and Jolt v5.6.0 commit `e77f175595e64cb44218cc9d9d56fc365ad0e36a`; both upstream projects publish MIT licensing. The installed desktop native package's recorded commit was unavailable from the upstream public repository when reviewed, so this browser source pin requires ABI qualification against the existing managed binding.

The [build script](../../../../Tools/Dependencies/Build-JoltBrowser.ps1) supports `-PlanOnly`, verifies existing .NET tool packs, uses clean exact Git checkouts, and writes all source/build/archive evidence under a reserved validation run. Its [owned CMake project](../../../../Tools/Dependencies/JoltBrowser/CMakeLists.txt) adds the clean pinned `joltc` checkout as a subdirectory and compiles the single-threaded entry point into that same static target with Jolt's target definitions. It installs no workloads and changes no desktop native supply. Execution that acquires/builds these new source dependencies requires the owner approval specified in AGENTS.md and the checklist.

## Binding and job-system audit

The reviewed managed binding uses static native callback targets and unmanaged function pointers. Its `LibraryImport` signatures include function-pointer parameters, including trace/assert and query callbacks. Browser linking must prove these signatures, native library resolution, and callback registration; source inspection alone is not acceptance. If the unchanged binding fails, own a narrow `joltc` binding using pointer-sized callback tokens and static `UnmanagedCallersOnly` targets for the spike, and document that it does not yet cover the complete engine backend.

The reviewed `joltc` thread-pool factory maps a non-positive thread count to `-1`, which requests the native default worker count. Passing zero does not establish a single-threaded browser path. An [owned C ABI wrapper](../../../../Tools/Dependencies/JoltBrowser/jolt-browser-single-thread.cpp) constructs `JobSystemSingleThreaded`; the existing `JPH_JobSystem_Destroy` deletes through its base class. The [throwaway browser harness](../../../../Tools/Dependencies/JoltBrowser/Spike/JoltBrowserSpike.csproj) uses the repository's WebAssembly SDK pattern, subclasses the public managed `JobSystem(nint)` constructor, and links both static archives through `NativeFileReference`. Its minimal browser host runs managed `Main` and displays success or failure. The managed source creates a world, drops a box for 120 fixed steps while checking each update result, casts a ray, and destroys any bodies that were created even after partial initialization. None of these source-level steps proves archive linkage, browser execution, callback compatibility, or absence of pthread worker creation.

The execution prerequisites are a reserved validation run, the pinned .NET 10 browser workload/tool packs, the exact clean upstream checkouts, successful static archives, and a browser runner. The owner approved acquiring and building the recorded sources on 2026-09-30; default promotion and cross-platform determinism remain separate decisions. The initial browser pass must inspect the produced WebAssembly linkage and runtime for any pthread creation, then verify managed callback registration and the full loop before treating the unchanged binding as compatible. A failed managed ABI or callback signature must produce a named failure and a separate thin-binding decision; the current harness does not silently replace JoltPhysicsSharp.

Cross-platform deterministic replay is not claimed. The desktop binary supply is not rebuilt with the same deterministic flags. That would require a separate approved all-platform native supply decision and matching validation.

## Native build evidence

The approved build completed on 2026-09-30 using clean checkouts of both exact source pins and Emscripten 3.1.56 from the installed SDK pack 10.0.2. It produced `libJolt.a` (4,771,436 bytes) and `libjoltc.a` (473,360 bytes), copied both MIT license notices, and recorded `native-build-pin.json`. The build preserves the desktop NuGet supply and does not install or modify workload packs.

Two build-script corrections were required on Windows: CMake source definitions use forward slashes, and the frozen cache points to the installed `emscripten/cache` directory containing `sysroot_install.stamp`. Evidence is under `Build/_AgentValidation/20260930-105523-unified-browser-runtime/temp-build/jolt-browser-native/`, with the successful command output in `logs/portable-jolt-native-build-3.log`. Archive creation alone does not qualify the managed ABI or prove absence of browser worker creation.

The same source and Emscripten pins built successfully on Linux with the approved SDK pack 10.0.12 on 2026-10-01. CMake 4.4.3 and Ninja 1.13.2 came from their official releases; CMake's archive checksum and signed checksum manifest, and Ninja's official release digest, were verified. The 145 native build steps produced `libJolt.a` (4,771,436 bytes, SHA-256 `b4bea8094ae79e882ef2c8c929aeff37cf175156cc25ce81f6bd4eeb33ed718f`) and `libjoltc.a` (473,360 bytes, SHA-256 `2e516d4104c6bbc71a868f58a8e2dd23fa02b019b9ccffbf5179084983437ae8`). Evidence: `Build/_AgentValidation/20261001-163800-webgpu-baseline/logs/jolt-native-build.log`. Emscripten's own Python entry points were permitted for this build; no custom Python build or editing helpers were used.

## Managed linkage findings

The initial untrimmed interpreter spike publish completed with no warnings/errors, but live `Foundation.Init` failed with `JPH_Init`. The generated native table admitted archive basename `libjoltc`, while the unchanged managed package requests `joltc`; its `libjoltc` table was empty. The SDK derives module admission from native archive basenames. The spike now stages a byte-identical `joltc.a` in its intermediate output and references that alias instead, preserving the original pinned archive and the desktop supply. Adding a second module alongside the empty `libjoltc` table is insufficient because the binding's resolver can select the empty table first.

The corrected module admission exposes a genuine conflict in the exact `JoltPhysicsSharp` 2.22.0 package. Its single-precision `JPH_ContactListener_SetProcs` import returns `void`; its double-precision overload returns `IntPtr`. Both target the same C symbol. The pinned native header and implementation define only a void-returning function. The SDK consequently emits both `void JPH_ContactListener_SetProcs(void *)` and `void * JPH_ContactListener_SetProcs(void *)`, and compiling the generated P/Invoke table fails. Selecting single precision at runtime cannot remove the conflicting import metadata from this untrimmed static-link build.

Read-only metadata/archive comparison found all 1,147 unique managed import names defined in the pinned native archives; this was the only conflicting managed return type found. Symbol presence does not prove the remaining ABI, callbacks or struct layouts. The conflict is not evidence of unsupported function-pointer parameters, and no generated-table edits, signature suppression, trimming workaround or alternate physics fallback were applied.

The owner selected the browser-only reviewed source-build route on 2026-10-01. It corrects only the erroneous managed overload to `void`; it does not replace the binding with a narrow spike API. Default promotion and determinism remain undecided.

Evidence is under `Build/_AgentValidation/20260930-105523-unified-browser-runtime/`: `logs/portable-jolt-spike-publish.log` records the initial publish; `logs/portable-jolt-spike-publish-2.log` records the conflicting declarations after admission is corrected; `reports/portable-host/jolt-native-archive-audit.md` contains archive provenance and symbol evidence. Browser callbacks, stepping, raycast, teardown and absence of worker creation remain unqualified.

## Browser managed source supply

[Prepare-JoltBrowserManaged.ps1](../../../../Tools/Dependencies/Prepare-JoltBrowserManaged.ps1) verifies a clean checkout of JoltPhysicsSharp commit `77a5be2dd30d587c1981dfcaf15851f18041b39c` (version 2.22.0), checks the exact MIT license and original source SHA-256 hashes from the lock, and stages every managed source file beneath a reserved validation output. The return-type correction changes the double-precision `JPH_ContactListener_SetProcs` overload from `nint` to `void`. The separately pinned callback ABI patch preserves typed managed APIs while passing pointer-sized tokens across native import boundaries. A separately reviewed three-file ownership correction is described below. The pristine checkout is never patched. Exact original, patch, and corrected-source hashes and a per-file provenance manifest accompany the staged license.

The [owned managed-only project](../../../../Tools/Dependencies/JoltBrowser/Managed/JoltPhysicsSharp.Browser.csproj) compiles this staged source into the original JoltPhysicsSharp assembly identity without importing the upstream project, whose project references would bring desktop native packages. It checks the corrected interop, ownership files, license and every inventoried source hash before compilation. Its only warning exclusions cover existing upstream native-filled-field and nullable diagnostics. The Spike consumes this project and still stages the byte-identical `joltc.a` alias. The production desktop leaf continues using its original NuGet package.

The full MIT notice at the exact source commit was verified against SHA-256 `afdd2e799fae375a71ce7ff6addb35b77f064c19321593aa869a662c2dc213b1`. The staged build carries this notice. XRENGINE licensing is unchanged.

Example preparation and managed compilation (PowerShell):

```powershell
$run = 'Build/_AgentValidation/<reserved-run>'
pwsh Tools/Dependencies/Prepare-JoltBrowserManaged.ps1 -OutputDirectory "$run/temp-build/jolt-browser-managed"
$managedSources = (Resolve-Path "$run/temp-build/jolt-browser-managed/staged").Path
dotnet build Tools/Dependencies/JoltBrowser/Managed/JoltPhysicsSharp.Browser.csproj -c Release --artifacts-path "$run/temp-build/jolt-managed-artifacts" -p:JoltBrowserManagedSourceDirectory="$managedSources"
```

Pass the staged directory as `JoltBrowserManagedSourceDirectory` together with `JoltBrowserArchiveDirectory` when publishing the Spike. Use an absolute resolved source path so invocation location does not change its meaning. No workload is installed by source preparation or managed compilation.

On 2026-10-01 the managed-only Release build passed with zero warnings and errors. Evidence: `Build/_AgentValidation/20261001-163800-webgpu-baseline/logs/jolt-browser-managed-build.log`. This establishes managed compilation and the checked patch, not successful browser static linking or execution.

The native build script now discovers the current host RID rather than hardcoding Windows, and its plan-only mode reports missing tool packs without invoking the native compiler. Unix SDK packs use the host Python 3 runtime internally for Emscripten; the script does not install Python. The native pack location now follows the approved .NET 10.0.401 workload set at 10.0.12, which still contains Emscripten 3.1.56. The native Git pins are unchanged. Archives built with the current pack still require successful managed relinking and browser execution.

## Engine Backend Composition

The complete `XREngine.Runtime.Physics.Jolt` project targets `net10.0`. Desktop resolution retains `JoltPhysicsSharp` 2.22.0 from NuGet. The browser root explicitly sets `XREngineJoltBrowser=true` on its Jolt project reference, selecting the reviewed managed source project even when the SDK does not propagate the root runtime identifier; direct `browser-wasm` builds select the same supply. Browser capability advertisement is compiled only for that source supply. Browser scene initialization requires explicit caller-thread scheduling and the physics owner, then creates the owned native `JobSystemSingleThreaded` adapter. Desktop scene initialization retains `JobSystemThreadPool`. Failed initialization and normal destruction dispose scene, job, and collision-filter resources under the corrected ownership contract. Process-wide foundation initialization publishes success only after the native initializer succeeds, so a failure cannot poison subsequent attempts. Native release remains to be exercised through the browser runtime.

The browser executable imports [JoltBrowserNative.targets](../../../../Tools/Dependencies/JoltBrowser/JoltBrowserNative.targets), which admits only `joltc.a` and `libJolt.a`, rejects threaded WebAssembly, checks the archive provenance against the exact committed lock, and publishes the native license notices. The admitted `joltc.a` remains a byte-identical copy of `libjoltc.a`. This composition is explicit; unsupported or missing native modules fail rather than selecting a substitute solver.

## Current Static Link And Execution Evidence

The untrimmed interpreter spike published successfully on 2026-10-01 with no compiler warnings or errors. Its generated P/Invoke table has a single `void JPH_ContactListener_SetProcs(void *)` declaration and one `joltc` module containing 1,148 entries, including the owned single-thread job export. The linked WebAssembly module is 4,803,349 bytes, declares unshared memory, and has no thread, pthread, worker, or proxy imports. These are static linkage findings, not runtime proof of callback behavior or absence of worker creation.

Evidence is under `Build/_AgentValidation/20261001-163800-webgpu-baseline/`: `logs/jolt-spike-publish-3.log`, `logs/jolt-spike-publish-4.log`, and `reports/jolt-wasm-linkage.json`. This environment's SDK out-of-process task hosts cannot create their IPC sockets, so local qualification used ignored MSBuild `UsingTask Override="true"` declarations to run the same pinned SDK tasks in-process; SDK files and permissions were unchanged. Normal CI does not depend on this local workaround.

Playwright with installed Chromium 151.0.7922.173 failed before loading the spike page because Chromium's process-singleton socket creation returned `EPERM`, including the reviewed escalation attempt. Evidence: `logs/jolt-spike-browser-2.log`. Create-world, 120 steps, managed contact callbacks, raycast, and teardown have not run in that browser environment. The spike now checks contact-added and contact-persisted callbacks as well as the original stepping/raycast loop.

### Callback Import ABI And Native Execution

Chromium CI run `36912281747` at checkpoint `36c4bde` reached the actual engine world lifecycle, but its standalone native spike aborted with Mono `aot-runtime-wasm.c:90` and `:188`. The initial spike emitted its first result only after the raycast, so that empty console did not establish an initialization failure. Loading the same published `wwwroot/_framework/dotnet.js` directly in Node 24.19.0 reproduced the identical WebAssembly function IDs and offsets without a browser or socket. Added stage output established that native foundation/world creation, 120 fixed steps, and managed contact callbacks succeeded; the failure occurred on entering the all-hit raycast.

The interpreter could not translate the direct function-pointer parameter in `JPH_NarrowPhaseQuery_CastRay3`. The browser-only [callback ABI patch](../../../../Tools/Dependencies/JoltBrowser/managed-callback-abi.patch) retains the original typed managed methods and unmanaged callback targets, but redirects each affected native call to an explicitly named private import taking `nint` for the callback. The cast preserves the native WebAssembly function-table token; the entry point, return marshalling, parameter order, pointer/reference arguments and calling convention stay unchanged. No delegate allocation, generated P/Invoke-table edit, native assertion suppression, trimming workaround or alternate solver is introduced.

The exact source mapping is confined to `JoltApi.cs` at managed commit `77a5be2dd30d587c1981dfcaf15851f18041b39c`:

- Two foundation trace/assert callback imports
- Two shape query result-callback imports
- Four broad-phase query collector imports
- Sixteen narrow-phase query imports: both precision overloads of raycast collector/result, point collector/result, shape-overlap collector/result and shape-cast collector/result
- One vehicle tire-impulse callback import

The 25 typed wrapper signatures remain available. Callback fields passed inside native procedure structs are untouched. The patch hash is `184291897af5e9949b32b7b2c45a682d24e7efcc9a866b487f0f8b65a2766afa`; the final corrected interop source hash is `cfd9c4fafc12ab609461c863e5ade61f833ddc9f16909471af10c863d84626ef`. Preparation verifies the original source, intermediate return-type correction, separate callback patch, final source, and existing lifetime patch before producing its per-file manifest. The native source/compiler pins and the lifetime patch remain unchanged. Re-running the native build performed no compile work and reproduced both previously recorded archive hashes.

The corrected published WebAssembly module completed 16 native world lifecycles across two calls to managed Main in one Node runtime. Each call initialized and shut down the native foundation. Every lifecycle completed 120 fixed steps, reported box Y=0.47999975, two ray hits, one contact-added callback and 31 contact-persisted callbacks, rejected reuse of transferred filters, and passed world/filter disposal checks. This establishes executed native stepping, raycast callbacks, teardown and reinitialization; it is not a browser GPU qualification or an allocation/leak measurement. A reflection audit found zero direct function-pointer parameters among 1,198 native import declarations and retained all 25 typed callback wrappers.

Evidence under `Build/_AgentValidation/20261001-163800-webgpu-baseline/`: `logs/jolt-spike-node-stages.log` records the failure immediately before the raycast; `logs/jolt-callback-node-after.log` records the successful repeated native execution; `logs/jolt-callback-spike-publish-final.log` records the final publish; `reports/jolt-callback-import-metadata.json` records the assembly audit. The ignored `scratch/run-jolt-spike-node.mjs` runner takes the published framework loader path and runs two complete init/shutdown passes. The corrected Chromium CI lifecycle gate remains required.

### Body Property ABI Qualification

The canonical cooked RollingBall world exposed a second concrete ABI error after native scene and actor creation had succeeded. Its `DynamicRigidBodyComponent` applies lock flags through `MotionProperties.SetMassProperties`. The pinned managed declaration returned `float`, while the pinned native header declares `void`; the generated WebAssembly call signature therefore trapped with `null function or function signature mismatch`. Runtime checkpoints localized the failure to that exact call, after body creation, registration, transform binding, and actor properties had completed.

The browser-only [body property ABI patch](../../../../Tools/Dependencies/JoltBrowser/managed-body-properties-abi.patch) corrects this return to `void` and passes `BodyCreationSettings.SetMotionQuality`'s enum by value, matching the native scalar parameter instead of sending a pointer. Both existing public wrapper APIs remain unchanged. The patch SHA-256 is `8e70684c260476a72f88e705f8c7e1521dba704d0769495dd26189ec448e0809`; the intermediate body-property-corrected `JoltApi.cs` hash is `457ddcebf815a0d3f10a81d0c484a39ce1cba62d5b873ed734c478da002e4cae`. Preparation checks the prior callback-corrected source, this separate patch, and the final source before writing its per-file inventory. The supported native build reports no compilation work while refreshing provenance. Native source/compiler pins and desktop NuGet supply are unchanged.

The first uninstrumented canonical WebAssembly run passed native body creation and cached-property application after the correction, then reached RollingBall's authored camera setup. It exposed missing headless rendering factories and an empty canvas physics backend catalog, recorded in `logs/rollingball-browser-boot/node-abi-fix.log` and `node-canvas.log` under the existing validation run. After the shared browser composition supplied those capabilities, both headless and canvas-composed Node runs completed fresh start/step/stop cycles and failed-load/retry. Canvas composition in this Node harness does not initialize a GPU or qualify rendered pixels.

The adjacent pinned-header audit identified three imports outside the exercised RollingBall call path: `BodyCreationSettings.SetObjectLayer` passed a pointer instead of the native scalar layer; `BodyInterface.ActivateBodiesInAABox` declared a pointer return instead of `void`; and both precision overloads of `BodyInterface.ApplyBuoyancyImpulse` passed `BodyID` by reference instead of its native scalar value. The separately reviewed [body query ABI patch](../../../../Tools/Dependencies/JoltBrowser/managed-body-query-abi.patch) corrects those exact declarations while preserving the public wrapper APIs. Its SHA-256 is `95ca8de8f346f89aea6549b48a4b8dd77b08cd7a463e0f4dfbe418ed9ea92f3c`; the final corrected `JoltApi.cs` hash is `30098aba4ea2879271d3427a392452e2420b72703f259a0a6cfe8862a84a6dd5`. This has separate patch and intermediate-source checks in the supported preparation workflow. Focused runtime qualification remains separate from the canonical game's actor-startup result.

A disposable WebAssembly probe using the corrected public wrappers passed six native worlds across two foundation initialization/shutdown lifetimes under Node. Each world round-tripped object layer `4294901762` and `LinearCast` motion quality, applied mass properties under a native write lock, woke a sleeping body through area activation with managed filters, and applied a buoyancy impulse producing upward velocity `0.16350001`. Every world removed and destroyed its body before disposal. The publish emitted no warnings or errors. Evidence is `logs/rollingball-browser-boot/body-query-probe-{publish,node}.log` under the existing validation run; its source is `scratch/jolt-body-api-probe/Program.cs`. This validates the single-precision runtime path; the double-precision buoyancy declaration is corrected against the same native scalar contract but is not an executed double-precision solver qualification.

### Reviewed Managed Ownership Correction

The exact pinned managed source leaves `NativeObject.OwnsHandle` false in its parameterless constructor. The public `PhysicsSystem` constructor and the base `JobSystem` constructor use this path without changing ownership. Consequently `Dispose()` skips their `DisposeNative()` methods. For `PhysicsSystem`, that skips native world and listener destruction and release of its managed callback `GCHandle`.

The owned single-threaded job adapters set their inherited protected `OwnsHandle` property to true. `PhysicsSystem` is sealed, so the equivalent fix cannot be supplied through a normal derived engine adapter. The pinned native `JPH_PhysicsSystem_Create` stores all three filter pointers, and `JPH_PhysicsSystem_Destroy` deletes them. Both `ObjectLayerPairFilterMask` and `ObjectVsBroadPhaseLayerFilter` independently own their native pointers. Making the world owned therefore also requires explicit ownership transfer to prevent double frees.

The approved [managed-lifetime.patch](../../../../Tools/Dependencies/JoltBrowser/managed-lifetime.patch) has SHA-256 `b35fb8fee9489983d65885b3bdf168ed745ec17eb934a5e046475b19c1edf5c4`. It adds one-time filter transfer validation in `NativeObject`, makes newly allocated `BroadPhaseLayerInterface` objects owning until transfer, and makes `PhysicsSystem` own the created world and retain the transferred managed filter wrappers. Constructor failure disposes partially acquired listeners, callback userdata and the native world; zero handles are guarded. A second world cannot reuse transferred filters. The lock records exact original and patched hashes of all three files, and the build independently checks those expected patched hashes.

The corrected spike source repeats eight complete world lifecycles, including rejection of already-transferred filters, 120 steps, managed contact callbacks, raycast and disposal of world/filter wrappers. It published successfully in `logs/jolt-spike-publish-lifetime.log`. Those runtime checks remain ready for an eligible browser runner; compilation and successful return from managed `Dispose()` alone are not evidence of native teardown or leak-free repetition.

### Engine Actor Allocation Ownership

Canonical-world stop diagnostics subsequently showed that Jolt's active actor maps were not a complete ownership ledger: component deactivation called `RemoveActor`, removing each actor from the maps before scene destruction enumerated them. Both component body references survived with valid-looking IDs after the native physics system had been disposed. The engine leaf now tracks allocated actors separately from active registrations. Temporary removal retains the allocation and permits same-scene reactivation; explicit body or scene destruction releases its shape metadata, invalidates its ID, and clears the owning component's body reference. Reattaching a destroyed or foreign-system wrapper is rejected.

A five-cycle canonical canvas-composed Node run observed two allocated/native bodies before each stop, then zero allocated bodies, a null physics system, null component body references, and destroyed retained wrappers with invalid IDs after every stop. Failed-load/retry also passed. Evidence is `logs/rollingball-browser-boot/node-canvas-owned-actors.log`. Forced-GC managed heap and the engine's strong object-cache counts continued growing at nearly the earlier rate, so this repair establishes deterministic Jolt actor cleanup without claiming that the broader authored-graph retention issue is solved.

A disposable lifecycle probe in the same canonical WebAssembly runtime preserved body ID `8388608` across remove/re-add, kept its allocation ownership while detached, and released it when explicitly destroyed while detached. Allocated actors decreased from two to one, the owning component reference cleared, and re-adding the destroyed wrapper raised `ObjectDisposedException`. Final world stop released the remaining actor and left zero allocations. Evidence is `logs/rollingball-browser-boot/node-actor-lifecycle.log`; no production test files were added or changed.

## Kinematic Target Integration

Source review found that the engine's Jolt target setter immediately changed pose instead of deriving contact-producing velocity. It now publishes a value mailbox that the Jolt scene consumes with its actual fixed simulation delta through `BodyInterface.MoveKinematic`, before moving-ground controller queries. A subsequent step without another target clears the prior command's retained velocity. Non-null and null commands select kinematic and dynamic motion respectively, matching the existing PhysX wrapper's mode selection. Immediate `SetTransform` remains separate and cancels an unconsumed target. See [Physics API](../../../developer-guides/physics/physics-api.md#kinematic-motion-and-immediate-pose-changes) for the complete behavior.

The desktop Jolt leaf Release build passed with zero warnings and errors on 2026-10-01 after this correction. Evidence: `Build/_AgentValidation/20261001-163800-webgpu-baseline/logs/jolt-kinematic-leaf-build.log`. This is compile evidence only; live contact/friction comparison, browser physics execution, and solver parity remain unverified.

## Matched Native And WebAssembly Measurement

On 2026-10-01, the repaired spike passed real Chromium startup, stepping,
contacts, ray queries, and repeated teardown in
[Actions run 36918408996](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36918408996)
at `fe3a11974100a7946b6d9f768eb28646dadc1621`. Its browser worker list remained
empty. This supersedes the earlier runtime-unverified notes for that bounded
spike, not the engine/gameplay parity gates.

A separate matched-source measurement compared the unchanged desktop
`JoltPhysicsSharp` 2.22.0 / `JoltPhysics.Native` 1.1.0 Linux library with the
pinned, corrected browser build. A dynamic box with half-extents 0.5 m fell
from (0, 5, 0) onto a static floor with half-extents (10, 1, 10), centered at
(0, -1, 0). Both ran 120 steps of 1/60 s with gravity (0, -9.81, 0), one
collision step, and single-worker execution. The freshly published WebAssembly
ran under Node twice with fresh foundation initialization and shutdown.

Every sampled step's XYZ position, linear velocity, and cumulative contact
counts matched exactly in both WebAssembly repetitions. Maximum position and
velocity deltas were zero; contact-added began at step 59 and contact-persisted
at step 60. Final counts were one added and 31 persisted contacts. The downward
ray from (0, 10, 0), with displacement (0, -20, 0), hit the box at fraction
0.45100003 and the floor at 0.5 in each execution.

The disposable measurement source hash was
`4ac77de9ad29ee7121dac65356d9796872af3dbc81dc812be46ee9ee4d7baa85`.
Evidence lives under `Build/_AgentValidation/20261001-163800-webgpu-baseline/`:
`reports/jolt-native-wasm-parity.json`, `scratch/jolt-parity/Program.cs`, and
`logs/jolt-parity-{native,wasm-node,browser-publish}.log`. This establishes a
small matched-scene result, not universal determinism, Chromium state-stream
comparison, moving-platform/contact-friction parity, or RollingBall gameplay
acceptance. The desktop default remains unchanged.

## Acceptance before default promotion

Confirm Jolt as primary after the desktop parity gates and browser create-world, drop-box, 120 fixed steps, raycast, and destruction loop pass. Preserve saved backend selections. Keep PhysX desktop-only and Jitter experimental/opt-in. Box3D comparison remains optional and adds no dependency unless separately approved.

Reviewed upstream sources: [joltc build](https://github.com/amerkoleci/joltc/blob/886e088675bae3a086f8318c7803f8ee962c2f2c/CMakeLists.txt), [joltc job-system implementation](https://github.com/amerkoleci/joltc/blob/886e088675bae3a086f8318c7803f8ee962c2f2c/src/joltc.cpp), [joltc license](https://github.com/amerkoleci/joltc/blob/886e088675bae3a086f8318c7803f8ee962c2f2c/LICENSE), [Jolt single-threaded job system](https://github.com/jrouwe/JoltPhysics/blob/e77f175595e64cb44218cc9d9d56fc365ad0e36a/Jolt/Core/JobSystemSingleThreaded.h), [managed job-system base](https://github.com/amerkoleci/JoltPhysicsSharp/blob/77a5be2dd30d587c1981dfcaf15851f18041b39c/src/JoltPhysicsSharp/JobSystem.cs), [managed interop](https://github.com/amerkoleci/JoltPhysicsSharp/blob/77a5be2dd30d587c1981dfcaf15851f18041b39c/src/JoltPhysicsSharp/JoltApi.cs).
