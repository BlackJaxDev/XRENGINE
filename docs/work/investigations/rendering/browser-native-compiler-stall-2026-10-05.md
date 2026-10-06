# Browser native compiler stall

## Observed failure

The published Advanced world reaches WebGPU native opaque shading preparation,
then exceeds its existing 45-second first-frame limit on the Linux software
adapter. In normal run
[`37264491996`](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37264491996),
job `111624420665`, the shader module completed and validation/memory scopes
fulfilled while `createComputePipelineAsync` remained pending. This does not
identify a particular driver compiler stage. UI animation frames also stalled
in a separate job; a shared cause remains unproven.

## Single diagnostic attempt

The bounded diagnostic ran once in
[`37283428635`](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37283428635),
job `111676595057`. Its immutable profiler source commit was
`2e4447fa012de8ad674b0d1ce13189fed756af71`; activation commit
`00cf18d497e8faeb6415f01d89535ff38dce66d5` bound the exact run ID. It consumed
the existing Editor-published Advanced bundle from commit
`27c0c5b461d8df719e0de0f533106c1a2214c9e1`, source run `37258776047`, artifact
`11324725115`. The bundle ZIP SHA-256 was
`2ac0e2b94d9efb0ba812506f7e3085e924d8ce24c1d97ab85f4df8f35a88b296`.

The sanitized result reports `unavailable`, reason `NativeCommandUnavailable`,
and `authorizationConsumed: true`. The attempt finished after approximately 1,059 ms;
collector lifetime and requested sample duration are null. The failure occurs
in an unprivileged command before the collector spawn. The fixed perf file
passed its filesystem check; package ownership/version queries and FIFO setup
use a common command helper with a one-second limit. The reason does not
distinguish a failed command from its timeout or identify which command failed.
The elapsed time makes timeout plausible, but it is not proof.

No code-region or stack sample summary was produced. Raw deletion was verified.
The recorder/supervisor exit flags remain false because no collector was
launched; they are not evidence of an unverified running privileged process.
The diagnostic preserved the original verdict: native compilation timed out
at approximately 45,000.6 ms. Preparation cleanup completed. No retry was made.

Only the allowlisted summary was uploaded, as artifact `11333446835`; its ZIP
SHA-256 is
`42d3409b5f35dd19d1414c217fcac7586ee4c71a8f43c9327669f3daf660ae3c`.
The request, activation record and dedicated workflow are removed after the
consumed attempt. The helper's existing admission checks remain inactive in
ordinary runs. A later capture needs a separately reviewed scope and explicit
authorization; the prior attempt is not reusable.

## Remaining work

- Identify the failed prerequisite with bounded unprivileged diagnostics before
  considering another capture. Do not infer a shader optimization from an
  unavailable profile.
- Qualify Advanced output on the actual target browsers/devices. Earlier
  physical-device success on narrower source versions is not evidence for this
  exact software-adapter failure.
- Keep the UI frame stall, native compilation stall and native meshlet cooking
  failure distinct until shared causality is demonstrated. The meshlet cook's
  buffer-backed position repair is published independently at
  `4dbec0d42fb2992fed6402a816476445b912439c` and has its own runtime checks.

The normal Linux workflow now records the exact package metadata, package
ownership and FIFO-creation prerequisite outcomes through
`Tools/BrowserSmoke/native-profile-prerequisites.mjs`. Each fixed command has a
five-second limit; the report records elapsed time, completion status and
whether the expected metadata or FIFO properties match. It records whether
the original one-second budget was exceeded. Raw command output is not
retained. The script starts no perf process, browser or privileged command,
creates only its own temporary FIFOs and verifies their deletion. This is an
informational prerequisite record, not shader or profiling acceptance.

On exact source `2b97bda4075563efc9f2558832c6fbcf57611f31`,
[normal run 37290314776](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37290314776/job/111698924336)
passes every prerequisite on its fresh runner: the expected-package query takes
13.769 ms, file-owner lookup 84.771 ms, actual-owner version lookup 10.556 ms,
and FIFO creation 2.448 ms. Each finishes within the earlier one-second budget,
all expected metadata matches, and FIFO cleanup is verified. The fixed perf file
is a regular root-owned executable with no group/other write or special mode
bits. These results rule out a persistent missing prerequisite on that runner;
they do not identify the earlier failure under the shader workload or authorize
another capture. Evidence artifact `11337247033` has ZIP SHA-256
`75ecd6f52c1a6f4710dbf3faa665f847f5d86e78a5f42e2b0eb32499b3afa39a`.

The local unprivileged execution completed with the expected missing-package
outcomes on the cloud development environment and successful FIFO creation and
verified deletion. Syntax and whitespace checks pass. That execution validates
the diagnostic's unavailable-tool path and cleanup only; it cannot establish
which prerequisite failed on the earlier Actions runner.

## Exact integrated-source checks

Run [37386054995](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37386054995)
checks source `e4a90cf2f865a08421bb68c293d27616465e1558`. Portable startup and
physics, the Windows Editor publisher, RollingBall, RenderingParity, modular
pipelines, and the static CPU/GPU meshlet comparison pass. Advanced again
exceeds the existing first-frame limit while native compute pipeline creation
is pending. The UI job has a separate animation-frame failure after startup;
neither failure is explained by the other.

The Advanced evidence archive is artifact `11380387683`, 410,998 bytes,
SHA-256 `37673a09a9607e9a55d803cb9fd203effedd57fe8a174d2767f945df24511a36`.
Its unprivileged Chromium trace records shader-module creation in 124.452 ms.
The Vulkan shader handle/SPIR-V span completes in 201.118 ms, including Tint
SPIR-V generation in 97.533 ms and `vkCreateShaderModule` in 0.251 ms.
Asynchronous compute pipeline initialization starts without a recorded end.
These completed spans exclude WGSL parsing and Tint SPIR-V generation as the
45-second software wait. They do not identify the pending native driver pass.

The same source's genuine Windows Editor bundle also fails on Intel Arc xe-lpg,
driver `32.0.101.8132`, in sandboxed Edge `154.0.4258.53` with fallback false.
Its native WGSL is 410,012 bytes, SHA-256
`a20f06b658dc75a875a1a9e1ef03bb8dff1204c9ac729972729a42f0a8d20e96`.
Both application startup and isolated native/Uber compute pipeline creation
remain pending at the original 45-second limit. No draw or shading dispatch
was accepted. The software trace does not prove that this hardware driver
waits in the same internal pass.

The receiver normal/opacity specialization retained material and shadow
semantics and passed all canonical cooks and ABI checks. The new exact-device
results establish no compilation-time improvement. Earlier compile-only
experiments that replaced shadow-plane bias or selected decals with no-ops
helped isolate costly dependencies, but they are not valid rendering fixes
and are not part of the shipping shader.

## Existing recipe-family specialization

The runtime already sends Uber cohorts to their raster-surface consumer and
other admitted cohorts to the native material consumer. The shared source
previously selected those mutually exclusive material paths from a runtime
record, so each compiled program retained code that its admitted cohorts could
not use. The existing Uber schema define now selects those paths at cook time.
Each consumer explicitly rejects the wrong material family with the existing
material-failure diagnostic bit.

Native shading keeps engine and legacy materials, projective mirrors, ordered
decals, shadow receiver reconstruction, direct lights, and probe lighting.
Uber shading keeps its exact full-float raster surface and lighting exports,
AO rules, debug outputs, opacity, velocity, and reactivity. Common reconstruction
is unchanged. The cooker preserves declared parameters for the existing Uber
consumer identity so unused texture bindings retain their exact ABI. No new
recipe, schema, resource, runtime dispatch, desktop GLSL, or fallback is added.

The narrow ShaderCooker Release build passes with zero warnings and errors.
All 16 existing native/Uber, direct/export, depth, and x1/x4 companions cook.
All 17 descriptor ABI fields match the exact prior cooked artifacts. The main
native WGSL decreases from 410,012 to 368,171 bytes, and the Uber consumer from
372,354 to 174,870 bytes. Generated function inventories retain the required
native operations and remove the opposing material family's unreachable code.
These source and cook results do not establish a faster native pipeline build.
Browser compilation and rendered acceptance remain required at the new source.
