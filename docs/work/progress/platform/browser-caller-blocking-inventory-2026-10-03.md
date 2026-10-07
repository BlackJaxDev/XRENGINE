# Browser caller-thread blocking inventory

Updated: 2026-10-07. This is a source inventory for the shared browser compile closure, not a live browser acceptance result. The caller-thread scheduler and physics-chain changes are recorded in [caller-thread physics-chain scheduling](browser-physics-chain-scheduling-2026-10-03.md). The new committed-source scan below updates the lexical inventory. The later sections retain their dated call-path and validation evidence.

## Committed-source refresh (2026-10-07)

The scan is pinned to [commit 4422849b](https://github.com/BlackJaxDev/XRENGINE/commit/4422849bde5585b0ca30060134d4f06060af1b48).
It uses the 19 project roots in that commit's `Build/Portable/PortableProjects.tsv`
and 5,243 tracked C# paths from `git ls-tree -r -z --name-only <commit>`.
Each expression is passed to `git grep -E <expression> <commit> -- <paths>`.
This reads committed blobs, including tracked paths hidden by ignore rules.
It excludes generated files and external source imports. The 17 tracked
RollingBall game scripts are a separate input scan and have zero matches in all
three columns. The project list differs from the older reference walk below;
the table is not a like-for-like measure of removed calls.

The three original expressions remain:

```regex
\.Wait\(|\.WaitOne\(|\.Result\b|GetAwaiter\(\)\.GetResult\(\)|Thread\.Sleep|\.WaitAll\(|\.WaitAny\(|\.Join\(
new Thread\(
Task\.Run\(|Parallel\.(For|ForEach|Invoke)|ThreadPool\.
```

Counts are matched files, not call sites. The alternation pipes above are
unescaped ERE operators; the requirement ledger escapes them only for its
Markdown table.

| Project | Wait/result/join | `new Thread` | Task/parallel/pool |
| --- | ---: | ---: | ---: |
| `XREngine.Extensions` | 2 | 0 | 4 |
| `XREngine.Data` | 10 | 0 | 6 |
| `XREngine.Audio` | 0 | 0 | 0 |
| `XREngine.Animation` | 1 | 0 | 0 |
| `XREngine.Input` | 0 | 0 | 0 |
| `XREngine.Modeling` | 0 | 0 | 0 |
| `XREngine.Runtime.Core` | 24 | 2 | 7 |
| `XREngine.Runtime.Rendering` | 42 | 2 | 9 |
| `XREngine.Runtime.Host` | 17 | 2 | 3 |
| `XREngine.Runtime.AudioIntegration` | 3 | 0 | 2 |
| `XREngine.Runtime.AnimationIntegration` | 3 | 0 | 0 |
| `XREngine.Runtime.InputIntegration` | 0 | 0 | 0 |
| `XREngine.Runtime.ModelingIntegration` | 0 | 0 | 0 |
| `XREngine.Runtime.Rendering.WebGPU` | 6 | 0 | 0 |
| `XREngine.Runtime.Platform.Browser` | 0 | 0 | 0 |
| `XREngine.Browser` | 1 | 0 | 0 |
| `XREngine.Audio.WebAudio` | 0 | 0 | 0 |
| `XREngine.Runtime.Net.WebSockets` | 0 | 0 | 0 |
| `XREngine.Runtime.Physics.Jolt` | 0 | 0 | 0 |

The thread expression finds six locations in six files, two each in Core,
Rendering, and Host:

- `XREngine.Runtime.Core/Execution/RenderWorkDomain.cs`
- `XREngine.Runtime.Core/Scene/Transforms/TransformPropagationWorkers.cs`
- `XREngine.Runtime.Rendering/Runtime/RuntimeEngine.Rendering.SecondaryContext.cs`
- `XREngine.Runtime.Rendering/Runtime/RuntimeRenderThreadHost.cs`
- `XREngine.Runtime.Host/Core/Time/EngineTimer.cs`
- `XREngine.Runtime.Host/Engine/Subclasses/Engine.CodeProfiler.cs`

Their placement remains open work. The wait expression finds 203 lines across
109 files, including 74 `string.Join` lines across 51 files. Other matches
include result properties and comments. The task/parallel/pool expression finds
64 lines across 31 files. These numbers do not establish browser reachability
or blocking.

A separate Core/Rendering/Host/Data scan finds 42 files and 203 lines with static
`File`/`Directory` member tokens or explicit `new FileStream`, `new FileInfo`, and
`new DirectoryInfo` constructors. Core has 16 files/71 lines, Rendering has
11/39, Host has 3/38, and Data has 12/55. It misses target-typed constructors
and includes comments. The remaining `AssetManager.Metadata.cs` match is its
`File.GetAttributes` comment; its physical operations use the host provider.
The earlier broad type-token scan at `2b11b0c2` found 18 Core, 22 Rendering,
four Host, and 21 Data files, including declarations and comments. That broader
scan is not refreshed here. These are different metrics from the historical
136 direct-call count. The direct and broad expressions are:

```regex
\b(File|Directory)\.[A-Za-z_][A-Za-z0-9_]*|\bnew[[:space:]]+(FileStream|FileInfo|DirectoryInfo)[[:space:]]*\(
\b(File|Directory|FileStream|FileInfo|DirectoryInfo)\b
```

Compared with the earlier pinned `2b11b0c2` scan, the manifest roots are unchanged
and eight C# paths were added. The wait expression decreased from 211 lines in
113 files to 203 in 109. Explicit thread construction decreased from ten locations
in nine files to six in six. General, auxiliary, and physics worker mechanisms
now reside outside the portable roots; metadata retry waiting also moved to its
host provider. Task/parallel/pool counts remain 31 files/64 lines. The direct
filesystem scan decreased from 43 files/231 lines to 42/203 after metadata and
GL submit-log file operations moved to host providers. This is placement
evidence, not runtime or performance acceptance.

The post-baseline native metadata extraction in
[commit 9e024e33](https://github.com/BlackJaxDev/XRENGINE/commit/9e024e331a638004f7be0e33a48a70ecd5f59524)
removed the shared frontend's target-typed `FileInfo` read. The direct expression
missed that constructor, so this move did not change its count. The
[runtime asset I/O record](browser-runtime-asset-io-boundaries-2026-10-03.md)
records its scope. Neither inventory closes the physical-placement requirements.

## Physics worker placement (2026-10-07)

The physics worker extraction keeps `PhysicsChainCpuWorkScheduler` in Core
and moves its thread construction, signals, completion wait, and joins into
Desktop. The public scheduler keeps its handle buffer, range claiming, metrics,
inline conditions, and execution/disposal guards. Each scheduler captures one
worker group. Browser/caller-thread and zero-worker construction do not access
the factory. Native positive-worker standalone consumers must install the
factory; an absent or invalid group fails instead of selecting inline fallback.
The [project ownership contract](../../../architecture/runtime/project-organization.md)
records this composition requirement and the constructor/caller-fault cleanup.
At that commit, general, auxiliary, render, transform, timer, and profiler worker
placement remained open. The next section records the general/auxiliary move.
This does not change the requirement count.

Core, Desktop platform, and Browser platform Release builds pass with zero
warnings and errors. The existing three `PhysicsChainCpuWorkSchedulerTests`
are selected by the normal Windows workflow without source or assertion edits.
At commit `d41ba319a8ba5a8634edaad0338cdee09456c79e`, all 41 selected tests passed
in [run 37576818620](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37576818620).
The three scheduler cases verify exactly-once work, deterministic range order,
and zero steady-state managed allocation on the calling thread. Artifact
`11464541092` is 7,382 bytes with SHA-256
`47b756ccf69bf56788cb16ad93e3c599b755737f8a22c596b568a616992c9838`.
The downloaded TRX contains all three passed cases. This does not test factory
failure injection, desktop/VR pacing, or browser worker execution.

## General and auxiliary worker placement (2026-10-07)

`XREngine.Runtime.Execution.Threads` now owns the existing general and auxiliary
worker implementations. It targets `net10.0`, references Core, and adds no
external package. It is outside the browser compile closure. Core keeps job
queues, priority and affinity policy, throttling, scheduler state, topology,
metrics, and the new domain contracts. The moved loops retain their existing
worker names, context setup, wake behavior, and dispatch algorithms. A small
cleanup also disposes the unused semaphore when a zero-worker domain first
shuts down; normal manager shutdown already disposed it on the later join call.

Native hosts install the worker factory before requesting threaded jobs.
Registration is atomic and does not overwrite a custom factory or start workers.
Each manager or scheduler captures one factory for both domains. Caller-thread
construction does not consult it. Native zero-general-worker scheduling still
uses its two auxiliary lanes. Desktop, the shared RenderBench scope, NUnit
setup, SoftwareVulkan, and the standalone smoke publisher now compose the
provider explicitly. The publisher retains its `net10.0` target. Full Desktop
bootstrap registers workers before asset-source replacement can invoke read
cancellation callbacks. The [project map](../../../architecture/runtime/project-organization.md)
records the standalone registration requirement.

The manager owns every returned domain before starting either. Constructor
rollback and ordinary shutdown attempt cancellation and both domain stop
requests before joins. One cooperative budget covers the remaining joins and
manager completion waits. Failed domains cannot cause early disposal of manager
synchronization. Collected errors are reported after the other cleanup attempts;
failed constructor cleanup preserves the startup error first in its aggregate.
Existing internal startup cleanup bounds remain, so the total constructor
failure path is not limited to a fixed two seconds.

Independent source/lifetime and composition reviews pass. Release builds of
Threads, Host, Desktop platform, Browser platform, the standalone publisher, and
SoftwareVulkan pass with zero warnings and errors. The local Bootstrap build
cannot complete because the tracked OpenVR.NET and OscCore-NET9 submodule sources
are absent.
The dependent local RenderBench build was not run. Exact commit
`102a3030667fec56b2e1f337f6f56462c2c87123` passed the Windows Editor build and
publication job in [run 37583510694](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37583510694).
Its unchanged filtered tests passed **76/76**: 10 `JobManagerTests`, 25
`EngineWorkSchedulerTests`, and the previous 41 ownership, physics, shader,
spill, and handoff cases. Artifact `11466686179` is 16,016 bytes with SHA-256
`faebf71dc08f0220cfff1109f6791327fbf02018ebd53614e3ef8813a2e99aea`.
The downloaded TRX has no failed, skipped, aborted, or inconclusive case.
The full run finished with seven successful jobs. Its existing UI animation-frame
check failed, and shadow ON exceeded its 45-second first-frame limit while
`engine-advanced-shade-native-depth-no-decals` remained pending for 37.5 seconds.
Neither failure supplies worker-placement or desktop/VR pacing acceptance.

The separate `XREngine.Benchmarks` executable also needs native worker
registration. Its `--gltf-phase0-report` path calls `ModelAssetImporter.Import`,
which reads `RuntimeModelImportServices.Current` before it selects the import
backend. The default service constructs a `JobManager` even when the report
disables asynchronous mesh processing. Normal benchmark startup now registers
the existing factory before CLI dispatch and references the worker leaf.
The `VulkanPerformanceToolOnly` build keeps its separate entry point and no
engine project references. Benchmark bodies and scheduler clocks do not change.
Its local tool-only Release build passed with zero warnings and errors. The
normal build stopped at offline restore because BenchmarkDotNet and
AssimpNetter are absent from the local package cache. The Windows validation
workflow now compiles both modes with separate output roots and retains the
build logs. Exact commit `87dffaeadfacec93c50515f7e5aca10fc3c2f8f9`
passed both Windows Release builds with zero warnings and errors in
[run 37607320065](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37607320065).
Artifact `11477607675` retains both build logs. Its ZIP is 421,383 bytes with
SHA-256 `6d410506e351f8540320ceb25763bd093a737106cf062bb867e249351dff4a55`.
The same commit passed the existing 80 filtered unit cases; artifact
`11477801078` is 17,273 bytes with SHA-256
`dac3b1f656ee4640d5af6c243643f542275ba60eb6b0e5baee743005799a4ac9`.
These checks verify compilation and the existing unit cases. They do not
exercise the glTF report or a benchmark workload; runtime import remains
unqualified.

The existing local Headless project also built with zero warnings and errors
and passed **144/144** selected animation, VR, routing, and filesystem unit
cases. The run used the clean published source and disabled internal tracing.
Its log does not embed a commit hash; source identity was checked separately.
No fixture or assertion changed. These results do not establish physical VR
pacing, whole-engine runtime acceptance, or browser feature acceptance.
Render-lane, transform, timer, profiler,
batch/pool, and other blocking-site placement remain open. No requirement state
or desktop/VR scheduling policy changed.

## Native logging placement (2026-10-07)

`XREngine.Runtime.Diagnostics.Native` now owns physical debug-log files,
directory discovery and retention, and the three existing ThreadPool dispatch
operations. The new `net10.0` project references only Core and adds no package.
Core keeps message formatting, preferences, filtering, console entries,
writer dictionaries, and their existing lock. The browser category and auxiliary
console paths do not require this native provider.

Native hosts must register logging before a native logging operation. A missing
provider gives a configuration error. Registration preserves a custom provider
and does not open files or start workers. Each queued operation captures its
provider. An active session and its writers remain on their original provider
until reset. Replacing or removing the registry entry does not dispose them;
removal rejects new native operations. The implementation preserves the three
dispatch points, their caller/worker timing and execution-context flow, file
formats, local timestamps, root fallback, and retention rules. Final directory
fallback remains best effort. Category file errors still propagate before
console insertion; auxiliary file errors remain best effort.

Desktop composition and the identified tool/test entry points register the
native leaf before logging can occur. The benchmark assembly also registers
when loaded by a benchmark child process. Its tool-only build excludes that
initializer and native reference. The approved dependency test adds the new
leaf and permits only its intended Desktop implementation edge. The
[project map](../../../architecture/runtime/project-organization.md) records
the startup and custom-host contract.

Independent source, lifetime, and composition review passed. Eight local Debug
builds passed with zero warnings and errors: Core/native leaf, Desktop,
BrowserRuntimeMetadataCooker, BrowserSmoke Publisher, Headless, SoftwareVulkan,
and both benchmark modes. RenderBench compilation is blocked by the absent
OpenVR.NET and OscCore-NET9 submodule projects. The focused full NUnit cases
share that blocked composition and have not run locally. Exact published
Windows Release CI and its focused test filter remain pending. These results
do not qualify live file output, shutdown draining, browser behavior, or
desktop/VR pacing. The lexical table above remains pinned to its stated older
commit; this placement change does not close the remaining inventory.

## Historical lexical refresh

The inventory includes the Browser executable and its direct/transitive engine project references. The project-reference walk yields 18 fixed projects plus the configured generated RollingBall game project: the 19-project portable closure. Counts are files with a match, not distinct blocking calls. The refreshed 22:43 UTC source scan uses the three search expressions in `UR02.03b` over Git-tracked C# paths plus new working-source files in recursively referenced project directories. Ordinary `rg` directory scans were insufficient: the repository `Assets/` ignore rule hides tracked `XREngine.Data/Core/Assets/XRAsset.cs` and its four default worker wrappers. Use `git grep` for tracked source, or feed explicit tracked paths to `rg`; do not let ignore rules define the runtime closure. A file can appear in multiple columns. Conditional project references are included lexically; the configurable game-project reference and externally supplied managed Jolt source require separate scans. These counts include in-progress source and do not qualify it.

| Project | Wait/result/join | `new Thread` | Task/parallel/pool |
| --- | ---: | ---: | ---: |
| `XREngine.Runtime.Core` | 29 | 4 | 8 |
| `XREngine.Runtime.Rendering` | 40 | 2 | 9 |
| `XREngine.Data` | 9 | 0 | 7 |
| `XREngine.Extensions` | 2 | 0 | 4 |
| `XREngine.Animation` | 1 | 0 | 0 |
| `XREngine.Audio`, `XREngine.Input` | 0 | 0 | 0 |
| `XREngine.Runtime.AnimationIntegration` | 3 | 0 | 0 |
| `XREngine.Runtime.AudioIntegration` | 3 | 0 | 2 |
| `XREngine.Runtime.InputIntegration` | 1 | 0 | 1 |
| Browser platform, WebAudio, Jolt adapter and WebSockets leaves | 0 | 0 | 0 |
| `XREngine.Runtime.Host` | 17 | 2 | 4 |
| `XREngine.Browser` | 1 | 0 | 0 |
| `XREngine.Runtime.Rendering.WebGPU` | 5 | 0 | 0 |

The expressions deliberately overcount. `string.Join` accounts for many wait/join matches, including the sole Browser executable match. Several `.Result` entries are named result fields/types. `XRBoolEvent` reads task results only after `Task.WhenAll`; the four preceding WebGPU `GetResult` sites execute only for tasks already faulted or canceled; the in-progress capture path adds another completion site requiring its own review. `RenderableMesh.Skinning` checks `IsCompleted` before observing results. The shared shader cache observes a completed task or selects its synchronous-capable desktop path.

The configured RollingBall game closure was scanned separately on 2026-10-04. Browser publication generates its game project from the 17 tracked C# scripts under `Samples/RollingBall/Assets`; no additional ignored or untracked scripts were found. Those files contain no matches for the blocking, explicit-thread, task-pool or direct-file expressions. `RollingBallGameBootstrap.CreateWorld` retains a synchronous-shaped asset call, but Browser startup first awaits the same startup world's source load. Its subsequent `AssetManager.Load` route is cache-only when synchronous work is unavailable and fails immediately if the object is missing. Concrete diagnostic and VR manifest file writes reside in `Samples/RollingBall.DesktopVR`, which the browser game project does not reference.

A separate timer search found no actual `System.Threading.Timer` or `System.Timers.Timer` constructors in the 18 shared project directories or the configured managed Jolt source. RollingBall's optional validation path has one 45-second, one-shot `System.Threading.Timer`; normal browser startup does not enable runtime-smoke validation. Success and watchdog expiry dispose it through `Finish`. An early failed `CompleteRuntimeSmoke` marks completion without explicitly disposing that timer; its eventual callback then observes completion. This is a bounded diagnostic-lifetime remainder, not a synchronous wait or proof that the browser creates a worker thread. Timer scheduling in the browser requires runtime evidence.

## Caller reachability and implemented boundaries

- Desktop worker domains, render-thread host, timer worker loop, secondary OpenGL context, code profiler, and VR calibration are not created by the browser host. Their waits and joins retain desktop topology. Caller `JobManager`, frame stepping, transform dirty-batch processing, and physics-chain scheduling already use their dedicated threadless branches.
- Event `InvokeAsync` runs its existing synchronous listeners on the browser caller without `Task.Run`. Its browser branch invokes the full listener snapshot even if an early listener faults, then propagates the first original fault after the listener profiling scopes close. `XRBoolEvent` all/any async variants retain their boolean results when no listener faults. Explicit `InvokeParallel` and `XRBoolEvent` parallel methods give desktop-worker diagnostics. The public parallel collection/string helpers likewise report the unavailable worker mode before their old broad catches. Desktop concurrent behavior is unchanged.
- Mesh index preparation and lazy BVH generation enqueue owned work for the next caller-job pump. Index tickets still validate geometry revision and publication ownership. Synchronous exact-readiness calls report pending before waiting. Skinned BVH production also uses the caller job pump, while retaining its existing task result and version check. Browser and native caller-thread BVH construction skip disk caching before resolving the file-system provider or cache root. The portable `BvhDiskCache` facade captures the installed `AssetFileSystemServices.Current` provider once per operation and uses its optional Rendering-owned `IBvhDiskCacheBackend` capability. Physical cache reads, writes, path creation and hashing now reside in `DesktopBvhDiskCache` in `XREngine.Runtime.Platform.Desktop`; `DesktopAssetFileSystem` forwards the capability. The existing `BVHT` magic, v2 schema/hash/path, index records, raw flat-node payload and temporary-file replacement behavior are preserved. Custom native file-system providers must implement this optional capability to retain BVH disk caching; providers without it return a cache miss or skip storing, and the default desktop provider retains disk caching. This extraction passed an exact normalized source comparison and targeted static checks for admission order, provider capture, portable I/O removal and unchanged callers/project references; managed build and runtime acceptance remain pending. It closes only the BVH cache placement slice of the broader I/O inventory.
- The point-shadow render-matrix path and neutral-pose preview traverse every affected descendant immediately in caller mode. Height-scale eye vertices and cold replication schema discovery execute the same calculations sequentially there. Desktop parallel settings and execution remain intact.
- Cross-batch mesh shader versions and the public versions snapshot remain hidden until their deferred root publication commits, including the interval after tentative map installation but before cache registration. The owning batch retains its construction access. Caller access reports shared preparation-pending status; WebGPU frame recording and authored indexed admission retain the pending frame and retry. The narrow production witness covers unpublished and commit-window access, commit and successful retry, aborted publication, injected failure and reconstruction.
- Browser diagnostics write to the browser console without queueing ThreadPool work or opening a synchronous file writer. Render-profile preparation retains its async task; its dedicated capture-thread operation has a precise browser diagnostic. Off-owner viewport enumeration and reentrant shared helper geometry report their pending owner instead of waiting on the caller.
- Convex decomposition's synchronous convenience wrapper is desktop-only and reports the async/cooked-collider requirements in a browser world. Its existing async API and cooked collider shape use remain available.

- Caller image-resize jobs retain independent source pixels until completion and cancel obsolete requests after source disposal/replacement, format changes or supersession. The [mipmap boundary](browser-mipmap-caller-resize-2026-10-03.md) passed 29 original and 16 independent review checks, including unchanged serialization records. It requires an installed image codec; browser composition currently has none.
- Authored Uber rebuild and debounce now schedule preparation/adoption through the caller job pump with serial, cancellation and revision guards. The [Uber source record](../rendering/browser-uber-caller-preparation-2026-10-03.md) records clean isolated compilation and focused runtime checks. Captured scheduler ownership prevents implicit worker creation after shutdown; immutable terminal-failure receipts are consumed by the material owner before readiness gating, preserving the shader and enabling a fresh request. The final probe covers rejection, queued cancellation, reentrancy, teardown, supersession, pipeline readiness and recovery.

## Remaining classification

The broader blocking-site item remains open. The reviewed [lower-level source boundary](browser-lower-asset-source-boundary-2026-10-03.md) now supplies the base `XRAsset` import/reload admission, captured asynchronous `TextFile` reads and both file-mapping guards without a Data-to-Core dependency. Its final isolated build and 136 ownership/admission checks pass. The [remaining path wrappers](browser-runtime-asset-wrapper-boundaries-2026-10-03.md) also pass their final owner-aware build and 24 production-method checks, including a serialization owner independent of the installed raw-read source. Those wrappers retain the admitted desktop project-directory behavior and avoid host-file probes during catalog reference resolution. [Gaussian and DDGI caller consumers](browser-gaussian-ddgi-asset-consumers-2026-10-04.md) now use those captured asynchronous reads and owner-side adoption; their isolated Rendering build and 40 focused owner-lifetime checks pass.

Browser admission and physical project placement are distinct. Several shared APIs still retain their synchronous desktop implementations behind explicit host/source admission, and cache-only runtime calls remain synchronous-shaped. The literal inventory requirement to move every remaining blocking operation to a desktop leaf, and the requirement to remove runtime-reachable synchronous wrappers, are therefore not closed merely by these guards. Desktop scheduling and admitted host behavior remain intact.

The 2026-10-05 BVH cache extraction passed independent source review and an exact normalized comparison of the moved implementation. That comparison accounts only for the desktop namespace/type, the supplied cache-root argument, and the admission checks retained in the portable facade; serialization, hashing, path construction and error handling match the previous implementation. The two `XRMesh.GenerateBVH` call sites, project references and tests are unchanged. Exact commit `a7f3dadf334bee75af2792055175bc687e14491b` then passed the portable/browser build and native-runtime checks, Windows Editor build/publication, RollingBall, RenderingParity, modular-pipeline and static-meshlet browser lanes in [run 37312528400](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37312528400). The run still failed its existing UI animation-frame and Advanced native-compute-preparation checks. This evidence does not close the whole physical-placement inventory or establish an independent desktop BVH cache-hit runtime result.

The [asynchronous HLOD/impostor capture implementation](browser-hlod-impostor-capture-boundary-2026-10-03.md) now passes its combined builds and independent lifetime review; its physical GPU acceptance remains open. `OctahedralImposterGenerator.Generate` still contains a desktop `ManualResetEventSlim.Wait` (`Rendering/Tools/OctahedralImposterGenerator.cs`), but `RequireSceneCaptureSupport` rejects Browser/caller execution before enqueueing or waiting; Browser HLOD calls the asynchronous path. The [exact cooked Uber contract](../rendering/browser-uber-base-material-contract-2026-10-04.md) has since passed its offline publishing/cooking and fresh hydration checks; live pixels and optional Uber features remain separate acceptance. The Gaussian/DDGI consumer build, runtime checks and independent ownership review pass as recorded above.

The image-resize scheduling fix does not install a browser image codec. `Mipmap2D.ResizeAsync`, `InterpolativeResizeAsync` and `AdaptiveResizeAsync` select caller jobs before their `Task.Run` desktop alternatives (`Objects/Textures/2D/Mipmap2D.cs`). `XRTexture2D` host-file import, preview and streaming-cache creation require host-file admission before their desktop jobs; the GPU streaming-cache cook's `ManualResetEventSlim.Wait` also requires that admission and an active OpenGL renderer, so it is not the WebGPU browser path. `UIVideoComponent` synchronously resolves a stream only after video GPU action creation; Browser's registered video service rejects that creation, and no browser FFmpeg backend is installed. Optional render-profile capture needs a browser implementation if browser profiling becomes a requirement. These are retained compatibility APIs or feature exclusions, not evidence of a reachable Browser wait in the named paths.

Current targeted review found no new browser-reachable blocking wait or explicit thread creation in Uber preparation, mip resize, HLOD/impostor capture, Gaussian/DDGI reads, the configured RollingBall startup, or the named media and texture-import paths. This is a call-path conclusion for those sites, not whole-page execution evidence or a complete proof for every lexical match. The literal `UR02.03b` demand to make *every* remaining shared-closure blocking/thread-creating site asynchronous, place it in a desktop leaf, or confine it to cook/editor remains a scope decision: guarded desktop compatibility implementations still live in portable assemblies, including the impostor wait and OpenGL texture cache cook. The Browser project can compile those implementations without reaching them. Moving or deleting those APIs requires an architectural choice beyond this inventory.

The [runtime I/O admission group](browser-runtime-io-admission-2026-10-04.md) adds source/epoch-owned asset response reads and guards missing-font host recovery, file capture/profiling/HiZ diagnostics, archive extraction and synchronous native-TLS startup. Its exact final Host build and production-method probes pass. The current Browser WebSocket composition does not subscribe the remote-job asset handler; the catalog-owner probe establishes the shared method's ownership contract, not live Browser remote-job delivery. The internal snapshot fallback remains classified under desktop edit/play transitions. A separate scan of the configured 177 managed Jolt source files found no lexical wait, thread/task-pool or direct-file candidates; it does not classify the native C++ implementation.

Validation here includes source, managed-contract, and Node-hosted browser-wasm interpreter evidence. Over the reviewed `8300fa354511000119b0e45f6e19c4493122ebff` tree with only this group's 22 source files overlaid, the native-Jolt Browser Release build, including WASM native link, passed with **0 warnings and 0 errors**. The isolated production witness passed twelve cross-batch version publication, commit-window isolation, same-batch access, retry, abort and failure checks. Its commit-window observation first reproduced tentative-version escape before the publication guard was corrected. A separate browser-wasm event witness passed early-throw/later-listener, original-fault, boolean-result, and profiling-disposal checks for `XREvent`, `XREvent<T>`, and `XRBoolEvent<T>` all/any async methods. This does not establish browser page startup, feature pixels, disposal or physical-device behavior; those still require live acceptance.
