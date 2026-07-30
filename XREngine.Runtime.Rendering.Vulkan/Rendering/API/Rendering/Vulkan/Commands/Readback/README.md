# Vulkan Readback

Owns observational buffer/image readback, pixel and depth/stencil decoding,
and restoration of the exact pre-transfer layout when it is known.
`VulkanReadbackTaskTracker` is the lifecycle owner for asynchronous readback
tasks that must settle before renderer teardown. Readback may borrow renderer
transfer primitives but must not change the resource's steady-state layout
policy or live in the blit owner.