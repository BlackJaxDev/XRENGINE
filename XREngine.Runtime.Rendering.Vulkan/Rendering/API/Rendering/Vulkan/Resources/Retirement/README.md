# Vulkan Resource Retirement

Owns per-frame-slot deferred-destruction queues and global handle
deduplication. Queue admission and removal are exactly-once operations;
readiness is evaluated against `VulkanResourceLifetimeTracker` observations
before the renderer invokes native destruction.
