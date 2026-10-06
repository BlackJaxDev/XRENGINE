# Windows CI Build Preparation

## Report and evidence

The user reported that `Windows CI / Build and test` repeatedly failed. The latest twelve runs inspected on 2026-10-05 all failed. Sampled jobs failed before the solution build and unit-test steps, so these results did not establish test failures.

- [Run 37340652079](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37340652079/job/111866571063): submodule builds failed with MSB4242 because workload set `10.0.401.1`, pinned in `global.json`, was not installed. SDK setup succeeded, but the workflow did not restore workloads.
- [Run 36448963328](https://github.com/BlackJaxDev/XRENGINE/actions/runs/36448963328/job/109018458147): settings generation built engine dependencies and failed with MSB3030 copying `VulkanMemoryAllocatorBridge.Native.pdb`. The packaged native DLL reuse path did not generate that file, but the project required it unconditionally.
- Submodule logs also reported missing MSBuild. Discovery relied on `PATH` and hard-coded Visual Studio locations, omitting hosted-runner editions and install layouts.

## Changes

- Build workflows select the SDK from `global.json`; Windows CI, deploy, and release restore solution workloads before submodule builds. Browser compilation retains its existing `wasm-tools` installation, using the pinned SDK.
- Vulkan symbols are registered after native preparation and only when present, preserving normal output and publish propagation. Packaged DLL reuse removes stale generated symbols. Design-time and no-build operations do not compile the bridge.
- Submodule builds discover MSBuild and C++ targets using `vswhere`, with PATH and installed-directory fallbacks.
- After native Rive succeeds, its managed build restores packages and skips rebuilding the native project reference under the managed `AnyCPU` platform.
- The native Rive build applies `Tools/Patches/rive-sharp/reuse-scene-pointer.patch`. It accepts an already applied patch and stops on source conflicts. The user approved this repository-managed build patch.
- The software-Vulkan validation project links the GPU diagnostic source dependencies required by its shared fullscreen pipeline. This repairs a CS0246 failure uncovered by the full solution build without changing test behavior.
- No dependency versions, submodule revisions, or tests were changed.

## Local validation

Evidence root: `Build/_AgentValidation/20261005-110440-windows-ci/` (disposable).

- `dotnet workload restore XRENGINE.slnx`: passed on a machine where the pinned workload set was already installed. Fresh hosted-runner provisioning still needs confirmation.
- Release build of `XREngine.Runtime.Rendering.Vulkan`, using isolated artifacts and an empty `VULKAN_SDK`: passed with zero warnings and errors; packaged DLL copied without a PDB.
- No-build Release publish of that output: passed and included the native DLL without requiring symbols.
- Release settings generator run, including its editor and engine dependency build, with isolated artifacts and an empty `VULKAN_SDK`: passed and wrote settings/schema outputs.
- MSBuild item collection with native preparation: available PDB registered for output and publish.
- Switching from existing generated symbols to packaged DLL reuse: stale PDB removed and omitted from copy items.
- Explicit existing-bridge reuse and design-time item collection: no generated symbols required; design-time collection did not compile native code.
- All five workflow YAML files parsed successfully; all six SDK setup jobs use `global.json`, and every Windows submodule build follows workload restoration. Whitespace checks passed.

## Rive source fix

The first full submodule build reached native Rive compilation, then failed in `native/RiveSharpInterop.cpp` at lines 780 and 789. Clang treated the unused `scene` variables as errors. The build patch changes the guarded returns to use `scene->loop()` and `scene->isTranslucent()`. It does not disable compiler warnings.

Upstream `rive-app/rive-sharp` main was checked on 2026-10-05. Its latest commit is the same pinned `89f4e0df357c7431edb8ab3f80b9b9dd3d3904b2` from 2025-01-22. Both offending lines remain upstream. Pulling latest cannot fix them, and updating nested `rive-cpp` does not change this wrapper file. The user approved the tracked build patch after this check. The submodule revision remains unchanged.

`Tools/Build-Submodules.bat Release AnyCPU` now passes with zero warnings and errors. It builds OpenVR.NET, OscCore, native Rive, and managed RiveSharp. A second run also passes and reports that the patch is already applied. Logs: `logs/submodules-patched-build.log` and `logs/submodules-patched-repeat.log` under the evidence root.

The full Release solution build was repeated after the Rive patch and passed with zero warnings and errors. Log: `logs/solution-build-after-rive-patch.log`. The dependency report was regenerated, and its license outputs were reviewed. The Rive MIT license and submodule revision did not change.

The report could not retrieve the existing CodeAnalysis analyzer license text during this run. Its checked-in MIT text and link were retained. Unrelated license whitespace changes were removed.

The first full solution build passed all other projects but failed the software-Vulkan validation project because its linked fullscreen pipeline now references GPU diagnostic types. After linking those production sources, the targeted Release build passes with zero warnings and errors.

The subsequent full `XRENGINE.slnx` Release build, with isolated artifacts and no Vulkan SDK, passes with zero warnings and errors. The unit-test suite was not executed. Fresh hosted-runner confirmation remains outstanding.

Hosted CI and user confirmation remain outstanding. The passing local builds do not establish a passing unit-test suite on a clean runner.

## Hosted runner retry

The user reported another failed run after committing the fixes.
[Run 37363539476](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37363539476)
targets commit `71ccb6a4fc7fc31647fc04f10e9c419811f68db4`. Its first attempt
ended after about 15 minutes with no runner assigned and no steps executed.
GitHub reported: "The job was not acquired by Runner of type hosted even after multiple attempts".
This attempt provides no new build or test result. A retry was requested and
accepted on 2026-10-05. Attempt 2 acquired a hosted runner and started checkout.
Attempt 2 completed on 2026-10-05 at 22:33 UTC. Workload restoration, submodule
builds, settings generation, restore, and the solution build all passed on the
hosted runner. The unit-test step failed: 726 failed, 4,875 passed, and 12 skipped
out of 5,613 tests. See [job 112000355423](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37363539476/job/112000355423).

The test log includes SPIR-V 1.6 output rejected by a Vulkan 1.0 target,
missing source-file paths and source-contract assertion failures, and playback
device errors from NAudio. These failures need separate investigation. The
passing build confirms the build fixes on the hosted runner, but the complete
workflow still fails.

GitHub reported an Actions incident on 2026-10-05 with delayed runner assignment
and failed jobs. Its status update at 21:54 UTC reported that Actions was
operating normally. This explains the first attempt's runner assignment failure;
the second attempt reached the test suite and reported test failures.
Source: [GitHub status](https://www.githubstatus.com/).

## Test suite repair

The complete TRX artifact has 5,778 cases: 4,875 passed, 726 failed, and 177 did
not run. The console summary omits 165 of the cases that did not run.

The user approved repair of the compiler, stale tests, fixtures, and identified
behavior defects. The work is in progress. No passing full-suite result is
claimed yet.

- The native shaderc DLL supplied by Silk.NET 2.23.0 reproduces the shared
  Vulkan 1.4 / SPIR-V 1.6 error in a minimal native compile. The same source
  succeeds with shaderc 2026.4. The engine now packages that compiler with
  pinned source revisions, archive checksums, rebuild instructions, and combined
  upstream notices. Shader caches identify the exact packaged DLL.
- Test contracts are being updated to the actual Runtime.Host and renderer
  owners. The repairs retain behavior and safety checks rather than require
  former filenames and private members.
- Fixtures now account for required Vulkan API objects, execution scheduling,
  material row layout, RVC feature selection, importer arguments, valid humanoid
  skeletons, and the current pose-audit schema.
- The full TRX summary is published in CI, including reasons that tests did not
  run. Hardware and optional integration requirements are documented in the
  [Windows CI guide](../../../developer-guides/testing/windows-ci.md).

### Stopped at the user's request

The final test-project Release build passed with zero warnings and errors.
The final focused run covered the classes that failed the earlier full local
run, plus the source workspace checks: 1,434 passed and 359 failed out of 1,793.
This is a partial-suite result. The complete CI suite is not yet passing.
The earlier full local run had 5,163 passed, 493 failed, and 124 not executed
in its TRX report. Further repairs followed that full run.

The packaged compiler passes the focused shader compile checks. Its DLL and
license notices appear in both editor build and publish items. Material import,
framebuffer binding, pose-audit schema, and several animation fixtures now pass.
The configured upper-leg axis test still fails and needs a separate diagnosis.

Remaining work includes stale source contracts, shared partial-type lookup
that still depends on filenames, and renderer architecture checks. Three
contracts need a behavior trace before changing their assertions: descriptor
prewarm planner context, descriptor-write invalidation, and retired image-view
rejection before native rendering. Existing architecture checks also report
renderer state, thread-local state, public implementation types, and type
placement. Do not remove these checks merely to make the suite pass.

The unfinished parser experiment was removed before the final build. No new
Roslyn dependency was added. No commit or push was performed. Existing user
changes were retained. Disposable evidence remains under the existing root:
`logs/tests-repair-build-8.log`, `reports/repair-wrap-up/repair-wrap-up.trx`,
and `reports/repair-wrap-up-failures.json`. Resume from these results, then run
the full suite and confirm the result on a fresh hosted runner.
