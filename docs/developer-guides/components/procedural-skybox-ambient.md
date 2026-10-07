# Procedural Skybox Ambient Lighting

`SkyboxComponent` in `DynamicProcedural` mode can drive the world global ambient term from its procedural sun and moon cycle.

## Fixed Ambient Color

Set `WorldSettings.AmbientLightColor` and `AmbientLightIntensity` for a fixed
ambient term. This does not require a skybox or dynamic GI. The Math
Intersections world uses neutral `(0.15, 0.15, 0.15)` at intensity `1.0` to keep
mesh faces outside direct light visible during inspection.

## Runtime Behavior

- `SyncGlobalAmbientLighting` is enabled by default for procedural skyboxes.
- Each sky tick computes the same sun and moon directions used by the sky shader and synced directional lights.
- The component writes `WorldSettings.AmbientLightColor` and `WorldSettings.AmbientLightIntensity` from sun elevation, moon elevation, and a low ambient floor.
- `SunGlobalAmbientScale` and `MoonGlobalAmbientScale` control how much of the synced sun/moon intensity becomes global ambient.
- `MinimumGlobalAmbientColor` and `MinimumGlobalAmbientIntensity` keep night scenes from collapsing to pure black.

## Render Paths

Forward and uber-shader forward meshes read the world ambient value through the `GlobalAmbient` uniform.

Deferred light combine also reads `GlobalAmbient`, so deferred and forward meshes share the same ambient baseline.

Advanced native opaque shading reads the effective world ambient color from a
canonical environment record. The world-swap capture stores the value inline.
The publisher updates the record when ambient color or intensity changes, even
when scene geometry does not change. Environment flag bit 0 enables this term;
retirement clears the flag before it removes the record. The existing GPU table
at binding 14 carries the record without a new push-constant layout.

The native evaluator adds `Ambient * Albedo * DiffuseEnergy * AO` to lit
surfaces. `DiffuseEnergy` uses the Fresnel and metallic factors from the PBR
material. This fixed diffuse baseline does not require probes or dynamic GI.
Probe lighting remains a separate contribution. The existing GI composition
state suppresses baseline and probe diffuse after it has published an opaque
replacement. Native push flag 32 carries that captured decision. Flag 16 only
requests material exports and must not suppress lighting. Unlit materials keep their own
color. The shared evaluator serves the single-sample and MSAA paths.

See the [native ambient validation](../../work/testing/rendering/advanced-world-ambient.md)
for runtime coverage and remaining checks.

In the deferred combine path, when light probe GI is active and resolves a probe
ambient term, diffuse ambient is:

```text
GlobalAmbient * ProbeIrradiance * Albedo
```

Without active probes, diffuse ambient falls back to:

```text
GlobalAmbient * Albedo
```
