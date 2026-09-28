# Portable browser scene boot

**Date:** 2026-09-28. **Base:** `fe2b45b4a51084f22d071216b7e1ac8222299411`.
**Result:** A published WebAssembly application runs the real engine scene
lifecycle. WebGPU rendering and mobile-device qualification remain unimplemented.

## Implementation

The `XREnginePortableRuntime=true` build profile compiles explicit existing-source
allowlists in Data, Extensions, Runtime.Core and Runtime.Rendering. It keeps
assembly/type identities intact instead of relocating serialized scene types.
Desktop defaults keep their original project/package references and native hooks;
portable output/intermediate directories are separate.

The selected graph contains no Bootstrap, editor, native windowing, native XR,
physics, audio, image/font processing or native graphics integrations. Rendering
contributes seven backend/presentation contract files; GPU resource/pass/command
contracts and backend implementation are still needed. Runtime packages are the
existing MemoryPack 1.21.4 and YamlDotNet 18.1.0; MemoryPack.Core and its build-time
generator are transitive. The SDK adds its own WebAssembly pack.

The shared `RuntimeSceneHost` owns roots and advances the existing
`RuntimeWorldLifecycle` tick groups. It snapshots lifecycle roots, handles
stop/restart during callbacks, and traverses transforms without worker dispatch or
synchronous task waits. Tick queues use indexed sorted storage to avoid allocating
a tree-enumeration stack each frame. Desktop transform registration and asset
archive decoding remain in desktop partial files. Quaternion compression was
split unchanged from native compression implementations.

The application explicitly composes a parent, child, and component, then runs a
bounded fixed-step `requestAnimationFrame` loop. It processes the existing global
deferred-destruction queue at the application boundary. The managed runtime stays
alive after Main returns so exported scene methods remain callable. A live
teardown failure exposed registered component/child event lists that were cleared
but never destroyed; their owners now release those lists during destruction.

The MSBuild guard rejects unreviewed projects/direct packages, Windows targets,
native copy items and resolved native runtime assets. The SDK's own Mono
browser-wasm runtime pack is a narrow exception. It is not a complete forbidden-API
analyzer or proof that all selected source paths are browser compatible.

## Validation

- .NET SDK 10.0.401, WebAssembly workload/runtime 10.0.12, Release,
  `PublishTrimmed=false`, `RunAOTCompilation=false`.
- Portable Data, Extensions, Core and Rendering built with zero warnings/errors.
  The final browser publish also reported no warnings/errors.
- Actual published output executed in Chrome Headless Shell **145.0.7632.6** on
  Linux through Playwright. Initial load plus two **Run again** cycles all passed,
  with no page errors or error-level console messages. The rendered result was
  captured and visually inspected.
- Every cycle validated 120 component updates, child world position `(2, 1, 0)`,
  one begin/end callback, no update after stop, and restoration of the engine
  object-cache count after teardown.
- Restored browser libraries were limited to the four engine projects,
  MemoryPack/Core/Generator, YamlDotNet and the SDK WebAssembly pack. The published
  `wwwroot` contained no DLL, EXE, SO, DYLIB or static archive files; managed
  assemblies are WebCIL. This artifact check complements the dependency check,
  rather than treating an extension alone as proof of portability.
- Desktop Data rebuilt with zero warnings/errors. Default desktop project
  evaluation retained baseline target frameworks, package/project references and
  native hooks. Full desktop Core/Rendering/editor builds and live desktop scene
  coverage were **not** performed.

The validation environment denies Unix-domain sockets. Standard MSBuild
out-of-process WebAssembly tasks failed with `MSB4216` before executing; the
publish above used a disposable `CustomAfterMicrosoftCommonTargets` import that
registered the same installed SDK tasks with `UsingTask Override="true"` and
`TaskFactory="AssemblyTaskFactory"`. No SDK files or tracked build defaults were
changed. Standard Chrome also failed at its profile singleton socket; the
headless-shell executable ran successfully. Normal SDK task-host operation and a
full desktop Chrome launch still need validation outside this restricted host.

Build/run instructions are in [XREngine.Browser/README.md](../../../../XREngine.Browser/README.md).
Disposable logs, browser result JSON, screenshot and the validation-only task
import were kept under the task's `Build/_AgentValidation` run. No required build
behavior depends on those files.

## Remaining scope

This validates a minimal scene boot, not the complete portable engine surface.
Cooked scene/resource loading, stable cooked-ID round trips, generated browser
registrations, trimming/AOT, canvas/surface ownership, GPU device creation,
visibility/render packets and drawing are unqualified. The current transform
traversal visits every root each step; render-notification deduplication and larger
scene performance need profiling. Exception policy for arbitrary user lifecycle
callbacks and physical mobile visibility/resume behavior also need broader
coverage. No startup, memory or frame-time budget is signed off by this sample.

The next concrete implementation is canvas presentation and asynchronous WebGPU
adapter/device initialization, followed by rendering a minimal engine frame.
