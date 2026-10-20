# Branch Integration TODO

Last Updated: 2026-10-09
Status: Merge committed on local `branch-integration` (`e9eb73bac`). Open items remain before promotion.
Architecture: [Runtime organization](../../../architecture/runtime/project-organization.md), [Physics chain world runtime](../../../architecture/physics/physics-chain-world-runtime.md), [Mesh submission contracts](../../../architecture/rendering/mesh-submission-strategies.md)
Validation: [Branch integration validation](../../testing/runtime/branch-integration-validation.md)
Resume state: [Branch integration handoff](../../progress/runtime/branch-integration-handoff.md)

## Current State

Local `branch-integration` has the physics merge `ce54e3c9b` and the browser merge `e9eb73bac` (`browser-library-boundary` at `fb827fb5a`). Neither is pushed. The follow-up fixes after `e9eb73bac` are in the working tree: the identity-cache test fixture, route republish on a renderer-only change, the retained morph version check, and three world-disposal edge cases. Their lasting rules are in the [physics chain world runtime](../../../architecture/physics/physics-chain-world-runtime.md#world-disposal). No open code items remain; the open checks are in the validation document.

## Open Code Items

None. Add items here if the decisions below require code changes.

## Decisions Needed

- When to push `branch-integration` and promote it to `master`.
- Browser source-row capture (`AdvancedBrowserGeometryBasisSource`, `AdvancedBrowserUberAttributeSource`) also runs on desktop for authored-textured and Uber materials. It keeps 80 bytes of CPU data per vertex, cached per mesh geometry revision, and only WebGPU reads it. Decide whether to capture it only for a WebGPU consumer.
- The browser `CreateRenderState` takes the Advanced `RenderState.CullMode` from the command override or material render options, not from the double-sided draw flag. The desktop OpenGL and Vulkan Advanced paths use this value. A command override now changes desktop culling, and `Front` passes through: OpenGL culls front faces, but the Vulkan stable-bin path treats every nonzero value as back-face culling. Decide the intended rule, then make OpenGL and Vulkan agree.
- Review the remaining old S08 index-preparation and octahedral capture patches for unique fixes before selecting further implementation work. See the handoff for exact refs.
- Keep deployment changes and dependency/submodule updates separate. Confirm their current remote state and applicable approval requirements before changes.

## Out Of Scope

- Completion of the entire WebGPU backlog as a prerequisite for integration.
- Deployment or release promotion during the integration.
