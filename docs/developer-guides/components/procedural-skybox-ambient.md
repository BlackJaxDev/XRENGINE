# Procedural Skybox Ambient Lighting

`SkyboxComponent` in `DynamicProcedural` mode can drive the world global ambient term from its procedural sun and moon cycle.

## Runtime Behavior

- `SyncGlobalAmbientLighting` is enabled by default for procedural skyboxes.
- Each sky tick computes the same sun and moon directions used by the sky shader and synced directional lights.
- The component writes `WorldSettings.AmbientLightColor` and `WorldSettings.AmbientLightIntensity` from sun elevation, moon elevation, and a low ambient floor.
- `SunGlobalAmbientScale` and `MoonGlobalAmbientScale` control how much of the synced sun/moon intensity becomes global ambient.
- `MinimumGlobalAmbientColor` and `MinimumGlobalAmbientIntensity` keep night scenes from collapsing to pure black.

## Render Paths

Forward and uber-shader forward meshes read the world ambient value through the `GlobalAmbient` uniform.

Deferred light combine also reads `GlobalAmbient`, so deferred and forward meshes share the same ambient baseline.

When light probe GI is active and resolves a probe ambient term, diffuse ambient is:

```text
GlobalAmbient * ProbeIrradiance * Albedo
```

Without active probes, diffuse ambient falls back to:

```text
GlobalAmbient * Albedo
```
