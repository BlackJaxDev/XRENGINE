# Ambient Occlusion Testing

Last updated: 2026-04-28

Implementation work for AO mode cleanup, HBAO+, GTAO scaffolding, VXAO scaffolding, schema visibility, and shared AO resource naming has landed. This document tracks the remaining validation work that should not keep the old implementation TODOs alive.

## Scope

- HBAO+ quality, cost, and default-readiness validation.
- Non-HBAO mode readiness validation for SSAO, MVAO, MSVO, GTAO, VXAO, and Spatial Hash AO.
- Editor/schema visibility validation for all AO modes.

## HBAO+ Validation

- [ ] Validate editor visibility for every AO type.
- [ ] Validate indoor contact-shadow and crevice quality.
- [ ] Validate outdoor large-radius occlusion.
- [ ] Validate alpha-tested foliage with detail AO on and off.
- [ ] Validate screen-border behavior and self-occlusion biasing.
- [ ] Measure GPU cost against SSAO and MVAO.
- [ ] Decide whether to switch the default AO type from `ScreenSpace` to `HorizonBasedPlus`.

## Non-HBAO Validation

- [ ] Decide whether MVAO remains a supported mode, becomes experimental, or starts a deprecation path.
- [ ] Validate the current MSVO gather and blur against canonical MSVO expectations.
- [ ] Hide or narrow MSVO claims if validation shows it is too far from canonical behavior.
- [ ] Validate Spatial Hash AO under camera motion and changing geometry.
- [ ] Validate Spatial Hash AO at screen edges, thin geometry, and low sample counts.
- [ ] Check whether cached cell reuse causes visible ghosting or lagging artifacts.
- [ ] Decide whether GTAO is exposed beside HBAO+ or becomes the preferred modern non-HBAO screen-space path.
- [ ] Publish a stable AO readiness matrix if these modes remain user-facing.

## VXAO Validation Gates

VXAO is not a finished AO implementation. Before treating it as more than a scaffold:

- [ ] Lock the shared voxel ownership, coverage, and transform contract with VCT.
- [ ] Define voxel coverage, memory, payload, and update strategy expectations for the shared volume.
- [ ] Define how VXAO blends with a short-range screen-space fallback for fine detail.
- [ ] Decide whether VXAO belongs in the default pipeline, as an advanced renderer option, or as research-only work.

## Suggested Evidence

- Editor screenshots showing correct AO setting visibility per mode.
- Indoor and outdoor comparison captures for `ScreenSpace`, MVAO, HBAO+, GTAO, and Spatial Hash AO.
- GPU timing captures for AO gather and blur passes.
- Notes on artifacts: halos, edge leaks, temporal instability, self-occlusion, foliage behavior, and thin geometry.

## Related Documentation

- [Ambient Occlusion](../../features/gi/ambient-occlusion.md)
- [Default Render Pipeline Notes](../../architecture/rendering/default-render-pipeline-notes.md)
- [VXAO Implementation Plan](../design/vxao-implementation-plan.md)
