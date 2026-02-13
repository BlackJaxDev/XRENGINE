# Compute Global Illumination (GI)

Contains GI-specific compute shader families.

## Subfolders

- `SurfelGI/` — surfel lifecycle, shading, and debug stages.
- `RESTIR/` — GI sample/resampling/final shading stages.
- `RadianceCascades/` — radiance cascade update stages.
- `LightVolumes/` — light volume compute passes.

Keep GI shaders grouped under this folder rather than mixing with non-GI compute stages.
