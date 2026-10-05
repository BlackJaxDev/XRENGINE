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
informational prerequisite record, not shader or profiling acceptance; its
first runner result remains pending.

The local unprivileged execution completed with the expected missing-package
outcomes on the cloud development environment and successful FIFO creation and
verified deletion. Syntax and whitespace checks pass. That execution validates
the diagnostic's unavailable-tool path and cleanup only; it cannot establish
which prerequisite failed on the earlier Actions runner.
