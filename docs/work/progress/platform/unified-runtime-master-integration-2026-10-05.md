# Shared runtime integration from master

This integration combines WebGPU commit `748ced6365e21c406086a6149462f0123817cb59`
with master commit `71ccb6a4fc7fc31647fc04f10e9c419811f68db4`. Their common base
is `11ef1f64663e631f969b36921eb5a02affabf9e5`. Master changes 1,150 files after
that base. The merge had 62 conflicted paths. Clean automatic merges also
required API and ownership corrections.

## Serialization and registration

The merged runtime uses master's cooked envelope version 2, archive version 5,
span readers, buffer-writer serializers and generated runtime contracts. The
upstream recook requirement for older envelopes and archives still applies.
The existing RollingBall version 5/6 world bodies and font version 3 codec are
unchanged.

Generic BinaryV2 graph definitions and references keep markers 62/63 and their
fixed-width identities. Master's selected-sharing snapshot markers use 64/65.
Those selected-sharing values are only used by fresh in-process play-mode
snapshots. The writer, reader, size pass and schema inspector use the same
marker definitions. The published NativeAOT reader still rejects generic
formats. The browser interpreter retains verified metadata and bounded type
resolution.

Generated empty asset-dependency declarations apply only to nine audited engine
assembly/type/codec combinations. Other generated schemas keep an unknown
dependency declaration, which browser cooking rejects unless a codec owner
provides an explicit contract.

The compiler source generator is an analyzer, not a browser runtime assembly.
The installed Editor's existing BrowserPublishing source payload now includes
its project, sources, registration props and Roslyn license notices. The new
inputs add 49,828 bytes before manifest overhead. This does not add the separate
CommonAssets packaging proposal.

## Runtime and rendering

Master's dense transform hierarchy, immutable callback snapshots, sequential
tick arrays and play-mode recovery coexist with caller-thread frame ownership
and retryable teardown. Direct button callbacks finish their captured list.
Device-owned callbacks still stop when their input owner or registration changes.
Root render snapshots publish after world binding and retire before detach.

GPU scene and deformation record sizes are unchanged. The generated GLSL
material field now uses `sourceContract`, which matches the shared generator.
Packed attribute inputs replace removed rich-vertex dependencies. Meshlet
payload version 4 remains authoritative; the reader translates only a verified
legacy version 3 compatibility token. Invalid tokens are still rejected.

Master's span-based realtime framing and high-rate lane retain the browser's
combined 128-packet, 2 MiB and 32-reliable-packet limits. Full transform snapshots
remain eligible for coalescing. Frame validation precedes peer, ACK and replay
state changes. Client and server must use the merged protocol implementation.
Native handoff-file loading remains in the desktop adapter.

RollingBall keeps its current project and executable names. Native actor checks
run at the first physics tick. Existing RenderBench manifest field names and
SteamVR identifiers remain unchanged. Current tool and project references use
the renamed sample paths.

## Verification

- Shared Runtime.Host and BrowserRuntimeMetadataCooker Release build: zero
  warnings and errors.
- Runtime.Rendering.WebGPU Release build: zero warnings and errors.
- Production smoke-world cooking: 7,021 published metadata types. A separate
  verifier process installed the verified metadata and hydrated the cooked world.
- Existing headless test project: Release build with zero warnings and errors.
  Its desktop filesystem discovery/change-monitor test passed without assertion
  changes. The project now links the existing desktop BVH cache implementation
  required by its filesystem adapter.
- Independent source reviews covered GPU ABI, world ownership, serialization,
  networking and the sample integration.

These managed checks used the pinned .NET 10.0.401 SDK. They did not execute
browser WebAssembly, Windows Editor publication, shader cooking or GPU rendering.
Exact-commit CI and browser acceptance remain required after publication. No
checklist row is closed by the merge alone.

## Published integration and browser follow-up

The merge was published as `ed3ec84a633c8ce83a8a47bf20246f14e9871221`.
Its parents are the recorded WebGPU and master heads. Its Git tree matches the
reviewed local tree. The reviewed UI input and shadow receiver changes followed
in `e06c98a8d005ff09b69ed233ebb86e06d03f6fe9`.

[CI run 37377991078](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37377991078)
passed portable compilation, browser publication and shader cooking. The Linux
browser check then found two integration faults. World lifecycle, asset delivery,
audio and native Jolt checks passed. The independent GPU canary also passed;
that result does not qualify engine rendering.

- Master closes `RuntimeWorldRenderer` collection publication until its owner
  opens it. `RuntimeWorldHost` already follows this contract. The direct browser
  diagnostic owner did not, so queued model registrations never reached scene
  collection. The approved fixture migration opens publication before starting
  the caller loop and closes it before failure cleanup or disposal. Scene content,
  draw assertions and time limits are unchanged.
- Master binds the scene node in a component's base constructor. This makes
  `IsActiveInHierarchy` true before the object initializer has supplied authored
  light settings. Cooked shadow allocation and directional property/cascade
  checks now also wait for world attachment. Activation still validates the final
  configuration before applying stored shadow dimensions. Explicit material-use
  checks, unsupported cascade/atlas diagnostics and desktop defaults remain.

Independent source review passed for both repairs. The desktop platform and
shared dependencies built with zero warnings and zero errors. The browser
fixture migration remains subject to the next browser build and live run.
These findings do not establish new rendering, performance or device acceptance.

## Transform publication before canvas activation

The exact `e06c98a8` shared-UI bundle failed at startup in two fresh physical
Edge sessions, including one without observer hooks. The artifact size and hash
matched the Windows publisher. Intel Arc hardware was selected with no fallback,
and the browser sandbox remained enabled. No UI input or pixel result was reached.
The exception reported stale transform handle `0:0` during canvas layout.

`TransformBase` published its `World` property change before updating the dense
hierarchy store. A `SceneNode` observer synchronously updated its world and
activated `UICanvasComponent`. Layout then marked the unregistered transform
dirty. The former object-based dirty queue did not require a dense handle at
that point.

The transform now updates its hierarchy store before publishing the world
notification. Callbacks see the attached, transferred or detached storage state.
Handle generation checks and stale-handle rejection remain unchanged. This is a
shared lifecycle fix; no UI test, scene, assertion or deadline is changed.
The Core, Rendering and desktop platform Release build passed with zero warnings
and zero errors. Fresh browser startup and UI interaction checks remain required.
