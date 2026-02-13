# Compute Shader Taxonomy

This folder contains compute shaders grouped by functional domain.

## Core GPU-driven stages

- `Culling/` — visibility extraction/culling stages.
- `Indirect/` — command/key/batch/count construction stages.
- `Occlusion/` — Hi-Z and occlusion-related stages.
- `Sorting/` — ordering/sort stages.
- `Debug/` — diagnostics/readback helper stages.

## Additional domains

- `GI/` — global illumination compute stages (`SurfelGI`, `RESTIR`, `RadianceCascades`, `LightVolumes`).
- `Animation/`, `AO/`, `BVH/`, `Particles/`, `PhysicsChain/`, `SDF/`, `Terrain/` — subsystem-specific compute stages.
- `Unused/` — quarantined/deprecated shaders retained for migration safety.

## Naming

Prefer explicit subsystem+operation names and avoid generic names.
