# Cooked browser shader artifacts

**Date:** 2026-09-28. **Base:** `f5012ca7c9ca1b4f42d9905a46944ac41d2ec943`.
**Status:** Source implemented and the sample package generated. No .NET build,
WGSL compilation, browser/GPU execution, regression tests, profiling or physical
mobile validation was performed, following the user's explicit validation waiver.
Generating package files is not evidence that a GPU accepts or renders them.

The subsequent [renderer module and asset bridge](browser-webgpu-module-assets.md)
moves shader sources, recipes and generated assets into the WebGPU leaf project.
Commands remain the same; earlier paths below describe this original delivery.

## Shared shader result contract

`ShaderCompileTarget.WebGPUWgsl` identifies UTF-8 WGSL. `ShaderArtifact` carries the
explicit target and payload, with target-checked accessors for SPIR-V and WGSL.
`ShaderCompileResult` now owns that artifact alongside its existing entry point,
identity, compiler, reflection, dependencies, diagnostics, cache and timing data.
The result/payload contracts are included in the portable rendering source list.

Existing Vulkan callers can keep constructing results with the original `byte[]
SpirV` argument, reading or replacing `SpirV` with an init/with expression, and
using nine-element positional deconstruction. The original Vulkan target retains
its numeric value. WGSL producers use `ShaderArtifact.FromWgsl` and the artifact
constructor instead. Reading/deconstructing `SpirV` from a WGSL result throws;
it never returns WGSL bytes to a Vulkan consumer. String representations avoid
format-specific getters. The payload retains byte-array ownership semantics;
callers must not modify published bytes. Serializers and consumers that inspect
format-specific properties must select the target first.

The browser packager below emits a portable JSON descriptor directly; it does
not call the desktop shader compiler service or deserialize `ShaderCompileResult`
in JavaScript. No new compiler provider or GLSL/Slang-to-WGSL translation is
claimed. Desktop Vulkan compatibility is preserved in source, not newly tested.

## Explicit WGSL cooking

Run from the repository root with Python 3.10 or later:

```sh
python3 Tools/Shaders/cook_browser_shaders.py
```

This packages `XREngine.Browser/wwwroot/mesh.wgsl` using
`XREngine.Browser/Shaders/browser-unlit.recipe.json` into
`XREngine.Browser/wwwroot/shaders`. No third-party Python package, native shader
tool or dependency upgrade is required. The generated assets are checked in so
an unchanged browser publish does not require Python. Re-run cooking after source
or recipe edits and publish the complete package with its consuming application.

Optional `--recipe` arguments select up to 16 recipes; `--source-root` and
`--output` change the explicit input/output roots. Recipes must use the supported
opaque unlit position/UV layout and self-contained WGSL. Includes, defines,
specialization, other stages/entry points, optional features, incompatible layouts
and other source languages fail by name. Additional named entries may share the
same profile; the current browser requests `browser-unlit` explicitly.

The tool is a **packager**, not a WGSL syntax/semantic validator or reflection
engine. It copies source with normalized LF line endings and preserves line/column
locations. Recipe metadata declares the binding contract; WebGPU still checks the
actual shader and pipeline asynchronously. No regex shader translator is used.

Inputs are bounded: 64 KiB JSON per recipe/descriptor/manifest, 1 MiB WGSL per
artifact and 16 artifacts per package. Duplicate JSON keys, nonfinite JSON values,
invalid UTF-8/BOM/NUL source, invalid counts, unsupported schema/options and source
paths escaping the selected root are rejected before the manifest is changed.

## Package identity and layout

| File | Responsibility |
| --- | --- |
| `manifest.json` | Schema 1, WebGPU backend, frame-packet version 1, named descriptor URLs and SHA-256 digests |
| `<descriptor-sha256>.shader.json` | Target/language/compiler identity, entry points, requirements, source dependency hashes, semantic schema, matrix, vertex/binding and pipeline contracts |
| `<source-sha256>.wgsl` | Canonical UTF-8 WGSL bytes matching the declared digest and byte length |

Descriptor identity hashes the exact deterministic JSON bytes, including source
hashes, target, entry points, compiler identity, semantic schema, layout, pipeline,
feature/limit requirements and the explicitly empty unsupported option sets.
Fields are sorted and files contain no timestamps or absolute machine paths.
The producer identity is `xrengine-wgsl-packager/1`; change it when introducing
incompatible packager semantics. The shader semantic schema is
`xrengine.browser.mesh.v1` and remains separate from package and packet versions.

The recipe declares position XYZ + UV stride 20, 64-byte object transforms,
16-byte material tint and the existing two bind groups. It fixes triangle-list,
no face culling, opaque output, one sample and `depth24plus` with depth writes and
`less` comparison. Matrix columns consume the existing System.Numerics row-major
field upload convention. This does not qualify coordinate/color correctness.

Every recipe is prepared before output publication. Source and descriptor files
are content-addressed and never overwritten with different bytes. The manifest
is replaced atomically last. Existing hash-named files are retained for older
manifests and in-flight clients; the cook tool does not prune them. Production
retention/garbage collection needs a separate deployment policy. For remote
publishing, upload immutable assets first and switch the manifest last as well.

## Browser loading and startup

`shader-artifact.js` revalidates the manifest using `cache: no-cache`, selects the
required named artifact, and fetches only exact digest filenames next to that
manifest. Content-addressed files use `cache: force-cache`. Redirects are rejected.
Streaming reads enforce byte limits even when Content-Length is missing. The
loader uses fatal UTF-8 decoding and SHA-256, verifies descriptor bytes before
parsing, and verifies WGSL bytes before passing them to WebGPU.

Schema, target, compiler identity, packet version, entry points, layout, pipeline,
dependencies, source length and requirement metadata must agree with the executor.
Missing/corrupt/incompatible content fails explicitly without a raw-source or
renderer fallback. Hashes detect version mixing and corruption; this is not a
signed trust boundary against an attacker who controls the whole deployment.

The renderer loads the package before requesting a device. It compares each
supported requirement with the adapter and sends the declared limits to
`requestDevice`. The initial recipe needs two vertex attributes, two bind groups,
three bindings per group, a 64-byte uniform binding and one dynamic uniform
binding; it requests no optional GPU feature. Other device limits retain API
defaults. These requirements describe the current shader layout, not the complete
browser renderer capability profile or validated hardware support.

Cancellation is passed to fetch; superseded or stopped startup cannot install the
result in another canvas session. Shader-module diagnostics include original
source path, line, column and artifact identity. Pipeline creation remains async;
ordinary rendering starts only after it resolves. The explicit counters snapshot
also includes the selected artifact identity and requested requirements. There is
no per-frame fetch, parsing or hashing.

## Tracking and remaining work

The active [runtime TODO](../../todo/rendering/mobile-webgpu-runtime-todo.md) now
has checked code-completion rows as well as the original acceptance checklist.
This makes implemented source visible without treating skipped validation as a
pass. Older canvas status prose was reconciled with the already-landed draw packet.

Still required:

- Registered renderer-module and existing mesh/material/camera integration,
  generic resource wrappers, visibility/render-buffer and frame-output contracts.
- An approved/pinned WGSL-producing compiler, target-aware production material
  generation, reflection and more shader/pipeline variants. Current metadata is
  an explicit narrow contract, not automatic shader reflection.
- Full cooked world/asset manifests, streaming textures/mips, lighting/shadows,
  transparency, animation and other production browser features.
- Browser compilation/rendering, source/layout mismatch and cache corruption cases,
  cancellation during fetch/hash/pipeline startup, build/Vulkan regressions,
  known-value coordinate/texture checks, warm-up budgets and mobile qualification.

Package generation run for this change:
`python3 Tools/Shaders/cook_browser_shaders.py` — produced one WGSL artifact.
No test, build or rendering acceptance result is claimed.

Primary API references: [WebGPU device requests](https://developer.mozilla.org/en-US/docs/Web/API/GPUAdapter/requestDevice),
[SHA-256 digest](https://developer.mozilla.org/en-US/docs/Web/API/SubtleCrypto/digest).
