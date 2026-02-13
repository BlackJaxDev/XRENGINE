# Traditional Mesh Rendering

Traditional mesh rendering files issue classic indexed/indirect mesh draws.

## Scope

- Draw command setup for traditional mesh submission.
- Traditional path-specific culling/dispatch wiring.
- Traditional-only render pass behavior.

## Not in scope

- Meshlet/task/cluster path behavior.
- Global policy/resource ownership that belongs in shared or `GPURendering` domains.
