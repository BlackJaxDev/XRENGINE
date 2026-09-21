# Global Illumination Providers

Global illumination is selected once through `EGlobalIlluminationMode` and resolved by
`GlobalIlluminationProviderRegistry`. The registry produces an immutable
`GlobalIlluminationPlan`; Default and Advanced consume that same plan through their
host adapters. A provider is not enabled merely because source code, a component, or
a shader exists.

## Current support

| Selection | Registry status | Contracted output | Notes |
|---|---|---|---|
| Light Probes and IBL | Supported | Existing PBR probe/IBL bindings | No provider-owned screen field is needed. |
| DDGI | Supported experimental path | Material-shaded, linear-HDR indirect diffuse for deferred opaque surfaces | Dynamic and baked Vulkan mono evidence is recorded in the modular-GI TODO. Forward, transparent, and world-space consumers are unsupported. |
| Radiance Cascades | Unsupported | None at runtime | Its module-owned resolve layout exists, but injection, propagation, and update production are incomplete. Selecting it allocates no GI work. |
| ReSTIR, voxel cone tracing, light volumes/LPV, Surfel GI | Unsupported | None at runtime | These have implementation TODOs and no verified modular provider. |

Unsupported selection is deliberate: the plan retains a diagnostic, but the registry
does not call a module to declare resources or contribute passes. It never silently
falls back to a CPU implementation or another GI method.

## Provider boundary

The relevant types live under `XREngine.Runtime.Rendering/Rendering/GI/Contracts/`.

| Type | Responsibility |
|---|---|
| `GlobalIlluminationProviderDescriptor` | Stable identity, selection enum, required host capabilities, output contribution/consumer coverage, static support, settings type, authoring/debug/bake metadata, and module factory. |
| `GlobalIlluminationPlan` | Immutable selected provider and host capability decision shared by layout, command construction, bindings, and diagnostics. |
| `IGlobalIlluminationHostAdapter` | Host identity, active execution owner, execution anchors, and neutral capabilities. |
| `GlobalIlluminationHostResources` | Host-provided depth/material/AO inputs plus the provider output, composition material, and target identities. |
| `IGlobalIlluminationModule` | Provider-owned support evaluation, resource declaration, pass contribution, invalidation, and release. |

`EGlobalIlluminationProviderFeature` advertises editor integration surfaces:
`Authoring`, `DebugPresentation`, and `Baking`. These flags are metadata only.
`StaticSupport` and the module's runtime support evaluation remain the authority for
whether a selection can execute. For example, Radiance Cascades declares authoring
and debug metadata while remaining explicitly unsupported.

## Inputs, outputs, and ownership

Hosts own their frame targets and provide neutral named handles through
`GlobalIlluminationHostResources`. A module may consume only the inputs it declares
and must declare every resource it writes. Modules must not reference
`DefaultRenderPipeline`, `AdvancedRenderPipeline`, their static resource constants,
or their framebuffer factories.

The current common screen contribution is linear HDR, material-shaded outgoing
indirect diffuse radiance. DDGI applies its material response and ambient occlusion
in its resolve; the generic compositor adds that result once and does not apply those
terms again. Invalid, pending, or incomplete data is unavailable, not valid black.
Screen output covers only the consumers named in the descriptor. It is not an
implicit source for transparent, forward, world-space, or secondary-ray shading.

Provider state remains local to its algorithm. In particular, DDGI owns probe
history, GPU geometry, direct-light buffers, environment capture, resource names,
update receipts, and bake assets. Physical state is keyed to its pipeline/renderer
owner and must be retired when that owner or resource generation changes. See
[Global Illumination Ownership And Selection](../../architecture/rendering/global-illumination-ownership.md)
for the lifetime and selection rules.

## Adding a provider

1. Implement `IGlobalIlluminationModule` in an algorithm-owned GI directory.
   Make `EvaluateSupport` return an honest diagnostic before any allocation.
2. Add one descriptor in `GlobalIlluminationProviderRegistry`. Supply only the
   capabilities and consumers the algorithm has actually validated. Attach a
   component/settings type and `EGlobalIlluminationProviderFeature` metadata when
   applicable; keep those types provider-owned.
3. In `DeclareResources`, declare only provider-owned resources and use the neutral
   context resources for host inputs/output. In `ContributePasses`, add only the
   provider's own passes at an anchor advertised by the host adapter.
4. Preserve the algorithm's submission, in-flight protection, invalidation, and
   disposal rules. A rejected or partial GPU update must not publish a new result.
5. Start with `StaticSupport.Unsupported(...)` until the runtime path has passed the
   relevant host validation. A module factory alone is not support evidence.
6. Document the signal convention, resolution/layer layout, validity semantics,
   consumer coverage, debug behavior, authoring/bake surfaces, and known limits in
   the algorithm TODO or developer guide.

Do not add pipeline booleans, per-method resource-profile bits, host framebuffer
factories, class-name admission checks, or command-chain guards. The registry is the
only serialized-selection mapping; common host code makes generic capability and
contribution decisions only.

## Settings, debug, and baking

The descriptor's `SettingsType` is the discovery point for provider-specific editor
and authoring UI. DDGI registers `DDGIVolumeComponent` and advertises authoring,
debug presentation, and baking. Its bake capture remains algorithm-local
(`DDGIBaking`) and requires an accepted completed update and valid active resources.

Debug views are presentation outputs, not a substitute for valid indirect radiance.
They must use the selected plan and provider-owned resources; debug controls cannot
force an unsupported provider to allocate graph work. Generic UI should show the
plan diagnostic and descriptor metadata instead of re-creating method-specific
selection logic.

## Validation paths

Every provider must validate both adapters before it is advertised as supported:

1. Build the rendering core and affected OpenGL/Vulkan projects without warnings.
2. In a named isolated editor session, select the provider in the Default pipeline,
   capture more than one camera view, inspect the images, and inspect the rendering
   logs for shader/pipeline failures.
3. Repeat with the Advanced pipeline, including its native stage family and any
   minimal-output exclusion behavior.
4. Exercise the exact claimed lifecycle: dynamic and baked updates where supported,
   reset/invalidation, resize/view-layout changes, debug presentation, and each
   advertised consumer. Keep unsupported consumers visibly unsupported.

The complete phase plan, evidence ledger, and planned-method requirements are in
[the modular GI architecture TODO](../../work/todo/rendering/global-illumination/modular-gi-architecture-todo.md).
