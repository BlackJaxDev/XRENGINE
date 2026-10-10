# Modular render-pipeline assets in the browser

## Required behavior

The owner clarified on 2026-10-02 that browser rendering must support the shared
modular pipeline model: `DefaultRenderPipeline`, `AdvancedRenderPipeline` and
other authored `RenderPipeline` assets. The selected asset, command graph and
settings remain authoritative. A browser-only pipeline copy, concrete-type
whitelist or substitution with Default is not an implementation of that request.

Actual unsupported operations must produce precise capability diagnostics that
identify the pipeline, command/pass, resource or program and missing requirement.
This does not make every desktop GPU feature available on every WebGPU device.
An unconfigured camera may retain its existing default-selection policy; this
change does not select a different default pipeline for desktop or browser.

The owner also explicitly permits CPU-direct, GPU-driven indirect and GPU
meshlet zero-readback submission in the browser. Preserve the authored mode;
capability checks must not impose a CPU-only browser policy. A zero-readback
mode must keep GPU-written counts and visibility on the GPU. Compute/indirect
meshlet algorithms are distinct from hardware task/mesh shader extensions that
WebGPU does not expose; report the latter limit without rejecting the former
algorithm as a category or silently changing its submission contract.

## Current evidence and restrictions

The published 64/110 milestone has live Default-based RollingBall and
RenderingParity evidence. That remains valid and does not establish Advanced or
arbitrary authored-pipeline support.

The shared execution spine already exists: `RenderPipeline` owns command chains,
pass metadata, postprocess schema, resource declarations and generation hooks.
WebGPU records the ordinary viewport execution, and `WebGpuRenderProgram`
consumes verified cooked program companions. Browser metadata includes all types
in the shipped portable closure and exact game assembly; no pipeline-type
metadata whitelist was found.

The restrictions to remove are elsewhere:

- `BrowserRenderingCapabilityAudit` admits only the exact Default type, then
  applies Default postprocess defaults and material-pass enums to every camera
- `BrowserEngineSession` binds named artifacts only to Default; the shared lit
  AO binding similarly assumes Default
- Pipeline artifact catalogs and their cooker/publisher/package/runtime readers
  permit a small fixed set of Default effect names. Global names cannot identify
  different programs for the same logical pass in two pipelines
- Publication does not discover arbitrary shader/material dependencies owned by
  pipeline commands and resource factories
- Generic raster-state policy lives on Default rather than a backend-neutral
  renderer capability contract

## Coherent implementation order

1. Add cold requirement and program/dependency declarations to the existing
   pipeline/command graph. They describe actual operations, resource formats and
   exact cooked program references without constructing GPU resources. Custom
   command behavior can provide its declaration; missing declarations fail by
   command responsibility, not by the owning pipeline's type
2. Use the same declarations for publication and runtime capability validation.
   Preserve asset identity, rebuild execution state from hydrated authoring data
   at the proper lifecycle boundary, and bind catalogs through the shared
   pipeline contract. A shader-free clear pipeline must not require Default
   tonemap/AO artifacts or a scene mesh draw to produce its output
3. Generalize all artifact-catalog boundaries together. Preserve content hashes,
   descriptor/stage ABI validation, duplicate rejection and finite budgets.
   Stable scoped pass references must avoid collisions. Retain old Default alias
   compatibility and the exact built-in compute-kernel ABI registry; arbitrary
   authored compute programs use the existing generic companion route
4. Discover and package pipeline/command-owned programs and material dependencies
   through the same asset/dependency cooker. Move raster validation to shared
   capability policy and expose optional produced AO through a shared binding
   contract, with a neutral binding when the pipeline has no AO producer
5. Implement missing backend operations and the actual Advanced shader/stage
   family. Keep capability rejection specific until each operation exists;
   removing a whitelist alone must never claim that Advanced renders
6. Qualify Default, Advanced and an unrelated authored modular pipeline through
   the real publisher and browser. Record output, resource lifetime and deliberate
   feature limitations independently from source compilation

## Advanced is a separate rendering family

Advanced owns canonical GPU-scene publication, integer visibility targets,
classification/reconstruction/native shading and its ordered stage contract.
Its MSAA path preserves per-sample integer visibility and shades those samples;
it is not enabled by adding ordinary color renderbuffer resolve alone.

Current operation gaps include integer render attachments/MRT support, storage
images and broader texture views, engine indirect submission, synchronization
lowering and the Advanced cooked shader family. Its current capability resolver
and WebGPU renderer also reject the backend explicitly. Those coarse gates must
be replaced by truthful concrete requirements as the implementation lands.
WebGPU limits such as unsupported multisample array forms or unavailable shader
features need faithful backend lowering or an explicit operation-level rejection,
never a hidden switch to another pipeline.

The current renderbuffer/color-resolve and pipeline-independent frame diagnostics
work remain useful shared backend improvements. They do not by themselves close
Advanced coverage. Existing desktop shaders, scheduling and renderer ownership
remain unchanged unless a separately reviewed shared correction is necessary.
