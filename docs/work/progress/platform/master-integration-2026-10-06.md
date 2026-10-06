# Master integration, 2026-10-06

The requested merge brings master `3a37bceec78e1a187407a0d47c7c4c3575805f73`
into WebGPU branch `38efd0b15b270951c2454c709f613e158aa52ea4`.
It includes two upstream commits and resolves six file conflicts.

## Combined behavior

- Keep the shared browser PreRender order, authored decal settings, and WebGPU
  BRDF and temporal-command capability guards.
- Keep master's direct Advanced OpenXR eye ownership and shared preparation
  acquisition. RVC no longer owns an embedded Advanced command family.
- Supply the explicit resolve FBO for TSR history capture.
- Pass the selected world to the new stereo render call. The browser branch
  added this argument to propagate authored scene clear color and world state.
- Preserve both framebuffer-stack test assertion sets. Map the previous factory
  source-scan assertions to the existing shared generator's Desktop mode,
  import, and assembly allow-list. Preserve the incoming host project checks.
- Keep the current browser checklist and the incoming historical shutdown
  findings. The checklist remains 120 of 162 complete.

The incoming shared changes do not change shader source or GPU row layouts.
The browser project graph does not acquire Vulkan or its native shaderc library.
Upstream native library files and license notices retain their supplied bytes.

## Checks and limits

Independent review checked the conflict resolutions, adjacent automatic merges,
host API defaults, browser dependency boundaries, and GPU layout scope.
Release builds of `XREngine.Runtime.Rendering.WebGPU` and
`XREngine.Runtime.Host` pass with zero warnings and errors. These builds include
the shared Core, Rendering, and integration projects. Restore used existing local
package inputs. Vulkan's optional local restore could not complete because its
Silk.NET Vulkan packages were absent from that cache; this is not a compiler
result. Exact-commit browser CI supplies the next executable qualification.

The retained native shaderc license has an upstream blank line at EOF. Other
staged files pass whitespace and conflict-marker checks. No desktop package,
headset, or user-computer run is claimed by these local checks.

Review also found two pre-existing desktop issues outside this merge repair:
the RVC source test still expects a removed helper name, and the desktop factory
generator can lack two model-asset component constructors when those assemblies
are absent from Bootstrap's compilation references. The relevant generator and
Bootstrap inputs are identical in the merge base and both parents. These findings
do not establish a new browser regression and need separate bounded work.

Reviewed local browser guard, shadow descriptor, and host file-transfer changes
remain separate from this merge. Their later integration needs review against
the merged source. The previous UI trace activation is bound to an older exact
commit and run; it cannot authorize a capture from this merge. The unused
replacement capture needs a fresh reviewed source and run binding. Held
networking requests and the separate publisher-adapter proposal are excluded.

## Windows publishing dependency

Exact merge run [37436603887](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37436603887)
built the Editor, then failed during Vulkan backend registration. The native
loader could not load `xr_shaderc.dll`. The browser workflow used checkout's
default `lfs: false`; the incoming DLL is an LFS pointer. Master's separate
Windows workflow uses `lfs: true`.

The bounded repair fetches only
`XREngine.Runtime.Rendering.Vulkan/runtimes/win-x64/native/xr_shaderc.dll`.
It first checks the committed pointer, then checks the delivered file's
5,520,896-byte size and SHA-256
`f5753bf8c64930c6170f681ce649cc4515468b5b4fc81aa552d0d79a47b42cff`.
The revision and native loading behavior stay unchanged. Other LFS assets are
not requested. A later successful Editor run must establish delivery and load;
source review alone does not prove this result.

The seven game jobs could not obtain their bundles after this publisher failure.
Those failures do not establish seven separate rendering regressions. The bound
UI capture cannot start without its published UI bundle; its allowance must be
reconciled from the exact run before any new binding is made.
