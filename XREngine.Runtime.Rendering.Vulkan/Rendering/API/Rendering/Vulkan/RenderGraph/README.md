# Vulkan Render Graph

Namespace: `XREngine.Rendering.Vulkan.RenderGraph`.

Owns Vulkan render-graph compilation, resource binding grammar, frame-operation
ordering, swapchain-context coalescing, attachment compatibility, resource
planning, and immutable graph/barrier plan publication.

`VulkanRenderGraphRuntime` owns mutable compiler/planner workspaces and
publishes versioned `VulkanRenderGraphPlan` snapshots. `VulkanResourceBindingView`
is the sole `tex::`/`buf::`/`fbo::` parser. `VulkanBarrierUsageMapper` maps
backend-neutral usage to Vulkan flags, while `VulkanBarrierPlanner` builds the
plan. Recording consumes the published plan and caller-owned/reused workspaces;
borrowed scratch collections are never returned as unmarked long-lived state.
