# Browser WebGPU allocation estimate

**Date:** 2026-10-02. **State:** Source and narrow JavaScript boundary probe complete; browser/device memory validation remains open.

`WebGpuCanvasRenderer.getStatistics().gpuMemory` takes a cold, on-demand snapshot of allocations reachable from the renderer's existing owners. It walks the generation-stamped resource table, renderer and forward-pipeline buffers/targets, scene culling and BVH buffers, Hi-Z texture, deformation jobs, engine-frame staging, readback and luminance tickets, and resources queued for retirement. A native-object identity map prevents counting shared mesh/skinning buffers, texture views, and referenced resources more than once. Referenced retired table entries and queued retirees are reported under `retiringEstimatedBytes`; disposed renderers report zero.

`logicalBufferBytes` sums WebGPU buffer sizes, the exact logical sizes requested by this renderer. `estimatedTextureBytes` sums descriptor footprints across every mip, layer, sample, and compressed block tail. `totalEstimatedBytes` is their sum; `liveEstimatedBytes` and `retiringEstimatedBytes` partition it. A 4-byte-per-texel proxy for `depth24plus`/`depth24plus-stencil8` is called out in `depth24PlusEstimatedBytes` and the snapshot basis. [WebGPU permits a 24-bit or 32-bit depth implementation](https://www.w3.org/TR/webgpu/), so even this depth proxy is not a physical-memory measurement. The established, more conservative resource-admission budget in `gpu-resources.js` remains unchanged.

This is **not driver-resident VRAM**. It omits driver row padding, tiling and allocation overhead; canvas swapchain textures; shader, pipeline, sampler and bind-group allocations; and native Jolt memory. Unknown texture formats or descriptors increment `unmeasuredCount` rather than silently contributing zero to a claimed complete measurement. It does not use manifest payload sizes as GPU-memory proxies. The snapshot allocates temporary bookkeeping and is intended for diagnostics or a low-rate status refresh, not each rendered frame.

Validation used the ignored production-boundary probe at `Build/_AgentValidation/20261001-225000-lit-surface/scratch/gpu-memory-probe.mjs`:

```sh
node Build/_AgentValidation/20261001-225000-lit-surface/scratch/gpu-memory-probe.mjs
node --check XREngine.Runtime.Rendering.WebGPU/Assets/gpu-memory.js
node --check XREngine.Runtime.Rendering.WebGPU/Assets/webgpu-renderer.js
git diff --check
```

The probe covers compressed mip tails, arrays, multisampling, 3D mip depth, malformed descriptors, depth-proxy reporting, duplicate references, pending retirement, and post-disposal zeroing. It passes with a 348-byte synthetic total (140 live, 208 retiring); output is in the adjacent `gpu-memory-probe.log`. The earlier 444-byte scratch fixture used a 2-layer, 4-sample color texture, which WebGPU would reject. Correcting that fixture to a valid single-sample array texture reduces its footprint by 96 bytes; the later retiring classification changes the partition, not the total. No tracked tests, browser GPU run, CI, or device-residency measurement were performed for this slice.
