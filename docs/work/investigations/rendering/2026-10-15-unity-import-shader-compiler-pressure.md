# Unity Import Shader Compiler Pressure

Last updated: 2026-10-15

## Problem

The user reported severe PC lag during the last two Aryia package runs. The
earlier run also disrupted Codex rendering. The user confirmed that the severe
lag started later during rendering, not during package extraction.

Related: [Unity package startup import](../assets/unity-package-startup-import.md).

## Result

The strongest evidence points to OpenGL shader compiler resource pressure and
driver stalls after scene publication. The earlier run logged eight compiler
out-of-memory messages. A timeout recovery path starts another compilation while
the abandoned driver work can remain alive. This path ran before the severe
stalls and Codex resource failures. It is a concrete engine defect and a likely
amplifier of the incident. The saved evidence does not prove the driver's exact
allocation failure or establish a new GPU reset.

The initial investigation used existing logs and did not launch an editor or
RenderDoc capture. The later controlled runtime check and CPU-only follow-up
are recorded below. The CPU follow-up reduced measured offline compiler memory;
OpenGL driver-link memory after that change remains unverified.

## Environment

- Windows, OpenGL 4.6, NVIDIA driver 616.92.
- RTX 4070 Laptop GPU, 8,188 MiB reported dedicated memory.
- Approximately 15.4 GiB usable system RAM.
- `AdvancedRenderPipeline`, `CpuDirect`, TSR, VSync override off.
- Main Aryia scene: 125 imported models and 35 materials, including 11 Pro-to-Uber
  conversions. Import completion does not mean shader compilation has completed.
- Both render and shared worker contexts requested implementation-maximum shader
  compiler threads.

## Timeline

Times below are Pacific time on 15 October 2026.

| Time | Evidence from the earlier run, PID 22996 |
|---|---|
| 12:30:40.995 | Main scene import completed. |
| 12:31:02.196 | `Combined:Shadow`, hash `9339211989564425242`, program 100 entered the shared-context lane. Source size was 307,728 bytes and 8,190 lines. |
| 12:34:03.546 | Program 100 was abandoned after the 180-second link timeout. |
| 12:34:08.389 | The same hash entered `DriverParallelSource` on the render context as program 230. |
| 12:35:42.372 | Render-thread `glFlush` took 7.621 seconds during a stuck-link nudge. |
| 12:35:42.625 | Codex desktop began logging `ERR_INSUFFICIENT_RESOURCES`. |
| 12:36:10.108 | A worker `glGetProgramiv(GL_LINK_STATUS)` call took 26.682 seconds. |
| 12:36:10–12:37:00 | Eight NVIDIA `C9999: out of memory - internal malloc failed` messages named Fishnet Top, Sheer Gloves, and Panties programs. Repeated messages are not proof of eight separate compiler jobs. |
| 12:36:24.037 | Worker `glCompileShader` took 13.381 seconds. |
| 12:36:48.895 | Windows Application Error recorded `codex-code-mode-host.exe`, exception `0x80000003`. This identifies the tool host, not a confirmed GPU-process crash. |
| 12:37:08 | The renderer shutdown was logged. |

The latest run, PID 29952, completed the main import at 13:02:01.619.
`Combined:Fishnet Top`, program 77, remained pending from 13:02:15 through at least
13:04:48. Its source contained a 318,172-byte fragment shader and a 4,869-byte
vertex shader. At 13:04:28, even a completion-status query took 5.277 seconds.
Mesh and texture jobs later reported queue waits near 126.8 seconds. These are
queue waits, not individual GPU execution durations. This run did not log the
same compiler out-of-memory message before termination.

## Source Mechanism

The relevant files are under
`XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/`:

- `Pipelines/GLProgramCompileLinkQueue.cs` has a one-large-source limit. A link
  timeout creates an abandoned result without terminating the driver's work.
  `TryGetReadyResult` then releases the in-flight and large-source reservations.
- `BackendObjects/Programs/GLRenderProgram.HazardDetection.cs`,
  `DeferRetryAfterSharedContextSourceTimeout`, explicitly leaves the old program
  allocated to avoid a blocking cleanup call. It can retry the same hash through
  driver-parallel compilation.
- `Pipelines/OpenGLShaderLinkBackendSelector.cs` selects that retry before the
  rule that directs large sources to the shared-context lane.
- `BackendObjects/Programs/GLRenderProgram.AsyncResults.cs` can call `glFlush`
  from the render thread to nudge a stalled driver-parallel link. The earlier
  run records a multi-second stall at this call.

Thus a queue timeout is treated as released capacity even though the driver may
still hold compiler memory. A retry can also move the same large source back to
the render context. This defeats the intended memory admission and thread
isolation for the timed-out work.

## Other Findings And Limits

- Both runs contain matcap shader errors such as undefined `_MatcapSlotParams0`
  and `_Matcap3Tex`. `Build/CommonAssets/Shaders/Uber/surface_extensions.glsl`
  constructs these names with token-pasting macros (`##`).
  `UberShaderVariantBuilder` removes static uniforms before replacing complete
  identifier references. Those replacements cannot see the pasted names.
  `GlslSnippetDeadCodeEliminator` also scans raw identifiers and misses the pasted
  sampler dependencies. This is a separate, confirmed source-generation defect.
  Explicit references for the four slots, or macro expansion before analysis,
  can preserve the dependencies and permit specialization.
- Ordinary failed shader hashes are blocked from immediate recompilation.
  A general same-hash compile retry loop was not established. The timeout retry
  above is a separate, observed path.
- Repeated `AdvancedMeshPreparation` messages do not establish a rebuild loop.
  Geometry revisions increase during source mesh writes. The sampled Body,
  Corset, and Jacket messages show different meshes or normal preparation
  progress. The large pending Fishnet program is dominated by fragment source,
  not generated blendshape vertex source.
- A process sample at 12:33:13 reported a 2,591 MB managed heap and 4,876 MB
  working set. This is one sample, not the peak system commit or driver memory.
- Imported-texture residency peaked near 231 MiB in the earlier window. That
  counter excludes other allocations and is not total VRAM usage. Its reported
  19.7 GB budget must not be read as device memory usage or capacity.
- No matching Display, NVIDIA driver, or resource-exhaustion System event was
  found in the inspected interval. Application log watchdog reports referenced
  old July–September dumps that Windows reported again. They are not evidence
  of new October resets. Direct access to `Windows/LiveKernelReports` was denied.
- The Codex resource errors and tool-host crash align with the engine stalls.
  That correlation supports shared resource pressure, but does not prove the
  exact cause of the reported UI rendering failure.

Microsoft defines [live dump code 0x141](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/bug-check-0x141---video-engine-timeout-detected)
as an unresponsive display engine. None of the accessible reports establishes a
new 0x141 event for these runs. An older [NVIDIA compiler memory report](https://forums.developer.nvidia.com/t/we-are-experiencing-like-a-memory-leak-using-the-glmaxshadercompilerthreadsarb/258442)
describes a similar parallel-compilation symptom; it does not prove the same
driver defect on this machine.

## Correction And Validation

The correction uses an explicit unfinished-native-work result. Queue reservations
remain held after timeout, consumer cancellation, and result consumption. A
process-wide stop prevents new source compilation after unfinished native work
or a compiler allocation failure. Both source lanes stop automatic retries.
Previously linked replacement programs remain available. Deferred cleanup uses
the source queue's completion state and does not poll abandoned handles.

The matcap implementation now references all four slot uniforms directly.
Static specialization and dependency pruning can see the same identifiers as
the GLSL compiler. Slot order and blend operations are unchanged.

Validation uses builds, CPU policy checks, and offline SPIR-V compilation.
The full avatar scene and GPU driver compilation are excluded from this check.
This validates the code and generated shader source, but cannot prove that the
NVIDIA driver will complete the large Fishnet shader within its memory budget.

Completed CPU checks:

- The coordinated build passed with no compiler warnings or errors.
- The selected policy, lifecycle, surface-feature, and representative SPIR-V
  checks reported 53 passes and two failures. All four representative shader
  profiles compiled. The failures are source-text contracts that still require
  the removed `DriverParallelSourceTimeouts` name and the old
  `ShouldPreferSharedContextForLargeSource(Hash, inputs)` call. No tracked tests
  were edited during this integration validation.
- A disposable console check passed for the process-fault selector, the usable
  binary-cache exception, and normal completed-failure reservation release.
  It also passed for retained normal and large-source reservations after a
  published unfinished result was consumed, cancelled before publication, or
  cancelled after publication. A new queue in the same process refused source
  admission. The check used simulated IDs and no GL context or GL calls.
- The same console check enabled all four matcap slots. Generated GLSL retained
  all four sampler declarations and replaced static parameter references with
  literals. Offline shaderc compilation produced SPIR-V successfully. GLSL
  property comments were excluded from the executable-identifier check.
- Final diff whitespace checks passed. No editor or GPU workload was launched.

The two stale source-text contracts need an authorized test update. CPU checks
do not prove live-driver timing, GPU memory use, or visual output. The source
fault policy limits additional work after a detected failure; it cannot cancel
or put a memory limit on the first native compiler job.

A later hardware check should use one material with process private bytes,
system commit, dedicated/shared GPU usage, and shader lifetime telemetry. A
compiler-thread limit can be compared, but is not yet a validated cure. Do not
infer total GPU time from the overlay's disabled timer.

## First-Compile Pressure Reduction

The follow-up reduces compiler input and concurrent native work before a timeout
occurs. It does not start an editor session or change system settings.

- All shared-context source queues use one process-wide permit. The permit covers
  shader compilation through confirmed program-link completion. Small links no
  longer defer polling and leave native jobs active behind the next compile.
  Unfinished native work retains the permit until process exit.
- Waiting workers do not hold the admission lock. They recheck job ownership,
  process faults, and shutdown before entering the compiler.
- Both unit-world compiler-thread defaults are `1`. The local unit-world settings
  also request `1`. The settings generator refreshed the schema and settings.
  `DriverParallel` mode now resolves a configured `0` to `1`, not the driver maximum.
  Worker initialization has the highest queue priority. Temporary suppression
  restores the configured hint instead of the driver maximum.
- Uber shadow and depth passes exclude the forward-lighting tail before dependency
  pruning. Coverage, alpha discard, and pass outputs remain in the shader.
- Static vector-component comparisons use float precision. Disabled material
  branches can then be removed before driver compilation. Unknown expressions,
  octal literals, and unsupported integer conversions remain dynamic.

The shared-source permit does not govern direct shader compilation or the
explicitly enabled render-thread source lane. The compiler-thread count is a
[driver hint](https://registry.khronos.org/OpenGL/extensions/ARB/ARB_parallel_shader_compile.txt),
not a hard thread or memory limit. One complex native compiler job can still use
substantial memory. These changes do not establish that a full cold avatar run
cannot stall the desktop.

CPU validation compares the same six generated profiles before and after the
source changes. Both versions include the explicit four-slot matcap correction.

| Profile | Source bytes before | Source bytes after | Reduction |
|---|---:|---:|---:|
| Maximal forward | 429,531 | 407,952 | 5.0% |
| Maximal directional shadow | 423,811 | 124,565 | 70.6% |
| Maximal point shadow | 423,591 | 124,039 | 70.7% |
| Maximal depth-normal | 421,113 | 121,561 | 71.1% |
| Layered matcap, slots disabled | 281,472 | 275,548 | 2.1% |
| Layered matcap, all four slots active | 281,472 | 281,292 | 0.1% |

All six profiles compiled to SPIR-V with the CPU compiler. The all-active matcap
profile retained all four sampler declarations. Source byte counts are not
measurements of driver memory. No GPU context or rendering workload was started.
The coordinated build passed with zero warnings and zero errors. A disposable
parser check passed eight comparison results and rejected ten unsupported
expressions, including float-rounding boundaries and ambiguous integer literals.
The existing disposable timeout, cancellation, reservation, and binary-cache
checks passed again with the new queue code. Its four-active-matcap shader also
compiled to SPIR-V.

The disposable evidence is in `reports/shader-memory-before`,
`reports/shader-memory-after`, and `logs/shader-memory-*.log` under the existing
task run. No tracked tests were added or changed.

The saved evidence is the two named engine sessions, the corresponding Windows
Application events, and the Codex desktop log ending at 12:39. The bounded
extraction is `unity-import-driver-stalls.json` in the task's disposable reports.

## Controlled Single-Material Runtime Check

The user authorized one bounded runtime check after closing other applications.
The first memory preflight had only about 2 GiB available RAM and held the launch.
Available RAM later rose above the selected 5 GiB launch threshold.

The isolated session was `aryia-single-material`. It used OpenGL,
`DefaultRenderPipeline`, one procedural box, one directional light without
shadows, no avatar hierarchy, no VR, no sky, and no anti-aliasing. The final run
used a 640 by 480 window and a 10 Hz render target. Engine program-binary caching
was disabled; the NVIDIA driver's own cache was not cleared. Both the render
context and source worker requested one compiler thread. The worker reported
`requested=1, reported=1, set=True`.

An external PowerShell process sampled physical RAM, commit headroom, editor
private bytes, and working set every 250 ms. It stopped only the named session
through the session manager. Selected stop limits were:

- Less than 3 GiB available physical RAM.
- Less than 4 GiB available commit.
- More than 2 GiB editor private bytes.
- More than 90 seconds of editor runtime.

The first observed editor sample also required 5 GiB physical headroom. These
are conservative test thresholds, not driver memory limits or safety guarantees.
Separate `nvidia-smi` sampling recorded total GPU memory use once per second.

Two setup problems were corrected before the material run. The first startup
was stopped manually before material attachment because the watchdog converted
a JSON UTC timestamp through a local-time string. The corrected identity check
passed checks for `DateTime`, `DateTimeOffset`, and timestamp strings. A later
box-only startup could not attach the source material because managed-session
retention removed its old extraction cache. That editor was stopped. The source
package was then extracted with the existing CPU importer into the task's own
scratch folder, outside managed-session retention. The box-only run reached
1.133 GiB sampled private memory and retained at least 4.557 GiB available RAM.

The material run started at 13:58:35 Pacific on 15 October 2026, PID 22952.
A disposable helper imported `Fishnet Top.mat` from the actual V1.3 package and
assigned its converted Uber material to the box's only source LOD. The import
reported `POIPRO0001` and 19 texture slots. It retained the existing partial
conversion behavior. The source shader and several texture GUIDs, including
`_ToonRamp`, were unavailable in this primary-package-only fixture. Thus this
check does not establish complete dependency conversion or visual parity.

The material's combined program 36 entered the shared source queue at
13:58:43.939. It contained a 318,172-byte fragment source and a 4,585-byte vertex
source. Both shader compile-status queries completed. The last logged native
setup call was the program binary-retrieval hint at 13:58:43.982. There is no
completed `glLinkProgram` timing or linked-result record before termination.
This places the remaining concern at native program linking. It does not
identify which allocator owned each private-memory allocation.

The external monitor stopped the process at 13:58:54, before the configured
20-second shader completion timeout or 90-second runtime cap:

| Measurement | Result |
|---|---:|
| Private memory near link start | 1.309 GiB |
| Highest sampled editor private memory | 2.118 GiB |
| Highest sampled editor working set | 1.939 GiB |
| Lowest sampled available physical RAM | 3.240 GiB |
| Lowest sampled commit headroom | 12.351 GiB |
| Highest sampled total GPU memory use | 681 MiB |
| Stop reason | Editor private bytes exceeded 2 GiB |

Sampling can miss short peaks. Private memory rose rapidly near the final
sample, so the observed value exceeded the trigger threshold before termination.
The session manager confirmed the editor stopped. Available physical RAM
recovered to about 5.65 GiB. The sampled System event interval contained no
Display/NVIDIA error; this is not proof that every desktop symptom was absent.

The guard worked, but the material link did not complete. The source reductions
and one-thread hint do not yet make this forward material safe for a full-avatar
cold run. No higher-limit retry, screenshot readback, or RenderDoc capture was
attempted after the memory stop. The next code investigation should reduce this
actual forward shader's link workload and separate managed from native memory
growth before another GPU run.

Evidence is in `reports/aryia-single-material/summary.json`, `memory.csv`,
`gpu.csv`, and `attach-result.json`, plus `logs/aryia-single-material/`, under the
existing task run. The helper, source fixture, settings override, and watchdog
are disposable scratch artifacts. The normal unit-world settings were not changed.


## Fishnet CPU-Only Compiler Reduction

The user requested further memory reduction and prohibited GPU validation.
A disposable CPU harness imported the same `Fishnet Top.mat`, prepared its Uber
variant, and resolved its fragment source. The baseline was exactly 318,172
bytes, matching the stopped runtime link. No editor, GL context, native driver
compiler, or RenderDoc capture was started for this follow-up.

### Findings and change

The generated shader already used static material values. There was no stale
canonical-source bypass. The retained canonical file path in the runtime log
was metadata, not evidence that specialization failed.

The source optimizer could not remove several unused lighting function trees.
A local Boolean hid the static lighting mode, a ternary retained both primary
shading functions, and early returns retained the later generic shading code.
The shader now uses direct property conditions and complete braced `if/else`
paths. Point and spot light construction stays inside its selected shading path.

Flat source lighting previously calculated a light map and then replaced it.
It now selects the flat result first. The flat primary path skips shadow
sampling when its authored weight is zero. Flat additive lighting skips ramp,
shadow-mask, and material-occlusion samples that its result does not consume.
These changes keep runtime property branches and the existing formulas.

Fishnet uses lighting mode 5 and additive type 3 with additive shadow weight 1.
Its point and spot shadow visibility remains required. Those shadows were not
removed or replaced with an unshadowed result.

### Bounded CPU measurements

Each offline compile ran in a separate process. A 250 ms external monitor
stopped only that process if private memory exceeded 2 GiB, available physical
RAM fell below 3 GiB, or elapsed time exceeded 120 seconds. All measurements
in the comparison table completed without a stop. Peaks are sampled process private bytes, not
managed-heap measurements or driver allocations.

| Measurement | Baseline | Final candidate |
|---|---:|---:|
| Resolved source bytes | 318,172 | 234,255 |
| Optimized SPIR-V bytes | 878,420 | 877,740 |
| SPIR-V instructions | 49,988 | 49,948 |
| Offline compile time | 17.587 s | 7.258 s |
| Sampled peak private memory | 1,414,242,304 bytes | 663,285,760 bytes |

The final whitespace cleanup exported 234,249 bytes. It differs from the compiled
candidate only by a removed blank line and the variant-hash comment. Source
checks confirm that point and spot shadow calls remain, while generic primary
shading and directional shadow calls are absent from this static material.

A repeated baseline produced the same SPIR-V size and instruction count. It
took 17.940 seconds and peaked at 1,372,188,672 private bytes. Source size fell
26.4%. The final sampled CPU compiler peak was 51.7% to 53.1% below the two
baseline runs. The optimized output changed by less than 0.1%. This demonstrates
lower compiler work on unused source, not a substantial reduction in the final shader's operations.
It does not establish lower OpenGL driver-link memory or a safe GPU link.

The six existing disposable profiles also compiled: maximal forward,
directional shadow, point shadow, depth-normal, layered matcap with slots off,
and all four matcap slots active. That process peaked at 1.899 GiB and retained
at least 3.738 GiB available RAM. No tracked test was added or changed.

### Remaining cost and next decision

The actual material also passed CPU compilation with ramp (mode 0), wrapped
(mode 2), and PBR (mode 6) overrides. Successful compiles used warnings-as-errors
and reported no warnings or errors. A runtime-changeable `_LightingMode` export retained 26
animated properties and 146 static properties. Its 321,047-byte source reached
the 2 GiB process limit after 23.2 seconds. The monitor stopped that CPU process
at a sampled 2,181,771,264 private bytes, with 3,726,454,784 bytes of available
RAM. It produced no SPIR-V. No higher-limit retry was made. This remains a
limitation for dynamic material variants.

The final optimized module still contains 593 switch instructions and 193 loop
merge instructions. The legacy point and spot shadow dispatch selects one of
four literal samplers by calling the full filter algorithm in each switch arm.
Both regular and Forward+ lighting call these shadow paths. This structure can
multiply the compiler's inlined control flow. It is a remaining workload cause;
these CPU results do not attribute a specific NVIDIA allocation to it.

A later reduction should first assess a common local-light evaluation loop.
It must preserve Forward+ order, negative-index termination, source-index
checks, and brightness clamping. Calling one helper from the two existing loops
can still produce two inlined copies. Moving sampler selection to individual
texture reads is broader: contact shadows, all filter modes, moments, texture
sizes, mip selection, and debug margins must remain correct. No such shadow
refactor was included in this bounded change.

The full-avatar cold run remains unvalidated. Keep GPU validation separate from
this result; do not increase the earlier memory limits to force a successful
link.

Disposable evidence is under the existing task run in `reports/fishnet-cpu-*`.
The `scratch/fishnet-cpu` harness contains the exact source baseline, CPU export,
SPIR-V instruction counter, and bounded process monitor. The rendering guide
records the static-dispatch rule for future shader changes.


## Shared Local-Light Shadow Evaluation

The user authorized the next code reduction with CPU-only validation and the
same memory limits. `calculateStylizedLocalLighting` previously evaluated
regular lights and Forward+ lights in separate loops. Four light helpers each
included a point or spot shadow call. After inlining, each shadow filter tree
could appear twice even though only one light-list mode ran.

The function now selects light data in one loop and then evaluates it at one
shared site. It contains one point-shadow call and one spot-shadow call. The
shadow sampling snippets, filter modes, sampler-slot switches, atlas fallback,
contact shadows, moments, mip selection, and debug output code are unchanged.

The selection branch preserves regular point-then-spot order and Forward+
visible-list order. Forward+ still stops at the first negative list index,
clamps brightness, and resolves its source index from `Params.w`. The regular
path keeps raw brightness. Both shadow calls use canonical light records and
the geometric receiver normal. Invalid Forward+ source indices still produce
unshadowed light; the near-zero-distance skip remains. There is no new
zero-attenuation skip. The spot attenuation multiplication order is unchanged.

### CPU comparison

The exact source baseline was saved before editing. The same actual Fishnet
material and shaderc configuration produced these results:

| Measurement | Before shared loop | After shared loop |
|---|---:|---:|
| Resolved GLSL bytes | 234,249 | 233,648 |
| Optimized SPIR-V bytes | 877,740 | 457,696 |
| SPIR-V instructions | 49,948 | 25,961 |
| Switch instructions | 593 | 301 |
| Loop merge instructions | 193 | 96 |
| Static load instructions | 1,064 | 632 |
| Offline compile time | 7.258 s | 3.235 s |
| Sampled peak private bytes | 663,285,760 | 329,195,520 |

The compiled instruction count fell 48.0%, and sampled CPU compiler memory fell
50.4% from the preceding static-pruning result. Source size fell only 0.3%.
This confirms that the shared evaluation site removes substantial inlined code,
rather than only shortening the input text. Instruction counts are static
module counts, not per-fragment execution or GPU performance measurements.

Review of optimized field loads found one extra position read for regular point
lights. Regular spot lights reread position, direction, and outer cutoff at the
shared shadow site. These are scalar/vector field loads, not whole light-record
copies. Forward+ canonical shadow loads remain the same. Static shadow-metadata
loads fell from 26 to 13 for binding 37 and from 24 to 12 for binding 38. The
small regular-path read increase is a runtime performance item to check later.

The process monitor requires at least 3 GiB free physical memory before launch.
It retains the 2 GiB process-private, 3 GiB free-physical, and 120-second stop
limits, sampled every 250 ms. The actual-material compile completed without a
stop and retained at least 5,095,763,968 bytes of available physical RAM.
No editor, GL context, native driver link, screenshot, or RenderDoc capture ran.

The six representative profiles and actual-material static modes 0, 2, and 6
compiled without warnings or errors. The profile process peaked at 944,771,072
private bytes. The generated source retains the checked PCF, cube, atlas,
legacy point/spot, and EVSM2/EVSM4 filter functions. The common local-light
function contains exactly one point-shadow call and one spot-shadow call.

The runtime-changeable lighting mode remains expensive. Its 320,446-byte source
crossed the 2 GiB private-memory limit after 19.7 seconds. The monitor stopped
the CPU process at a sampled 2,395,734,016 private bytes, with 3,824,644,096 bytes
of free physical RAM. Sampling permits an overshoot between checks. It produced
no SPIR-V, and no higher-limit retry was made. Static mode 6 uses the separate
PBR path and retains its previous 65,901-instruction output.

The four literal sampler slots still select complete shadow filters. This
change removes duplication between light-list modes; it does not redesign
sampler selection. That broader change remains separate. OpenGL driver-link
memory and rendered shadow parity remain unverified. A later visual check must
include mixed point/spot Forward+ tile boundaries: material texture reads now
occur after the light-type branches join, while shadow derivatives retain their
existing logical guards.

Evidence is in `reports/fishnet-shadow-*` under the existing task run. The exact
pre-edit shader is `scratch/fishnet-cpu/before-shadow-dedup.frag`. The rendering
guide records the common-loop rules. No tracked test code was added or changed.


## Dynamic Lighting Variant Compiler Memory

The user requested that the runtime-changeable lighting variant compile below
2 GiB, with the same CPU-only checks. The saved 320,446-byte baseline retained
three point-shadow call sites and three spot-shadow call sites: one stylized
site and two PBR sites. Its previous bounded compile stopped at the memory
limit and produced no SPIR-V. The baseline was exported again but not recompiled.

`calculateForwardLocalLighting` now selects regular or Forward+ lights in one
loop and evaluates each at a shared site. The four PBR light helpers were
removed. The PBR and stylized loops share `trySelectUberLocalLight`, which only
selects ordinary light values. Its `UberLocalLight` value contains no sampler,
shadow record, or buffer-layout contract. The lighting models retain separate
response formulas and accumulation loops.

PBR still passes color and diffuse intensity separately to the BRDF. Distance
attenuation stays inside the BRDF; spot cone factors remain outside it, with
shadow multiplication last. The specular-suppression setting, surface values,
view direction, geometric shadow normal, canonical shadow records, light order,
source-index rules, and brightness rules remain unchanged. No directional
shadow cache or change to main's directional/local/ambient composition was added.
The point and spot filter snippets remain unchanged.

### Initial CPU result

The dynamic variant now compiles successfully. It retains `_LightingMode` as a
runtime uniform and does not replace runtime mode switching with a fixed mode.

| Measurement | Result |
|---|---:|
| Resolved source bytes | 317,587 |
| Optimized SPIR-V bytes | 1,458,380 |
| SPIR-V instructions | 83,553 |
| Offline compile time | 20.714 s |
| Sampled peak private memory | 1,946,279,936 bytes (1.813 GiB) |
| Lowest available physical RAM | 3,981,193,216 bytes |
| Memory or time stop | None |

The previous dynamic compile was stopped, so it provides no complete output-size
or peak-memory baseline. The new result is below the 2 GiB process-private limit
with about 192 MiB of sampled margin. The monitor still requires 3 GiB free RAM
before launch and stops at 2 GiB private memory, below 3 GiB free RAM, or after
120 seconds. No GPU context, editor, driver link, or RenderDoc capture ran.

Code review found no blocking semantic defect. As with the stylized shared loop,
regular PBR selection can reread a few canonical light fields at the shadow site.
It does not repeat shadow filtering. GPU performance and pixel parity remain
separate runtime checks, including mixed point/spot Forward+ boundaries.

The static Fishnet material, static modes 0, 2, and 6, and the six representative
shader profiles all compiled successfully within the same limits. The static
Fishnet process peaked at 292,278,272 private bytes. The six-profile process
peaked at 965,156,864 private bytes. None of these runs reached a stop condition.

A second fresh-process dynamic compile produced the same 1,458,380-byte SPIR-V
module and 83,553 instructions. It took 24.493 seconds and peaked at
1,948,860,416 private bytes (1.815 GiB), leaving 189 MiB below the 2 GiB limit.
Both dynamic runs completed without a memory or time stop. All successful
compiles used warnings-as-errors and reported no warnings or errors. The dynamic
source retains `_LightingMode` as a uniform, switch cases 0 through 8, and two
executable shadow calls per local-light type: one PBR and one stylized call.
Static mode 6 now produces 41,791 instructions instead of 65,901.

The source-text test
`UberShaderForwardContractTests.UberShaderFragment_UsesSharedForwardLightingContracts`
still names the removed PBR helpers. Those assertions need to be updated to the
new call structure after the repository's feature-validation test-edit
restriction is lifted. No tracked test was added or modified in this CPU-only
integration check. The shader compile checks validate the actual generated code.

Evidence is under `reports/fishnet-dynamic-*` in the existing task run. The
pre-edit source snapshots are `scratch/fishnet-cpu/before-dynamic-memory-*`.

## Controlled Runtime Retry After Compiler Reductions

The user authorized another Fishnet Top runtime check. After the user closed
applications, the test passed the 5 GiB available-physical-memory launch check.
The isolated `aryia-single-material` session reused its existing binaries and
read the current shaders from `Build/CommonAssets`. The fixture used one box,
one imported Fishnet Top material, a 640 by 480 viewport, and a 10 Hz render
target. OpenGL reported one compiler thread and one shared-context worker.
Engine program binary caching was disabled.

An initial empty-scene launch stopped before material attachment because the
launcher checked a stale `$LASTEXITCODE` value after a successful PowerShell
script call. That editor was stopped. The corrected launcher checked the live
session process, started a fresh monitor, and attached the material successfully.
This setup retry did not change the shader or the memory limits.

The independent monitor retained the 2 GiB editor-private, 3 GiB available-RAM,
4 GiB available-commit, and 90-second stop limits. Its requested sample interval
was reduced from 250 ms to 50 ms. The first editor sample also required 5 GiB
available RAM. Sampling and process termination can still permit an overshoot;
this is not a hard allocation limit.

| Measurement | Material run result |
|---|---:|
| First sampled available physical RAM | 5,559,152,640 bytes |
| Peak sampled editor private memory | 2,256,961,536 bytes (2.102 GiB) |
| Lowest sampled available physical RAM | 3,458,666,496 bytes (3.221 GiB) |
| Lowest sampled available commit | 13,684,056,064 bytes (12.744 GiB) |
| Samples | 171 |
| Time from first to final sample | 11.103 s |
| Stop reason | Editor private memory exceeded 2 GiB |
| Fishnet link ready | No |

The runtime used the expected 234,497-byte fragment source and 4,585-byte vertex
source. Both compile-status queries completed. Program 36, `Combined:Fishnet
Top`, called `glLinkProgram` at 14:51:53.271 local time. That call returned in
1.576 ms with asynchronous completion polling enabled. No link-ready result
appeared before the memory stop at 14:51:56.305. The session manager recorded
the stopped state at 14:51:56.908. Both the editor and monitor exited.

These logs place the observed memory growth during pending driver linking.
They do not separate driver allocations from all other editor allocations.
The CPU-only compiler reductions therefore do not yet establish a driver-link
memory result below 2 GiB. The monitor stopped the run before the 20-second
link timeout; this run does not validate timeout recovery. No higher-limit
retry, full-avatar run, screenshot, or RenderDoc capture followed. Visual
validation remains incomplete. No System-log warning or error was returned
for the checked run interval, but that alone does not prove the absence of a
desktop or driver fault.

The next investigation should remain CPU-only and inspect the remaining
sampler-selection/filter expansion and the driver-link input. Keep point and
spot shadow behavior intact. Do not infer a safe runtime memory bound from
shaderc memory alone.

Evidence is under `reports/fishnet-runtime-retry/material-run` and
`logs/fishnet-runtime-retry` in the existing task run. The report contains the
memory samples, stop reason, attachment result, and summary. No engine code or
tracked tests changed for this runtime check.

## Shared Shadow Filter Dispatch After The Runtime Stop

The user requested a further shadow-filter and sampler-selection investigation
with CPU-only validation. No editor, OpenGL context, driver link, or RenderDoc
capture ran for this change. The CPU monitor retained its 2 GiB child-private,
3 GiB available-physical-memory, and 120-second limits, with a 3 GiB available
RAM preflight and 250 ms sampling. No limit was raised. No tracked tests changed.

### Findings And Retained Change

The legacy point and spot paths dispatch a complete shadow calculation through
four literal sampler slots. Within each selected sampler, several soft-shadow
modes called separate copies of the same Vogel filter. The cube dispatcher had
three reachable Vogel calls: explicit Vogel mode, CHSS, and Poisson/low-count
mode. The 2D dispatcher had two: explicit Vogel mode and CHSS. The point-atlas
cube dispatcher also had three.

`ShadowSampling.glsl` now selects the tap count and radius before one shared
Vogel call in the 2D and cube dispatchers. Separate penumbra helpers retain the
existing blocker search and math. They return a negative radius when no blocker
exists, and callers return fully lit visibility. The public CHSS wrappers keep
their original interfaces. `ForwardLighting.glsl` uses the same selection pattern
for point-atlas cube filtering.

Mode 3 still uses its own tap count before the ordinary filter-sample clamp.
Other modes retain the single-sample early return. Cube PCF, 2D fixed Poisson,
tent filtering, sample coordinates, depth comparisons, radius clamps, and
blocker sampling remain. Sampler-slot branches, texture operations, mip
selection, receiver-plane derivatives, contact shadows, fallback decisions,
and debug writes remain in their existing paths. Independent code review found
no semantic defect in this retained change.

### Rejected Candidates

- Removing the outer spot sampler switch reduced the static instruction count
  to 22,954, but moved receiver-plane derivatives and screen-space contact work
  across sampler-slot branches. It was reverted because this could change
  mixed-slot Forward+ behavior. It also did not show a CPU memory benefit in
  the initial sample.
- Applying the shared dispatch to array and linear-depth atlas-array helpers
  reduced the retained candidate from 24,070 to 24,010 instructions. That small
  gain did not justify the extra implementation scope, so it was reverted.
- Pure moment-evaluation extraction was reverted. The existing slot moment
  wrappers are not the live legacy-sampler path, so that extraction alone did
  not establish a compiler benefit.
- A large macro template and changes to snippet resolution were not introduced.
  They would require a wider shader-source and derivative review.

### CPU Results

The final baseline/candidate comparison used saved source inputs, the same CPU
harness, fresh processes, and sequential runs:

| Measurement | Before | After |
|---|---:|---:|
| Actual Fishnet resolved source bytes | 234,497 | 233,370 |
| Optimized SPIR-V bytes | 458,420 | 424,820 |
| Instructions | 25,996 | 24,070 |
| Loop merge instructions | 96 | 82 |
| Switch instructions | 302 | 276 |
| Compile time | 3.300 s | 3.170 s |
| Sampled peak private bytes | 385,716,224 | 322,596,864 |
| Lowest available physical RAM | 5,357,068,288 | 5,400,989,696 |

This paired check measured 7.4% fewer instructions and 16.4% lower CPU compiler
peak memory. Earlier samples of these same code versions had different memory
peaks, including a 292,278,272-byte baseline. Treat the paired result as one
bounded measurement, not a guaranteed memory reduction on each run.

The final dynamic variant retains runtime lighting-mode selection. It compiled
to 1,384,044 bytes and 79,269 instructions, compared with 1,458,380 bytes and
83,553 instructions before this change. It took 20.959 seconds and peaked at
1,907,560,448 private bytes (1.777 GiB), below the 2 GiB limit. The preceding
baseline repeat peaked at 1,948,860,416 bytes. Static lighting modes 0, 2, and 6
also compiled successfully; mode 6 now contains 39,649 instructions.

All six representative shader profiles passed in a serial run. Its peak private
memory was 904,380,416 bytes. The actual-material and lighting-mode SPIR-V
modules passed `spirv-val`. Successful harness runs reported no warnings or
errors. The existing compiler uses warnings-as-errors.

One profile run overlapped the end of the dynamic check because the coordinator
started it before the earlier command session had completed. Its monitor stopped
it when available RAM reached 3,170,373,632 bytes, below the 3 GiB limit. Its own
private memory was 890,253,312 bytes. The dynamic check completed within its
limits. The profile check was then repeated alone with unchanged limits and
passed, retaining at least 4,398,256,128 bytes of available RAM. This stopped
profile run is not counted as a pass. All validation child processes exited.

### Remaining Limits

These results establish a smaller compiled shader and a lower CPU compiler peak
in the paired comparison. They do not establish lower OpenGL driver-link memory
or a safe runtime launch. The last runtime result remains the 2 GiB stop.

Four sampler-specific filter copies remain. A further reduction must select
literal samplers at individual texture reads without duplicating the whole
algorithm. Keep implicit versus explicit LOD, texture size, contact sampling,
and receiver-plane derivatives explicit in that design. Visual parity remains
unverified, including adjacent fragments with different light slots or shadow
modes: merging mode paths can change implicit-derivative execution in divergent
control flow. Point and spot shadow behavior was preserved by source review
and CPU compilation, not by a rendered comparison.

Evidence is under `reports/fishnet-sampler-*` and
`logs/fishnet-sampler-*-export.log` in the existing task run. Saved snippet
baselines are in `scratch/fishnet-cpu/before-sampler-selection-*`. The final
comparison reports are `fishnet-sampler-baseline-repeat` and
`fishnet-sampler-final-static-repeat`; the final profile pass is
`fishnet-sampler-final-profiles-serial`.

## Shared Depth Filters Across Four Sampler Slots

The user requested removal of the remaining four-slot filter copies, with point
and spot shadows and derivative behavior retained. Validation stayed CPU-only.
The same 2 GiB child-private, 3 GiB available-RAM, and 120-second limits applied.
All compiler processes ran serially. No editor, graphics context, or driver link
ran. No tracked tests changed.

### Implementation And Review

`ForwardLighting.glsl` now prepares a plain point or spot receiver inside each
original literal-sampler branch. Preparation retains projection, bounds checks,
bias derivatives, contact sampling, moment shadows, and early debug writes.
It returns either completed visibility or the values needed for depth filtering.
One depth-filter call follows the slot switch. Depth debug sampling follows the
filter, with the same margin calculation. Invalid slots still return fully lit.

`ShadowFilterBodies.glsl` holds 14 shared depth-filter bodies. Native-sampler
wrappers remain in `ShadowSampling.glsl`. Integer-slot wrappers are in
`ForwardShadowSampling.glsl`. Only leaf texture and size reads select among
literal sampler indices 0 through 3. A token comparison confirmed that all 14
bodies retain their previous algorithms after adapter substitution. Both 2D
texture dimensions, cube normalization, far-distance scaling, loop guards,
blocker checks, and sample order remain unchanged.

Independent source review found no blocking defect. Moment, contact, array,
and atlas sampling were not moved into the shared template. Defined derivative
behavior is preserved. This does not establish identical pixels for previously
divergent paths where implicit derivatives were undefined. Visual parity is
still unverified.

### CPU Results

The actual material and lighting overrides 0, 2, 6, and runtime all compiled
with warnings treated as errors. All five SPIR-V modules passed `spirv-val`.
The six existing representative shader profiles also passed. Their process
peak was 975,114,240 private bytes; available RAM stayed above 4,350,918,656
bytes.

| Measurement | Previous source | Shared slot filters |
|---|---:|---:|
| Actual material source bytes | 233,370 | 244,033 |
| Actual material SPIR-V bytes | 424,820 | 403,908 |
| Actual material instructions | 24,070 | 22,318 |
| Actual material loop merges | 82 | 58 |
| Actual material sampled peak private bytes | 335,073,280 | 293,109,760 |
| Dynamic source bytes | 316,544 | 327,207 |
| Dynamic SPIR-V bytes | 1,384,044 | 1,342,172 |
| Dynamic instructions | 79,269 | 75,763 |
| Dynamic sampled peak private bytes | 1,907,560,448 | 1,918,033,920 |

The static baseline was compiled again in this session. The dynamic baseline
is the preceding session's measurement. The static candidate took 3.174 seconds;
the dynamic candidate took 22.950 seconds. Dynamic available RAM stayed above
3,657,543,680 bytes. The static instruction count fell 7.3%; the dynamic count
fell 4.4%. The static measured peak fell 12.5% against the new baseline sample.
Dynamic peak memory stayed near 1.79 GiB, so this change does not establish a
dynamic compiler memory reduction. Process peaks can vary between runs.

Compiled control-flow inspection confirmed that all point and spot sampler-array
accesses use constant indices 0 through 3. Representative static and dynamic
filter loops select a literal sampler per tap, join the four branches, and then
compare and accumulate once before the loop back-edge. The static point loop
is `%67037`, with sample join `%67105`; the static spot loop is `%46653`, with
join `%46749`. Dynamic examples are point `%145317` / `%145385` and spot
`%124947` / `%125043`. These IDs refer to this run's `fishnet.spvasm` files.
This confirms shared filter loops in those paths, not whole-program semantic
equivalence or driver behavior.

Exported source grew because the source trimmer retains shared macro bodies
and their helper references. The optimized output is smaller. This is evidence
that source bytes alone do not measure compiler cost.

The first static check stopped after 0.834 seconds when available RAM fell to
3,211,653,120 bytes. The child used only 141,639,680 private bytes at that point.
After the user closed applications and reported readiness, all later checks
passed under the unchanged limits. The stopped check is not counted as a pass.

### Why The Earlier Variant Used 1.78 GiB

The 1,907,560,448-byte value measured the whole CPU compiler process, not shader
storage or GPU memory. Its source was 316,544 bytes. Its final SPIR-V module was
1,384,044 bytes, or about 1.32 MiB. That module contained 79,269 instructions in
one function. The dynamic variant retains all runtime lighting choices and
their shadow paths. The actual static Fishnet material retains fewer paths.

The harness loads the .NET runtime and engine assemblies, prepares and rewrites
GLSL, then calls the packaged shaderc compiler. `VulkanShaderCompiler` selects
`OptimizationLevel.Performance`. The process counter includes managed memory,
native compiler allocations, temporary representations, and output buffers.
The one-function output is consistent with extensive inlining. Repeated filters
and many lighting branches increase the code that the compiler must process.
Final binary size does not include that temporary work.

The available measurement cannot assign memory to individual optimizer passes,
or separate glslang, SPIR-V optimization, and managed preparation. It therefore
does not prove that any one pass accounts for the full 1.78 GiB. A stage-specific
allocation trace would be needed for that attribution. CPU results also do not
establish the OpenGL driver's link memory or a safe runtime launch.

Evidence is under `reports/fishnet-four-slot-*` and matching export logs in the
existing task run. This change's saved source baselines are
`scratch/fishnet-cpu/before-four-slot-ForwardLighting.glsl` and
`scratch/fishnet-cpu/before-four-slot-ShadowSampling.glsl`.

## Dynamic Compiler Stage Measurement

The next investigation separated source preparation, native compilation, and
SPIR-V optimization. It kept the 2 GiB child-private, 3 GiB available-RAM, and
120-second limits. CPU measurements sampled at 25 ms to capture short stages.
Compiler processes ran serially. No editor, driver link, or GPU context ran.

### Stage Attribution

The scratch harness measured the existing `Prepare` and `CompilePrepared`
methods separately. It did not force garbage collection or change compiler
options. Both stages used the same dynamic source as the preceding check.

| Stage | Peak process private bytes | Peak managed bytes | Time |
|---|---:|---:|---:|
| Source preparation | 44,294,144 | 13,281,200 | 0.323 s |
| Native compile through `CompilePrepared` | 1,920,073,728 | 15,284,864 | 24.282 s |

A second probe called the exact packaged `xr_shaderc.dll` from a small Python
process. It used the saved rewritten source, GLSL fragment stage, entry point
`main`, Vulkan 1.4, SPIR-V 1.6, and warnings-as-errors. Performance optimization
peaked at 1,884,983,296 private bytes. Its output hash matched the managed
baseline exactly. With optimization disabled only for this diagnostic probe,
the native front end and SPIR-V generation peaked at 61,530,112 bytes and took
about 0.13 seconds. Its module passed validation. Production optimization
remained `Performance`.

This attributes the large peak to native optimization, rather than managed
source preparation or the GLSL front end. Independent peak values must not be
subtracted to calculate an exact optimizer allocation total.

### Pass Isolation

The installed SPIRV-Tools v2026.3 tool ran the Performance pass sequence in
bounded groups, then ran the middle passes in separate processes. This tool is
a different revision from the packaged compiler. Its results identify the
expensive algorithm; they do not measure an internal callback in the packaged
DLL.

| Diagnostic pass or group | Peak private bytes |
|---|---:|
| Inlining and initial control-flow cleanup | 289,103,872 |
| Initial scalar and local-memory cleanup | 328,740,864 |
| First SSA rewrite | 837,558,272 |
| First redundancy-elimination pass alone | 1,730,035,712 |

Every other isolated middle pass stayed below 180 million private bytes. The
[pinned redundancy-elimination implementation](https://github.com/KhronosGroup/SPIRV-Tools/blob/ef96ed763b43b59b33b31b362f09a02b729fa1c9/source/opt/redundancy_elimination.cpp)
copies a value map for each child in the dominator tree and retains those maps
in its work vector. The [Performance pipeline](https://github.com/KhronosGroup/SPIRV-Tools/blob/ef96ed763b43b59b33b31b362f09a02b729fa1c9/source/opt/optimizer.cpp)
first expands function calls. Large repeated shadow trees therefore increase
both the expanded graph and the state retained by redundancy elimination.
No dependency revision, compiler DLL, or production optimization level changed.

### Source Candidates

Reusing primary visibility in `applySourcePrimaryShading` was rejected for this
material. Fishnet's source shadow weight is zero, so later optimization already
removes those results. The candidate retained 75,763 instructions and the same
binary size. Its small peak difference did not establish a useful reduction.

The retained design uses one `calculateUberLocalLighting` loop for PBR and
stylized lighting. It shares light selection and point/spot shadow evaluation,
then selects the response. It preserves canonical receivers, shadow guards,
contact and moment paths, first-debug-writer order, attenuation grouping, and
the final directional/local/ambient addition trees. Direct mode conditions and
an explicit additive-off `if/else` permit static source removal. Directional
responses stay separate. Independent source review found no blocking defect.

The first shared-loop candidate compiled to 55,021 instructions and 967,180
SPIR-V bytes. Its native compile stage took 18.671 seconds and peaked at
1,824,813,056 private bytes. This is 27.4% fewer instructions and about 5% lower
peak memory than the measured baseline. This initial candidate used a local
Boolean mode argument; the final source uses direct mode checks for source
trimming.

A further candidate evaluates the local sum before the directional response
branch while retaining the same final arithmetic. The source review found no
side-effect or defined-derivative conflict. Its first compile was stopped when
available RAM fell below 3 GiB; that run is not a pass. A later source export
also hit the free-RAM guard. Compiler work paused for memory headroom. At that point, final
validation and the evaluation-order decision remained pending. The later shared
receiver checks below complete CPU validation with this order retained.

Evidence is under `reports/fishnet-stage-*`. Probe sources and the original
shader snapshot are in `scratch/fishnet-cpu/`; the snapshot is
`before-stage-UberShader.frag`. These files are disposable evidence, not build
dependencies.

### Resume Checks And Host Memory

On resume, available RAM was about 3.6 GiB. The final static shader passed the
CPU compiler and `spirv-val`: 403,844 SPIR-V bytes, 22,319 instructions, and a
404,029,440-byte sampled process peak. The six representative profiles passed
with a 1,000,005,632-byte process peak and at least 3,511,545,856 bytes available.
The current dynamic source passed the GLSL front-end syntax check at a
58,679,296-byte process peak. The prior shared-loop dynamic module also passed
`spirv-val`. These checks do not complete the current dynamic optimization run.

The user reported that no other application was open and requested host-memory
attribution. Read-only Windows counters showed about 15.4 GiB of usable physical
RAM and 4.0 GiB available during the snapshot. The Codex application process
group, whose executable is named `ChatGPT.exe`, used about 2.3 GiB of private
resident RAM across 18 processes. Its 12 renderer processes included individual
consumers of about 651, 394, and 264 MiB. The Codex backend used about 270 MiB;
Node helper processes used about 267 MiB. Windows Defender used about 296 MiB.
These are private resident bytes, not the sum of overlapping total working sets.

Windows nonpaged pool was about 1.3 billion bytes. Pool-tag sampling found
about 368 million bytes under `ismc`, 355 million under `EtwB`, and 257 million
under `ConT`. The owners of `ismc` and `ConT` were not established. The tracing
counters independently identified 256 MiB
allocated across `DtpSleepStudySession`, `DtpEnergyEstimationSession`,
`DtpKernalSession`, and `DtpMsbSession`, at 64 MiB each. No validation compiler
or engine editor process remained. No application, service, or trace session
was stopped. A single snapshot does not establish a memory leak.

The dominant user-process consumer is Codex itself. The process counters do not
map its renderer IDs to internal views. Available RAM remained below the roughly
5 GiB needed for the dynamic check plus the 3 GiB reserve. At that point the check remained
pending; its limits were unchanged. Resume evidence is under
`reports/fishnet-stage-resume-*` and `fishnet-stage-memory-pool-tags.json`.


## Shared Shadow Receivers Within Available RAM

The next source change kept production Performance optimization and the packaged
compiler unchanged. It did not disable shadows, reduce sample counts, replace
samplers, or change native dependencies. All validation stayed CPU-only.

Point shadow helpers now pass the guarded canonical record index instead of
copying `ForwardPointShadowData`. Each atlas tap reads the selected face from the
read-only buffer. The layout, face selection, fallback behavior, and all bounds
checks remain unchanged. This avoids passing the arrays for all six faces through
every helper. The isolated change still reached the available-RAM stop threshold;
it did not establish a complete compile peak.

The legacy point and spot receiver calculations now run once per selected light.
Sampler-size helpers, legacy contact loops, and moment fetches use literal slot
branches. Spot receiver-plane derivatives remain inside a slot switch after the
same distance and projected-bounds checks. Moment visibility and debug-margin
math run in shared pure helpers after the fetch. The separate debug fetch retains
its guard, normalization, and mip inputs. Screen-space contact is shared outside
the old outer slot branch. This preserves previously defined derivative behavior;
formerly undefined divergent sampling is not an equivalence target. Source review
found no material regression in these changes.

The shared point receiver alone also reached the RAM-reserve stop. With both
receivers shared, the complete dynamic compile passed:

| Measurement | Result |
|---|---:|
| Watchdog process private peak | 1,250,705,408 bytes (1.16 GiB) |
| Available RAM before launch | 4,424,491,008 bytes (4.12 GiB) |
| Minimum available RAM | 3,266,224,128 bytes (3.04 GiB) |
| Total process time | 13.405 s |
| Native compile stage time | 12.855 s |
| SPIR-V size | 835,764 bytes |
| SPIR-V instruction count | 48,093 |

Compared with the earlier 1,920,073,728-byte dynamic native peak, peak private
memory fell by about 35%. Compared with the first shared-local-loop result, it
fell by about 31%. These are process measurements, not a byte-exact allocation
attribution. The current source also retains the local-before-directional order
and direct authored-mode checks from the prior work.

The dynamic GLSL parse and SPIR-V validation passed. Explicit static lighting
modes 0, 2, and 6 each passed material export, production CPU compilation, and
SPIR-V validation. Their compile peaks were 819,326,976, 816,377,856, and
476,700,672 bytes respectively. The six representative profiles passed at a
912,756,736-byte peak: maximal forward, directional shadow, point shadow,
depth/normal, disabled matcaps, and four active matcaps.

All child processes ran serially under the unchanged limits: 2 GiB private
memory, 3 GiB available RAM, and 120 seconds, sampled every 25 ms. No editor,
OpenGL context, driver link, or GPU work ran. CPU success does not establish the
OpenGL driver's link peak or visual parity. Those runtime checks remain open.

Evidence is under `reports/fishnet-point-index-*`,
`reports/fishnet-point-receiver-*`, and `reports/fishnet-shared-receivers-*` in the
existing run. Source snapshots and transformation scripts are in
`scratch/fishnet-cpu/`. They are disposable evidence, not build dependencies.

### Runtime Retry Preflight After Shared Receivers

The user requested another controlled Fishnet Top runtime check. The saved
fixture remains one imported material on one box at 640 by 480 and 10 Hz, with
one driver compiler thread, one shared-context worker, and binary caching off.
The monitor process-identity self-check passed. No engine editor or shader
compiler process remained, and all named editor sessions were stopped.

Available physical RAM ranged from about 3.9 to 4.23 GiB. The saved runtime policy requires
5 GiB before launch and on the first editor sample. Its running stop limits
remain 2 GiB editor-private memory, 3 GiB available physical RAM, 4 GiB available
commit, and 90 seconds, with 50 ms sampling. The launch was held. No editor,
material attachment, or GPU operation ran. No application or service was stopped.
The lower CPU compile peak does not establish a lower driver-link peak, so it
does not justify reducing the runtime launch reserve.

Evidence is in `reports/fishnet-shared-receivers-runtime-preflight`. Runtime
validation remains pending until the unchanged launch preflight passes.


## Contact And Atlas Filter Sharing

The next reduction preserved every enabled Fishnet feature, authored property,
point/spot shadow path, filter mode, and sample-count limit. The packaged compiler
and Performance optimization remained unchanged. No tracked test changed.

The full legacy 2D and cube contact bodies now use shared body macros. Native
sampler and integer-slot overloads use the same math. Slot entry points reject
invalid slots, then run one ray march. Literal sampler branches remain at each
texture read. A normalized source comparison matched both former bodies after
only the texture-fetch substitution. The 2D direction, cube normalization,
far-plane scaling, fade, jitter, travel, and early guards are unchanged.

Four atlas filter dispatchers now share the final Vogel call between fixed-radius
and contact-hardening modes. These include normal and linear-depth 2D and array
atlases. Penumbra helpers retain the blocker search and return a negative sentinel
when no blocker exists. Public CHSS overloads keep their signatures and behavior.
Mode 3 still bypasses the filter-sample shortcut. The linear-depth one-tap Vogel
path remains an offset sample, not a center sample.

The normal-aware screen-space contact path now shares one body across 2D and
array textures. Texture adapters retain depth then normal sampling, decode, and
the clamped sample UV. Hit reconstruction still uses the unclamped UV. The layer
is resolved only for the array path. The 2D adapter has no mip-bias argument.
Depth-only and MSAA overloads are unchanged. Independent source review found no
material regression. The guarantees apply to previously defined derivatives.

### CPU Results

All runs used the existing 2 GiB child-private, 3 GiB available-RAM, and
120-second limits with 25 ms sampling. All compiler processes ran serially.

| Dynamic candidate | Peak process private bytes | SPIR-V instructions | Total process time |
|---|---:|---:|---:|
| Shared receivers, prior result | 1,250,705,408 | 48,093 | 13.405 s |
| Shared legacy contact loops | 1,246,973,952 | 46,160 | 12.587 s |
| Shared atlas Vogel dispatch | 1,243,975,680 | 45,404 | 13.209 s |
| Shared screen-space contact | 1,221,296,128 | 38,986 | 10.329 s |

The final dynamic module has 672,000 bytes. Instruction count fell about 19%,
and total compile-process time fell about 23%. Peak private memory fell only
about 2.4%, from 1.16 to 1.14 GiB. The first two small peak differences do not
establish substantial memory improvements by themselves. Final minimum available
RAM was 3,829,694,464 bytes. The final module passed `spirv-val`.

| Final Fishnet variant | Watchdog compile peak private bytes |
|---|---:|
| Authored properties | 191,139,840 |
| Static lighting mode 0 | 566,398,976 |
| Static lighting mode 2 | 564,305,920 |
| Static lighting mode 6 | 342,573,056 |

Each material export, compile, and SPIR-V validation passed. Static modes 0 and 2
used about 31% less compile memory than the prior shared-receiver check; mode 6
used about 28% less. The authored variant produced 201,996 SPIR-V bytes and
11,427 instructions. Its internal stage sampler recorded 191,913,984 private
bytes; the independent watchdog recorded the slightly lower peak in the table.

The six representative profiles also passed: maximal forward, directional
shadow, point shadow, depth/normal, disabled matcaps, and four active matcaps.
Their process peaked at 650,596,352 private bytes with at least 4,452,622,336
physical bytes available. Final material feature and property lists match the
prior shared-receiver export.

These CPU results do not establish the driver's link-memory peak. The runtime
launch requirement stays at 5 GiB available physical RAM. Its stop thresholds
remain 2 GiB editor-private, 3 GiB available physical RAM, 4 GiB available commit,
and 90 seconds. Shader reduction cannot change the idle-RAM preflight condition.
No runtime launch is permitted while that condition fails.

Evidence is under `reports/fishnet-contact-shared-*`,
`reports/fishnet-atlas-shared-*`, and `reports/fishnet-screen-contact-*` in the
existing run. The corresponding pre-edit snapshots and transformation scripts
are under `scratch/fishnet-cpu/`. They are disposable evidence only.

The final runtime preflight self-check passed, but available physical RAM was
4,544,745,472 bytes (4.23 GiB), below the unchanged 5 GiB launch requirement.
Available commit was 16,927,526,912 bytes. The editor was not launched. No GPU
operation ran. The preflight report is under
`reports/fishnet-screen-contact-runtime-preflight`.

### Return To The Full Aryia Package

The user requested a return to the Aryia package to check for PC lag. The local
unit-world settings already enable the Aryia unitypackage with its seven pinned
companion dependencies and partial import. The scene visibility policy remains:
Unity scenes checked, standalone prefab scenes unchecked. All managed editor
sessions were stopped. The available-memory check remained below the unchanged
5 GiB runtime launch requirement. No editor or GPU work started. Evidence is in
`reports/aryia-package-return-preflight` in the existing run.

### Full Aryia Package With A 4 GiB Launch Check

The user authorized a 4 GiB available-physical-memory launch requirement for
this attempt. Only the launch requirement changed. The stop limits remained
2 GiB editor-private memory, 3 GiB available physical memory, 4 GiB available
commit, and 90 seconds. The independent monitor sampled at a nominal 50 ms.
These sampled limits can have an overshoot between samples.

The run used the full package entry in `Assets/UnitTestingWorldSettings.jsonc`,
with seven companion dependencies, OpenGL, and `AdvancedRenderPipeline`.
The existing isolated session binaries contained the managed shader changes.
Shader sources came from the current repository. The session name remained
`aryia-single-material`, but this attempt did not use the single-material setup.

The editor started at 23:17:03 UTC on 15 October 2026. The monitor stopped the
owned session after about 8.5 seconds because available physical memory fell
below 3 GiB. There were 132 memory samples.

| Measurement | Result |
|---|---:|
| First sampled available physical memory | 4,850,429,952 bytes (4.52 GiB) |
| Minimum available physical memory | 3,202,342,912 bytes (2.98 GiB) |
| Peak sampled editor-private memory | 1,429,151,744 bytes (1.33 GiB) |
| Minimum available commit | 15,485,136,896 bytes (14.42 GiB) |

Startup completed world creation and some cached pipeline program uploads.
The last package progress record was `0% Resolving companion source 1 of 7`.
There is no evidence of Aryia scene publication or Aryia material compilation
in this attempt. It did not reach the later avatar-rendering condition that
previously caused lag. Driver-link memory after the shader changes and the lag
outcome remain unverified. The editor and monitor were confirmed stopped.

Evidence is under `reports/aryia-package-runtime-4g` and
`logs/aryia-package-runtime-4g` in the existing run. The launch environment and
monitor script are under `scratch/aryia-package-runtime`. No engine code,
root unit-world settings, or test code changed for this attempt.

### Startup And Package Preparation Memory

The next investigation kept the 4 GiB launch check, 2 GiB editor-private limit,
3 GiB available-RAM stop threshold, 4 GiB available-commit threshold, and
90-second timeout. The CPU preparation harness used the same limits. No test
code changed. An optional broker review failed at the provider spend limit;
local source review and measurements continued.

A CPU process ran the production dependency resolver and package preparation
code without creating a renderer. Downloading the seven dependencies completed
in 13.9 seconds with a sampled peak of 38,551,552 private bytes. Preparing the
primary package and combined project completed in 34.5 seconds with a sampled
peak of 58,363,904 private bytes. It found all 26 primary hierarchy entries.
This is source preparation, not scene conversion. Cumulative managed allocation
reached 2,191,699,560 bytes despite the small live process size.

`UnityPackageImportSource.CopyLimited` allocated an 81,920-byte buffer for every
pathname and extracted file. It now rents a 64 KiB transfer buffer and returns
it on success or failure. Pathname decoding also reads the existing buffer
without a second byte-array copy. Archive limits and cache validation remain.

Separate editor measurements sampled the managed heap every 100 ms. A bounded
EventPipe allocation trace used a 16 MiB event buffer. The two runs stopped
before Aryia conversion. Their dependency resolver rejected an incomplete cache
entry left by the earlier forced stop. The startup memory increase therefore
does not require avatar content. The trace is incomplete because the monitor
terminated the editor; its sampled allocation totals are diagnostic estimates.

The HDR sky texture decoder made one 128 MiB managed output array and roughly
128 MiB of temporary float arrays for a 4096 by 2048 image. `RuntimeImage.Dispose`
cleared the output reference, but collection could occur after other startup
allocations. This is temporary allocation pressure, not a demonstrated leak.
The Default world selects among five sky textures at random. The first heap
profile used `studio_small_09_4k.exr`; the allocation trace used
`warm_restaurant_4k.exr`. Comparisons must account for this source difference.

`MagickRuntimeImageCodec` now reads bounded native pixel spans through the safe
pixel collection. HDR output uses the existing owned payload pool, which frees
large native allocations when the returned image is disposed. Error paths
release both the lease and any transferred owner. Channel mapping, HDR range,
alpha, resolution, and pixel order remain unchanged. Encoded input goes directly
through the span decoder, including mapped and sliced memory. The ordinary
texture import path still reads the encoded file into a managed byte array.

Source review found no blocker in the pixel-span lifetime or output ownership.
Shader block-binding parsing is a separate Vulkan-only caller and was left
unchanged. The existing per-program uniform metadata cache also remains intact.

Evidence is in `reports/package-dependencies-baseline`,
`reports/package-extraction-baseline`, `reports/startup-memory-profile`, and
`reports/startup-allocation-trace` in the existing run. Runtime logs are copied
to `logs/startup-memory-profiles`.

The changed package preparation code completed with all 26 primary entries.
Cumulative managed allocation fell from 2,191,699,560 to 1,000,748,088 bytes,
about 54%. Sampled peak private memory fell from 58,363,904 to 56,320,000 bytes.
Elapsed time increased from 34.5 to 50.1 seconds in these separate cold-cache
extractions; this does not establish a speed improvement. Both runs stayed
within the memory limits. The candidate report is
`reports/package-extraction-after`.

Four isolated CPU probes decoded `warm_restaurant_4k.exr`. All produced the same
4096 by 2048 RGBA float output and SHA-256:
`0B7139910D9C119B11A0C74D0BE79A5581D68944F13BBAF1106A9884BD7114EB`.

| Input storage and codec | Managed decode allocation | Sampled peak process private bytes |
|---|---:|---:|
| Byte array, previous codec | 268,464,608 | 501,972,992 |
| Byte array, changed codec | 26,192 | 367,140,864 |
| Owned input, previous codec | 355,249,296 | 590,569,472 |
| Owned input, changed codec | 25,688 | 365,367,296 |

The changed codec released all 134,217,728 native output bytes when the image
was disposed. The owned encoded input also released all 86,785,163 bytes.
Both harness builds had zero warnings and errors. All probes passed the same
memory and time limits. Reports are under `reports/hdr-memory-*`.

Guarded builds initially stopped at the 3 GiB available-RAM threshold. The
changed codec and editor projects then built with zero warnings and errors.
The narrow build reused existing project references and kept analyzers enabled.
Its compiler processes used `DOTNET_GCConserveMemory=9`, two visible processors,
and a 768 MiB managed-heap cap. These settings applied only to the build.
The final build report is `reports/startup-memory-editor-build-managed-cap`.

The next full-package runtime attempt started at 23:47:43 UTC on 15 October
2026. It used the completed dependency cache from the CPU preparation check
and the changed codec. The independent monitor kept all existing limits.
There were 125 samples over 7.83 seconds before the RAM stop threshold fired.

| Measurement | Result |
|---|---:|
| First sampled available physical memory | 4,528,492,544 bytes (4.22 GiB) |
| Minimum available physical memory | 3,182,415,872 bytes (2.96 GiB) |
| Peak sampled editor-private memory | 988,291,072 bytes (0.92 GiB) |
| Minimum available commit | 14,650,429,440 bytes (13.64 GiB) |

Startup completed world creation and loaded cached Advanced pipeline programs.
No Aryia import progress or scene publication was logged before shutdown.
The managed-memory probe recorded about 343 MB of managed memory near shutdown;
this counter does not prove that all sampled objects remain reachable.
The runtime result is not a controlled before/after memory comparison because
the default sky selection and host memory load can differ between launches.
The CPU image comparison above provides the controlled codec measurement.

The editor and monitor were confirmed stopped. Evidence is in
`reports/aryia-startup-memory-after` and `logs/aryia-startup-memory-after`.
Source preparation now passes within the limits, but complete editor import
and the reported rendering lag remain unverified. The next measurement must
separate startup shader preparation, driver allocations, and host memory use.
Do not attribute the remaining physical-memory decrease to dependencies from
this run, or relax the stop limits to obtain a longer trace.

### Shader Source Preparation Allocations

The next CPU probe measured the existing compiled source preparation path for
`Advanced/Shading/ShadeNativeOpaque.comp`. It created no graphics context.
The cold include resolution allocated 39,120,152 managed bytes. Cold source
optimization allocated 109,228,624 bytes, and cold interface parsing allocated
19,812,048 bytes. Cached resolution and optimization each allocated 3,688 bytes;
cached interface access allocated 40 bytes. These are cumulative allocations,
not retained memory or driver-link memory. The process peaked at 112,914,432
private bytes and passed the unchanged guards.

Two source changes target the measured allocation paths:

- `ShaderSourceResolver.ExpandIncludesRecursive` scans lines as spans. Only
  include lines become temporary strings. CR, LF, CRLF, and final-newline
  normalization retain the previous `StringReader` behavior.
- `GlslSnippetDeadCodeEliminator.ClassifyChunk` and `AddIdentifiers` enumerate
  regex matches over spans. Ordinal alternate hash-set lookups avoid a match
  object and string for each repeated identifier. The regex, substring
  boundaries, declaration classification, and retained symbol set are unchanged.

Independent source review found no correctness blocker. The renderer and editor
compile-only build passed with zero warnings and errors. It omitted analyzers
and debug symbols to fit the build memory budget. Earlier analyzer-enabled
attempts exhausted the compiler heap cap. The successful build used one visible
processor and a 1 GiB compiler-managed-heap cap, inside the unchanged 2 GiB
process-tree stop and 3 GiB available-RAM stop. Peak process-tree private memory
was 1,100,460,032 bytes; minimum available RAM was 3,350,556,672 bytes.

The candidate CPU comparison was held before launch because available RAM was
below 4 GiB. No candidate allocation reduction or output-hash equality is yet
measured. No editor runtime started in this follow-up. Work stopped at the
user's request to wrap up. The next check is the guarded CPU comparison of
resolved and optimized source hashes and interface bindings for the Advanced
shader and the existing generated Fishnet shader, followed by a guarded editor
run only if those checks pass.

Source review also ruled out the schema builder's temporary shader-program
retention path for this startup: current standard stages do not register shader
factories. Repeated settings notifications can rebuild pipelines, but their
startup frequency was not measured. Neither path changed. An optional API
review failed at the configured provider spend limit; native review continued.

Evidence is under `reports/startup-shader-baseline`,
`reports/startup-shader-fishnet-baseline`, and
`reports/startup-source-allocation-build-ready` in the existing run. The CPU
probe and unchanged-limit guards are under `scratch/startup-shader-probe` and
`scratch/startup-memory-probe`.

### Resumed Shader Checks And Startup Audio Measurement

Work resumed on 16 October 2026. Both CPU shader comparisons passed under the
unchanged memory guards. Resolved source, optimized source, and interface
bindings matched the baseline for the Advanced and Fishnet shaders.

| Cold operation | Previous managed allocation | Changed managed allocation |
|---|---:|---:|
| Advanced include resolution | 39,120,152 bytes | 38,381,552 bytes |
| Advanced source optimization | 109,228,624 bytes | 51,044,392 bytes |
| Fishnet include resolution | 2,657,984 bytes | 1,843,840 bytes |
| Fishnet source optimization | 12,682,168 bytes | 7,517,112 bytes |

Interface parsing allocation was unchanged. These are cumulative managed
allocations during CPU source preparation. They do not measure driver linking.
The changed Advanced probe peaked at 91,787,264 private bytes. Both probes
completed without a guard stop. Evidence is in `reports/startup-shader-after`
and `reports/startup-shader-fishnet-after` in the existing validation run.

The next full Aryia startup began at 17:26:22 UTC. All seven companion sources
became ready, but scene conversion did not complete. The monitor stopped the
editor after 10.14 seconds when available RAM crossed the 3 GiB reserve.
Initial available RAM was 4,486,336,512 bytes; the minimum was 3,219,218,432
bytes. Sampled editor-private memory peaked at 1,167,659,008 bytes. The editor
and monitor both stopped. Evidence is in `reports/aryia-startup-source-after`
and `logs/aryia-startup-source-after`.

Host process sampling found a separate startup cost. Windows Audio Device
Graph Isolation (`audiodg`) working memory grew by about 370 MiB. The desktop
camera activates an audio listener even when sound, microphone, and video
streaming audio are disabled in the unit-world settings. The legacy listener
constructor immediately opens an OpenAL device and context. This call path is
confirmed in source; the measured host-memory increase is correlated with it.
A bounded one-listener probe is needed to test causation before changing the
listener lifetime. No audio endpoint settings or unrelated processes changed.

Complete avatar import and the reported rendering lag remain unverified. All
launch, memory, and timeout limits remain unchanged.

### Deferred Legacy Audio Device Creation

Legacy `ListenerContext` now keeps listener state without opening a playback
device. Source or buffer acquisition, effects access, `MakeCurrent`, capture
access, and native metadata queries initialize the transport. Initialization
replays the cached state and effective gain. `VerifyError` checks only an
existing transport. Disposing a dormant listener does not create one. V2
construction and the OpenAL transport implementation are unchanged.

The initialization gate serializes state changes and first device creation.
Published transport access and error checks use a volatile fast path to avoid
an inverse lock order with OpenAL effects. Disposal marks the listener under
the gate, then releases child resources outside it. Initialization failure
releases the transport even if effects cleanup fails. Source review also
corrected the first assignment of the default distance model so it initializes
attenuation without opening a device.

The baseline audio probe built with zero warnings and errors, before this audio
source change. Its guarded run did not start: available RAM was below the 4 GiB
launch requirement. Later available RAM fell to about 2.6 GiB, below the 3 GiB
stop threshold. The changed audio source has not been built or run. Its memory
saving is not yet measured. No unit tests changed. `git diff --check` passed.

The prepared scratch probe measures a dormant listener and an optional native
source/buffer request. It checks cached pose, attenuation, mute, gain replay,
one device open, disposal, and host audio-process memory. Complete this check
under the existing guards, then build the isolated editor and retry the full
package. A first native request can occur before audible playback. Device
errors now occur at that request; the existing hardware-dependent audio test
setup will need adjustment after live validation and user clearance for test
changes. Complete Aryia import and rendering remain unverified.

Final source review found no remaining initialization, replay, disposal, or
lock-order blocker. It found one new tick-allocation path: pose setters could
publish property events. The four pose fields now use the existing
`SetField(..., publishNotifications: false, ...)` overload. This preserves the
previous notification behavior and avoids new event-argument allocations on
listener updates. This final correction is also awaiting build and runtime
validation.

### Audio Validation And Full Package Retry

On 16 October 2026, available RAM recovered above the unchanged launch gate.
The baseline native audio probe completed in 13.11 seconds. One idle listener
started `audiodg`, which peaked at 340,869,120 working bytes. The probe process
peaked at 17,903,616 private bytes. This isolated run supports the startup audio
cost observed in the editor.

The changed audio project and scratch probe built with zero warnings and
errors. Two intermediate probe builds failed because the scratch program lacked
the enum namespace; the production audio build passed in both attempts. The
corrected probe then passed both checks:

- Dormant pose, gain, attenuation, tick, and disposal operations opened zero
  native devices. `audiodg` working memory stayed at 250,073,088 bytes throughout;
  that process remained from the earlier baseline. The probe peaked at
  11,227,136 private bytes and completed in 11.75 seconds.
- Native source/buffer acquisition opened exactly one device. Cached pose,
  Doppler, speed, distance, mute, and restored effective gain matched the native
  state. Disposal closed the transport and removed the logical listener. The
  probe peaked at 18,485,248 private bytes and completed in 17.94 seconds.

All audio probes passed the memory and time guards. They did not play audio or
create a graphics context. Evidence is in `reports/startup-audio-baseline`,
`reports/startup-audio-dormant`, and `reports/startup-audio-demand`.

The isolated audio/editor build passed with zero warnings and errors. It reused
existing project references and omitted analyzers and debug symbols, as in the
previous constrained editor build. It completed in 35.12 seconds, peaked at
803,823,616 process-tree private bytes, and kept at least 4,204,060,672 bytes of
available RAM. The audio assembly in the editor output matched the newly built
assembly. Evidence is in `reports/startup-audio-editor-build`.

The next full Aryia runtime began at 18:45:12 UTC. All seven companion sources
became ready. Package preparation produced all 26 roots, then six prefab
conversions started within about 0.17 seconds. The editor rendered its startup
world and remained accessible through MCP. A viewed viewport capture showed
only the Main Scene and sky; no imported avatar scene was published.

The watchdog stopped the editor at the 2 GiB private-memory threshold after
73.08 sampled seconds. Private bytes jumped from 2,081,361,920 to 2,848,354,304
between two samples about 67 ms apart. The sampled peak therefore exceeded the
threshold despite termination. Minimum available RAM was 3,864,567,808 bytes.
`audiodg` working memory stayed near 250 MB rather than growing during startup.
The editor and watchdog were confirmed stopped. Evidence is in
`reports/aryia-startup-audio-after`, `logs/aryia-startup-audio-after`, and the
corresponding `mcp-captures/` directory.

The audio change is now validated through native device use and editor startup.
Complete Aryia import and avatar rendering remain unverified. The next
investigation targets concurrent prefab conversion and repeated project setup
before the first conversion progress message. No memory or timeout limit changed.

### Serialized Package Conversion And Dependency Scan Reuse

The stopped run's final logs refine the earlier index hypothesis. All six
initial conversions reported 2% at 11:46:23.276 local time, after waiting about
50.9 seconds. `SourceGuidIndex` is already shared by project root. Its locked
initial scan reads `.meta` files through the first GUID line. It does not read
model or texture bytes. When that scan completed, several roots imported
`GoBone_Coat.fbx` at the same time. Concurrent native model import is a supported
candidate for the abrupt private-memory rise, but its exact allocation was not
captured.

Package startup now waits for each root's scene attachment or error before
starting the next root. Pending completion counts still include every entry.
Errors release the scheduling gate. Visibility rules are unchanged. Staged
renderer activation can overlap the next conversion; the synchronous hierarchy
conversion and scene attachment are serialized.

`SourceDependencyGraphBuilder` also had an independent repeated-work defect.
It tracked only the active recursion path. A later reference to a shared file
reopened it and appended its outgoing edges again. A per-build set now scans
each reached source or metadata file once. Parent reference edges and later
node-kind upgrades remain intact. Active recursion still detects cycles, and
cancellation is checked at each visit and streamed line. No unit tests changed.

The guarded editor build passed with zero warnings and errors in 22.16 seconds.
It used the same narrow compile-only build settings as the earlier editor run.
Peak process-tree private memory was 771,702,784 bytes; minimum available RAM
was 4,451,684,352 bytes. Evidence is in `reports/startup-package-serial-build`.

The retry began at 18:50:32 UTC. Its logs confirm sequential scene conversion
and publication: seven `.prefab` scenes were added with `visible: False`, then
`DemoHandsInPockets.unity` was added with `visible: True`. The next demo started
after that attachment. Partial-import material and missing-slot warnings did
not abort the available scene content. The GUID scan was much shorter on this
run, but filesystem caching differs, so this does not establish a timing gain
from the code changes.

The watchdog stopped the editor after 12.07 sampled seconds at the unchanged
2 GiB private-memory threshold. Sampled private memory peaked at 2,211,270,656
bytes (2.06 GiB); minimum available RAM was 4,225,634,304 bytes (3.94 GiB).
The editor and watchdog stopped. The process ended before a viewport capture
could verify the imported demo visually. Evidence is in
`reports/aryia-startup-serial-after` and `logs/aryia-startup-serial-after`.

The remaining increase coincides with directional-shadow resource creation.
The log reports four-layer 2048-square arrays with 12 mip levels, followed by
four-layer 4096-square arrays with 13 mip levels in R16f and DepthComponent24.
Shader programs for the first demo loaded from binary cache; those log entries
do not show a source compile or link. Inspect shadow texture allocation and
Unity light-resolution mapping before another runtime attempt. This correlation
does not yet prove the exact cause of the remaining process-memory increase.

### Directional Shadow Mip Allocation

Source inspection confirmed unused mip storage. The directional color-texture
mip-limit helper returned 1000 when moment mipmaps were disabled. Cascade raster
depth also kept the default limit of 1000. The OpenGL array allocator uses the
computed smallest mip level plus one for immutable storage, independently of
`AutoGenerateMipmaps`. Both arrays therefore allocated full chains while the
ordinary shadow path used only mip zero.

The helper now returns zero when moment mipmaps are disabled. Cascade raster
depth explicitly sets both mip limits to zero and disables mip generation.
Enabled moment mipmaps still allocate the full chain. Base resolution, cascade
count, and point/spot shadow code are unchanged. For four 4096-square layers in
R16f plus four-byte depth storage, unused levels account for about 128 MiB.
The equivalent 2048-square arrays account for about 32 MiB. These are storage
estimates; driver-private memory must be measured separately.

The 4096 light came from the startup world's explicit OpenGL light setting.
Imported directional lights use the engine's 2048 default. Unity source shadow
resolution and source quality settings are not currently mapped. That separate
import gap needs a defined policy; this change does not alter resolution.

The first narrow renderer build found a stale core reference assembly missing
`TransformBase.OverriddenMatrixChangeNotifications`, which is present in the
current source. The next guarded build refreshes Core, Rendering, and Editor
in dependency order. No unrelated source edit was needed.

Core, Rendering, and Editor then built with zero warnings and errors under the
same guards. The narrow compile-only build completed in 79.71 seconds, with
1,060,036,608 peak process-tree private bytes and 3,865,587,712 minimum available
RAM. The editor output matched the new Rendering assembly. Evidence is in
`reports/startup-shadow-mip-build-core`.

The next runtime began at 18:56:46 UTC. The OpenGL log confirms `levels=1` for
both color and raster-depth arrays at 2048 and 4096 resolution, each with four
layers. Eight imported scenes were attached before the stop. A viewed capture
from earlier in the run showed the startup sky; it does not validate the
imported avatar or shadow appearance.

The private-memory guard fired after 14.42 sampled seconds at 2,196,180,992 bytes
(2.05 GiB). The independent managed probe then observed 2,729,590,784 private
bytes (2.54 GiB) while session shutdown completed. Minimum available RAM was
3,954,106,368 bytes (3.68 GiB). Both the editor and watchdog stopped. Evidence
is in `reports/aryia-startup-mip-after`, `logs/aryia-startup-mip-after`, and its
`mcp-captures/` folder. Different sky selection and refreshed core binaries mean
these runs are not a controlled total-memory comparison. The allocation log
establishes that unused mip storage was removed.

The scratch watchdog now terminates its already verified editor process before
calling the session manager to reconcile the named session. It verifies the
executable path and start time again before that direct stop. Parser and
process-identity preflight checks passed; the changed stop path has not been
exercised by another editor run. Polling still cannot prevent a large allocation
from crossing the threshold between samples. Do not treat it as a hard memory
cap or claim that the sampled stop value was the actual process peak.

Complete Aryia import remains blocked by the 2 GiB runtime budget. Further work
must distinguish retained import data, renderer target storage, and driver
commit without another uncontrolled runtime attempt. All configured limits
remain unchanged. No unit test code was added or changed.

### CPU Import Isolation And Remaining Atlas Storage

The next audit found no duplicate cascade slice allocation. The four legacy
cascade arrays use about 480 MiB at the current resolutions: 96 MiB for the
2048-square light and 384 MiB for the 4096-square light, assuming four-byte
depth storage. Layer framebuffers attach those same arrays. The atlas groups
were not yet sampleable, so these arrays provided the active shadow fallback.
Removing them would remove that fallback.

The separate 4096-square, one-layer atlas raster depth array still allocated
13 mip levels. Atlas page framebuffers attach mip zero, and depth sampling
uses that level. Atlas depth creation now sets both mip limits to zero and
disables mip generation. The storage estimate falls by about 21.33 MiB per
such layer. Color moment filtering, shadow resolution, and point/spot shadow
behavior remain unchanged. Core, Rendering, and Editor built with no warnings
or errors. The guarded build took 40.04 seconds, peaked at 1,121,099,776 bytes
of process-tree private memory, and kept at least 3,832,143,872 bytes of RAM
available. Evidence: `reports/startup-atlas-depth-build` in the current run.

CPU isolation uses a scratch process guard with an unnamed Windows job. The
child starts suspended. The guard assigns and verifies the 2 GiB process
commit limit and kill-on-close flag, checks the unchanged launch conditions,
then resumes the child. The monitor stays outside the job. It checks available
system commit with `GetPerformanceInfo`, stops the job before writing a stop
log, and retains all existing RAM, commit, and timeout checks. A helper passed
limit readback, small allocation, oversized allocation rejection (Win32 error
1455), and idle-child termination on job closure. Evidence:
`reports/startup-guard-denied` and `reports/startup-guard-close`.

The job cap does not cover all GPU, kernel, or other-process allocations. A
sampled system RAM floor is still not a hard reservation. These checks do not
establish that another OpenGL run is safe. No editor ran during this isolation.

The CPU probe uses the actual serialized scene importer with partial import,
Auto FBX selection, model asset services, and retained results. It registers
OpenGL factories for texture metadata but creates no renderer or graphics
context. Early probe attempts lacked texture and model service registrations;
those failed attempts are not complete import measurements.

With the required services installed, the first 25 roots completed at about
146 MiB private memory. The final `Open Me.unity` root imported the avatar model
more than once and reached the hard process cap. The run ended with an Assimp
allocation failure after 80.53 seconds. Sampled private memory peaked at
2,146,824,192 bytes; minimum available RAM was 3,489,935,360 bytes. At the final
root failure, managed live memory was 1,435,204,360 bytes and cumulative managed
allocation was 20,557,447,120 bytes. Evidence:
`reports/startup-import-all-composed` and
`Build/Logs/StartupImportProbe_debug/windows_x64/xrengine_2026-10-16_12-16-57_pid20092/`.
This separates a large CPU import cost from the earlier renderer allocation.
It does not validate complete import or avatar appearance.

Source review found that geometry coordinate conversion creates two full
blendshape delta tables and a full vertex/target object graph, then packs the
same data again. Each table uses about 41 bytes per active record, before
array overhead. These are allocation totals; the two tables need not remain
live together. The next change replaces this temporary expansion with a
streamed packed-data read and preserves the existing buffer publication path.

A direct switch from deep mesh clones to the runtime shared-buffer clone was
rejected. That clone borrows source buffers; it does not keep an independent
asset lifetime or copy-on-write contract. Source replacement or disposal could
invalidate an imported scene. Published root geometry remains independently
owned.

### Streamed Geometry Validation And Retained Import Data

The geometry change keeps a compact base-attribute snapshot and reads packed
blendshape entries one vertex at a time. It preserves normal normalization,
tangent projection, handedness, target bounds, winding, and buffer publication.
Empty or duplicate shape names and duplicate active entries retain the previous
materialized path for compatibility.

The guarded Core, Rendering, and Editor build passed with no warnings or
errors in 63.81 seconds. Peak build-tree private memory was 1,096,839,168 bytes;
minimum available RAM was 4,417,871,872 bytes. A CPU probe used 12,000 vertices,
32 sparse shapes, and nonuniform, reflected, and missing-direction cases.
All populated base and blendshape bytes, names, indices, and bounds matched
exactly between the baseline and new assemblies. The probe initially included
unused trailing alignment padding in a uint2 buffer. Those bytes also varied
between baseline runs. Hashing only `ElementCount * ElementSize` removed that
measurement error; no production packing change was needed.

Managed allocations during conversion changed as follows:

| Case | Baseline bytes | Streamed bytes | Reduction |
|---|---:|---:|---:|
| Nonuniform | 11,233,880 | 6,520,512 | 42.0% |
| Reflected | 11,679,296 | 6,966,688 | 40.4% |
| Missing directions | 9,349,360 | 4,636,752 | 50.4% |

Evidence is in `reports/startup-geometry-stream-build`,
`reports/startup-geometry-logical-baseline`, and
`reports/startup-geometry-logical-after`. These small probes establish output
parity and allocation reduction. They do not establish the full import peak.

The next full CPU run still reached the hard cap while importing repeated
ARYIA model instances. It ended with an Assimp allocation failure after 76.41
seconds. Sampled private memory peaked at 2,147,479,552 bytes. Available RAM
stayed above 3,516,547,072 bytes. The final progress sample reported about
1.31 GB managed live memory and 23.49 GB cumulative allocation. Evidence:
`reports/startup-import-all-stream`. This is not a successful full import.

The stage samples exposed two further costs:

- Native FBX import calls `PrepareImportedMesh` before Unity coordinate
  conversion. Its conditional weak table retains managed deformation arrays
  for as long as each mesh stays alive. Coordinate conversion immediately
  makes those arrays stale. Their data size is approximately
  `64 * vertices + 16 * shapes + 16 * records + 16 * deltas` bytes, before
  headers. Delta entries are not deduplicated in this payload. Unity conversion
  now discards this optional cached preparation before changing each mesh.
  It leaves source buffers and pending consumers intact. The renderer retains
  its existing fresh-preparation path.
- Required texture validation decoded the same image for repeated references.
  One root context now caches successful validation by full path, byte length,
  and last-write time. It retains no image pixels. Failures are not cached.
  The importer checks the file stamp again after decoding and caches only an
  unchanged revision. A replacement that preserves both stamp fields remains
  outside this revision check.

The follow-up build completed Core and Rendering with no warnings or errors,
then stopped during Editor compilation when available RAM fell below 3 GiB.
The minimum sample was 3,213,479,936 bytes, and peak build-tree private memory
was 1,059,229,696 bytes. Evidence: `reports/startup-import-retention-build`.
After stop, about 3.6 GiB was available, below the unchanged 4 GiB launch
requirement. The latest editor changes and their full CPU import effect still
need validation. No editor or GPU run was started. No unit test code changed.

Final source review found no blocking defect in cache removal or validation
reuse. A later memory check reported 2,933,006,336 bytes available (about
2.7 GiB), below the stop threshold. No further build or import was started.

The next user-requested memory check reported 2,457,387,008 bytes of available
RAM (2.289 GiB) and 13.911 GiB of available system commit. RAM was below both
the 4 GiB launch requirement and the 3 GiB stop threshold. No build, CPU import,
editor, or GPU context was started. Validation of the latest import fixes
remains pending; all limits remain unchanged.

Further source candidates, not changed in this work, are prompt release of
consumed native FBX packed source chunks and avoiding deformation preparation
that composition will discard. Neither has a measured acceptance result yet.

### CPU Import Retry With Validated Editor Changes

The next requested retry found 4.915 GiB of available RAM and 16.804 GiB of
available system commit. The pending Editor build passed with zero warnings
and errors in 22.57 seconds. Peak build-tree private memory was 769,957,888
bytes; minimum available RAM was 4,314,906,624 bytes. The CPU probe build also
passed with zero warnings and errors. Evidence:
`reports/startup-import-retention-editor-retry` and
`reports/startup-import-retention-probe-retry` in the current run.

The full CPU import started with 5,298,348,032 bytes of available RAM. The
first 25 roots completed. The guard stopped the process during required-texture
validation in `Open Me.unity` after 24.73 seconds because available RAM fell
below 3 GiB. Sampled process private memory peaked at 1,938,538,496 bytes
(1.81 GiB), below the unchanged 2 GiB hard cap. Minimum available RAM was
3,189,563,392 bytes (2.97 GiB); minimum available system commit was
16,035,770,368 bytes. Evidence: `reports/startup-import-all-retention`.
The guard reports exit code zero for job termination; its nonempty stop reason
and the missing final root result make this a stopped run, not a successful
import. The child process was confirmed absent after termination.

Import counters showed 40 cached deformation entries totaling 122,554,560
bytes (116.9 MiB) during native import. After coordinate conversion, both
counters were zero. This confirms release of the stale preparation data.
Texture-validation reuse compiled and ran, but this stopped run does not
establish its total allocation reduction. Full import, rendering, and the
reported lag remain unverified. No renderer or graphics context was created.
All launch, process, RAM, commit, and timeout limits remained unchanged.

### Texture Validation Without Pixel Export

Required texture validation used `Decode(File.ReadAllBytes(path))` but read
only the returned dimensions. This retained an encoded managed array during
native decode and exported a complete RGBA buffer that validation discarded.
SDR export uses 64 MiB for a 4096-square image. Color conversion could also
create a native image clone.

`IRuntimeImageCodec.Validate(Stream)` now returns dimensions after full decode
and bounded native pixel-cache reads. The Magick backend applies required SDR
color conversion in place and preserves the first-image and output-size
checks. It leaves the caller's stream open and retains no decoded pixels.
Unity validation uses this path. Actual texture loading, quality, resolution,
and deferred registration remain unchanged. Native decode still needs its
pixel cache; this change does not make all image decoding constant-memory.

Core, Imaging.Magick, and Editor built with zero warnings and errors under
the existing guard. Build-tree private memory peaked at 773,152,768 bytes;
minimum available RAM was 4,669,149,184 bytes. The CPU import and focused image
probes also built successfully. No unit test code was added or changed.
Evidence: `reports/texture-validation-build`,
`reports/texture-validation-import-probe-build`, and
`reports/texture-validation-probe-build` in the current run.

The focused CPU probe compared legacy decode and streamed validation. Both
paths accepted color, transparent, grayscale, CMYK, and HDR fixtures with
matching dimensions. Both rejected invalid bytes, a truncated PNG, and a PNG
with a valid header but damaged IDAT pixel data. Streams remained open after
success and failure. Evidence: `reports/texture-validation-payload-fixtures`.

Separate guarded processes measured the same 4096-square Aryia texture,
`Top-straps-choker_Normal_OpenGL.png`:

| Measurement | Legacy decode | Streamed validation |
|---|---:|---:|
| Sampled peak private bytes | 402,116,608 | 213,241,856 |
| Managed allocated bytes | 121,341,104 | 69,416 |
| Measured operation milliseconds | 622.85 | 521.27 |

The sampled private peak fell by about 47%. These are single-run measurements,
not a throughput benchmark or a full-import peak estimate. Evidence:
`reports/texture-validation-legacy` and `reports/texture-validation-stream`.

The full CPU retry started with 5,498,925,056 bytes of available RAM and passed
the texture-validation stage that stopped the previous run. During later
texture validation, process private memory fell to about 893 MB. The first
25 roots completed. The guard stopped the final scene during the fourth
ARYIA model import at 62.63 seconds, when private memory reached exactly the
2,147,483,648-byte hard cap. Minimum available RAM was 3,315,666,944 bytes;
minimum available system commit was 15,821,180,928 bytes. Evidence:
`reports/startup-import-stream-validation`. The owned process was confirmed
absent after the stop. This was not a successful full import.

The measured texture-validation spike is reduced. Repeated model import is
the remaining observed blocker. Source and scene inspection confirmed four
separate instances of the same FBX: FULL FT/GoGo/SPS, GoGo, GoGo/SPS, and
Default. `ImportPrefabInstance` calls `ImportModelHierarchy` for each external
model instance; that path creates a new `ModelAssetImporter` and decodes the
model each time. This is not a GUID lookup failure. Each hierarchy receives
its own Unity modifications, removals, and additions. Reuse must preserve
independent hierarchy and mesh ownership; sharing mutable roots is not valid.
No model-cache change was made in this texture-validation work.

All limits stayed unchanged: 4 GiB available
RAM before launch, 2 GiB process private memory, 3 GiB available RAM stop,
4 GiB available system commit stop, and 90 seconds. No renderer, window, or
graphics context was created. Complete import and rendering remain unverified.

### Independent Avatar Imports Within The Existing CPU Limits

Native FBX import retained packed source chunks after their data had been
copied into owned `XRMesh` buffers. It also built all control-point weight
dictionaries before mesh workers started. Both lifetimes increased overlap
between import scratch and completed geometry. Each chunk now releases its
source arrays and index storage after successful mesh construction. Each
active worker builds its own temporary skin-weight dictionary.

Unity composition now sets `ModelImportOptions.PrepareGpuDeformationOnImport`
to false. Native import otherwise prepared managed deformation input that
coordinate conversion immediately invalidated. The option defaults to true
for ordinary native imports. The renderer retains its existing bounded
preparation path for final geometry. This changes preparation timing, not
skinning or blendshape support. Source review confirmed that bind matrices
are stored before workers start and that packed-source copying is synchronous.

The ModelAssetPipeline and Editor build passed with zero warnings and errors
in 28.03 seconds. Peak build-tree private memory was 772,018,176 bytes;
minimum available RAM was 4,874,366,976 bytes. Evidence:
`reports/model-import-lifetime-build` in the current run. The first full CPU
retry still failed during the fourth avatar import at the hard process cap.
It ended after 62.14 seconds with an Assimp allocation error. Sampled private
memory peaked at 2,147,340,288 bytes; minimum available RAM was 3,414,933,504
bytes. Evidence: `reports/startup-import-model-lifetime`. This run did not
complete the independence audit.

Packed source construction also allocated a tangent-delta array for every
blendshape entry, although the native FBX path supplies zero tangent deltas.
`XRMeshPackedSource` now keeps those zeros implicit until a nonzero tangent
delta arrives. The packed mesh reader supplies zero for an absent tangent
span. Final geometry stays owned by each mesh; no cross-avatar storage was
introduced.

Rendering built with zero warnings and errors in 36.69 seconds under the
same guard. A small CPU comparison used 12,000 vertices and 14,400 sparse
blendshape entries per case. All populated mesh-buffer hashes matched the
previous renderer for all-zero tangents, a first nonzero tangent after entry
4,097, and mixed zero/nonzero tangents. Source allocations were:

| Case | Previous bytes | New bytes |
|---|---:|---:|
| All zero | 3,672,840 | 3,279,552 |
| Late first nonzero | 3,672,816 | 3,574,488 |
| Mixed from the start | 3,672,816 | 3,672,816 |

The zero-tangent case reduced temporary source allocation by 10.7% in this
probe. Evidence: `reports/packed-tangents-build`,
`reports/packed-source-baseline`, and `reports/packed-source-after`.

The final CPU retry completed all 26 package roots, including `Open Me.unity`,
with exit code zero and no guard stop. It took 88.01 seconds. Peak sampled
private memory was 2,114,826,240 bytes (1.970 GiB). Minimum available RAM was
3,239,968,768 bytes (3.018 GiB), and minimum available system commit was
15,906,365,440 bytes. The run started with 5,602,181,120 bytes of available
RAM. Evidence: `reports/startup-import-packed-tangents`.

The CPU probe checked all four named avatar subtrees for cross-instance
aliases in nodes, transforms, model components, models, submeshes, LODs,
meshes, data buffers, data-source objects, nonempty topology lists, blendshape
name arrays, and bone palettes. Every utilized or root bone had to belong to
its own avatar subtree. All checks passed. Shared Unity material assets were
allowed; the importer still applies each instance's existing overrides.

| Avatar | Nodes | Meshes | Unique bones | Mesh buffer bytes |
|---|---:|---:|---:|---:|
| FULL FT/GoGo/SPS | 542 | 47 | 340 | 243,411,056 |
| GoGo | 525 | 46 | 340 | 243,320,480 |
| GoGo/SPS | 532 | 46 | 340 | 243,320,480 |
| Default | 505 | 46 | 340 | 243,320,480 |

The final root reported 125 models and 35 materials. Private memory after
completion was 1,830,850,560 bytes. The owned child was confirmed absent after
exit. Existing partial-import diagnostics remain, including missing material
slots, unsupported VRChat behavior, and fallback material conversions. CPU
completion does not establish visual correctness or GPU safety.

All limits remain unchanged: launch requires 4 GiB available RAM and 4 GiB
available system commit; process private memory is capped at 2 GiB; stop
thresholds remain 3 GiB available RAM, 4 GiB available system commit, and
90 seconds. The successful run had only about 31 MiB of process-cap margin
and 19 MiB of sampled RAM margin. This is a successful bounded CPU run, not
evidence of broad headroom. No editor, renderer, or graphics context ran.
No unit test code was added or modified.

Further headroom could come from reusing read-only parsed FBX source documents
within one root import. The native path, material-slot resolution, and Unity
animation import currently parse overlapping source data again. Such a cache
must remain separate from mutable runtime instances and must preserve the
strict animation parser's distinct validation mode. No source-document cache
was added in this work.

### Parsed Source Reuse For More Import Headroom

The previous successful run peaked at 61.24 seconds during the fourth native
avatar import. Native import, Unity coordinate checks, material remap alias
resolution, material-slot resolution, and animation conversion parsed
overlapping FBX data repeatedly.

A new `FbxSourceDocumentCache` retains only the most recently used source
revision and semantic policy. `SourceProjectImportContext` owns the cache
during one root conversion. `ModelAssetImporter` borrows it through an
instance property. Native import and the Unity readers reuse parsed source
documents while constructing fresh runtime instance data. Both root entry
points clear cached references immediately after hierarchy composition and
dispose the cache on exit. Structural mappings are not retained.

Review found a revision race in an initial draft with separate document
getters: a source edit between calls could combine old semantics with new
geometry. Native import now requests an atomic document group. Material-slot
and animation conversion request coherent pairs. Each cache miss verifies the
file stamp after parsing before publishing new entries. Strict Unity animation
validation has a separate entry and cannot reuse permissive native results.
Final source review found no remaining blocking defect. Current consumers
read the cached source arrays and construct their own meshes, bones, weights,
and animation clips.

Fbx, ModelAssetPipeline, and Editor built with zero warnings and errors in
27.06 seconds. Build-tree private memory peaked at 770,912,256 bytes; minimum
available RAM was 4,062,740,480 bytes. The CPU import probe also built.
Evidence: `reports/fbx-source-cache-build` and
`reports/fbx-source-cache-import-probe-build` in the current run.

The guarded full CPU retry started with 4,964,012,032 bytes of available RAM.
It completed the first 25 roots, then stopped after 36.36 seconds during the
main scene when available RAM fell below 3 GiB. Peak private memory was
1,659,801,600 bytes (1.55 GiB); minimum available RAM was 3,183,759,360 bytes;
minimum available system commit was 15,345,582,080 bytes. Evidence:
`reports/startup-import-fbx-cache`. The nonempty stop reason marks this as an
incomplete run despite the job-termination exit code zero. It did not reach
the four-avatar independence audit.

This run started with about 0.59 GiB less available RAM than the previous
successful run. It cannot establish a complete-import peak reduction. The
cache also retains source data between model imports, so lower cumulative
allocation alone does not prove lower peak memory. Keep this effect open
until a guarded full run completes.

Source review then found that deformer parsing reserved a full index-sized
normal-delta dictionary even when the shape had no normals. Dictionary
capacities now use the available index/value pair counts for skin weights,
position deltas, and normal deltas. Loop bounds and decoded values are
unchanged. This final capacity change has not yet been built or measured.

A small scratch cache-contract probe is prepared for source revision and
policy invalidation, eviction, strict-after-permissive validation, coherent
snapshots, disposal, and retained-document validity. It has not been built or
run. No unit test code was added or changed.

After the stopped run, available RAM fell to about 3.39 GiB, below the
unchanged 4 GiB launch requirement. No further build or import was started.
The remaining validation is the latest Fbx build, the small cache-contract
probe, and a complete CPU import with the existing four-avatar independence
audit. All memory and timeout limits remain unchanged. No editor, renderer,
window, or graphics context ran.
