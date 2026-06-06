# CUDA Usage Opportunities Design

Last Updated: 2026-06-06
Status: design guidance
Scope: identify where CUDA can materially benefit XRENGINE, where existing graphics compute is the better fit, and what constraints should govern future CUDA integration.

Related docs:

- [Rendering Architecture](../../api/rendering.md)
- [Physics Architecture](../../api/physics.md)
- [Audio2Face-3D Component](../../features/audio2face-3d.md)
- [Audio2Face-3D Engine Setup](../../features/audio2face-3d-engine-setup.md)
- [Animated Gaussian Cloud Capture And Streaming TODO](../todo/animated-gaussian-cloud-capture-and-streaming-todo.md)
- [Texture Compression And Cooked Texture Cache Design](texturing/texture-compression-and-cooked-cache-design.md)
- [GPU Softbody Mesh Rigging TODO](../todo/rendering/gpu/gpu-softbody-mesh-rigging-todo.md)

## Summary

CUDA should be treated as an optional, feature-scoped accelerator in XRENGINE, not as a general replacement for the engine's existing GPU compute layer.

The runtime renderer already has a substantial graphics-compute architecture: GPUScene culling, indirect draw construction, zero-readback submission, compute skinning and blendshapes, GPU BVH, softbody and physics-chain compute, particles, landscape, GI, AO, and post-processing. Those systems need cross-vendor behavior and tight frame-loop integration, so they should continue to use OpenGL/Vulkan compute unless profiling proves a specific CUDA path is worth the added interop and deployment cost.

CUDA is most attractive at subsystem boundaries where NVIDIA-only acceleration is acceptable, where work is offline or batch-oriented, or where a vendor SDK already requires CUDA.

## Existing CUDA And NVIDIA Footholds

- `MathNet.Numerics.Providers.CUDA` is referenced by several projects, but source search did not find runtime initialization or direct calls into the provider. Treat it as currently unused unless a hidden or generated path is identified.
- Audio2Face-3D already has an explicit CUDA plus TensorRT setup path through `Get-CudaToolkit.ps1`, `Get-TensorRT.ps1`, and the native Audio2X bridge.
- `Tools/Dependencies/Get-NvComp.ps1` can stage `nvcomp*.dll` and `cudart64_*.dll`, but there is no broad runtime decompression integration yet.
- NVIDIA SDK drop folders exist for DLSS, Streamline, Reflex, and RTXGI-style binaries, but those are feature-specific SDK integrations rather than a general CUDA compute layer.
- PhysX exposes CUDA-backed GPU workflow concepts, but the current `PhysxScene` code uses CPU SAP broadphase and does not enable GPU dynamics globally because GPU broadphase had issues with character-controller behavior.

## Recommended Candidates

| Area | Fit | Recommendation |
| --- | --- | --- |
| Audio2Face-3D live inference | High | Keep CUDA/TensorRT as the intended native bridge path. Missing CUDA/TensorRT should produce clear bridge diagnostics, not pretend inference is active. |
| Animated Gaussian reconstruction | High | Prefer a Python/CUDA sidecar or native CUDA bridge for the first expensive reconstruction backend, after license and commercial-use review. Keep runtime playback engine-owned and backend-neutral. |
| PhysX GPU workflows | Medium | Revisit only through an isolated validation scene or project setting. Do not enable GPU dynamics or GPU broadphase globally until CCT behavior, fallback policy, and diagnostics are proven. |
| Asset chunk decompression with nvCOMP | Medium | Prototype for large streamed assets only after format and access patterns settle. Good candidates are animated Gaussian chunks, meshlet payloads, and other bulk cooked data. |
| Offline geometry and import tools | Medium-low | Consider CUDA for model-processing jobs only after CPU benchmarks identify a real bottleneck. Keep editor and build flows functional without CUDA installed. |
| Neural texture or ML-assisted compression | Future | CUDA is likely useful for training, evaluation, or sidecar inference. Do not block the near-term cooked texture cache on it. |

## Non-Candidates For Now

Do not prioritize CUDA for the main frame-critical rendering path:

- GPUScene culling and indirect draw construction.
- zero-readback material scatter and material-table work.
- compute skinning and blendshape deformation.
- GPU BVH build, refit, frustum culling, and editor picking.
- surfel GI, ReSTIR, radiance cascades, light volumes, AO, fog, and post-processing.
- softbody, physics-chain, particles, and landscape compute.

These systems already use graphics compute buffers and shader dispatch. Moving them to CUDA would add OpenGL/Vulkan interop, synchronization, vendor lock-in, and deployment complexity without an obvious payoff.

Also avoid reviving CUDA video decode in the current OpenGL path. The FFmpeg decoder notes that CUDA/D3D11VA caused native crashes and that software decode is currently sufficient for streaming video.

## Integration Principles

1. Keep CUDA optional by default.
2. Make CUDA opt-in at the feature level, not a global engine requirement.
3. If the user explicitly requests a CUDA path, fail visibly with diagnostics when it is unavailable.
4. Avoid silent CPU fallback for explicitly requested accelerated modes.
5. Keep CPU fallback or non-CUDA workflows available for default editor, build, and test flows.
6. Do not introduce per-frame CPU/GPU copies to bridge CUDA output into graphics rendering.
7. Prefer sidecar or native-bridge boundaries where data exchange is coarse and measurable.
8. Require benchmark evidence before moving any existing graphics-compute runtime path to CUDA.
9. Run dependency license review before adding or upgrading CUDA-related SDKs or packages.
10. Keep CUDA binaries out of the repository unless the licensing and dependency policy explicitly allow them.

## Integration Shapes

### Sidecar Process

Best for offline Gaussian reconstruction, ML-assisted asset cooking, and research tooling.

Advantages:

- Keeps Python/CUDA dependency complexity outside the engine process.
- Allows restartable jobs, explicit logs, and backend replacement.
- Works well for long-running batch tasks.

Risks:

- Requires manifest and output validation.
- Needs deterministic job metadata for reproducibility.

### Native Bridge DLL

Best for Audio2Face-3D and any future runtime feature where a vendor C++ SDK must be called directly.

Advantages:

- Lower latency than an external process.
- Can expose a narrow C ABI to managed code.
- Matches the current Audio2X bridge direction.

Risks:

- Loader errors, PATH issues, CUDA runtime mismatches, and native crash risk must be surfaced cleanly.
- Requires careful lifetime and threading rules.

### Graphics Compute Shader

Best for frame-critical rendering and simulation work that already lives in GPU buffers.

Advantages:

- Cross-vendor path through OpenGL/Vulkan.
- Avoids CUDA/graphics interop hazards.
- Keeps render graph and memory barriers in one model.

Risks:

- Less access to CUDA-specific libraries.
- Some algorithms may be harder to express than in CUDA.

### Vendor SDK Integration

Best for DLSS, Streamline, Reflex, RTXGI, TensorRT, Audio2Face, or other SDKs where NVIDIA owns the accelerated implementation.

Advantages:

- Uses vendor-maintained optimized paths.
- Clear feature boundaries.

Risks:

- Proprietary licensing and redistribution constraints.
- Versioning and deployment need explicit docs.

## Roadmap

### Phase 0 - Inventory And Policy Cleanup

- Confirm whether `MathNet.Numerics.Providers.CUDA` is truly unused. If unused, decide whether to remove it or document the planned use.
- Update the PhysX docs or implementation so the documented GPU dynamics default matches current behavior.
- Keep CUDA/TensorRT/nvCOMP setup docs focused on the features that actually require them.

### Phase 1 - Preserve Audio2Face As The Primary Runtime CUDA Path

- Keep the native Audio2X bridge as the canonical CUDA/TensorRT runtime integration.
- Ensure bridge failure modes are explicit in the editor inspector and logs.
- Validate microphone-to-bridge latency separately from general audio playback.

### Phase 2 - Choose The Animated Gaussian Reconstruction Backend

- Use the animated Gaussian worker manifest as the stable engine boundary.
- Treat a Python/CUDA sidecar as the likely first backend if licensing permits.
- Preserve backend logs, model versions, command lines, and output validation summaries.

### Phase 3 - Prototype nvCOMP On One Large Streamed Asset Type

- Do not start with textures, because GPU-native texture compression is the nearer-term design for texture memory.
- Prefer animated Gaussian chunks or meshlet/cooked geometry payloads if profiling shows CPU decode or IO bandwidth pressure.
- Compare wall time, CPU time, upload time, memory pressure, and fallback behavior against the existing path.

### Phase 4 - Revisit PhysX GPU Workflows In Isolation

- Build a narrow validation scene for GPU dynamics, particles, or articulations.
- Include character-controller coverage before changing defaults.
- Keep project-level settings and diagnostics clear enough that GPU PhysX failures are actionable.

## Validation Requirements

Every CUDA feature should ship with targeted validation:

- capability detection and missing-dependency diagnostics;
- CPU or non-CUDA baseline measurements when fallback exists;
- GPU timing and wall-clock timing for the accelerated path;
- memory usage and transfer volume;
- no synchronous readback in frame-critical paths unless the feature is explicitly diagnostic;
- startup and shutdown behavior when CUDA DLLs are absent, mismatched, or unavailable;
- license and redistribution review for every new native dependency.

## Open Questions

- Should `MathNet.Numerics.Providers.CUDA` remain referenced if no source path uses it?
- Should PhysX GPU dynamics be exposed as a project/runtime setting, a scene-level setting, or a dedicated experimental backend mode?
- Which animated Gaussian reconstruction backend has acceptable licensing for open-source and commercial use?
- Is nvCOMP useful enough for runtime streamed payloads once DirectStorage, compressed texture formats, and existing cache formats are accounted for?
- Should CUDA capability detection live in a shared native-dependency service, or remain local to each feature bridge?
