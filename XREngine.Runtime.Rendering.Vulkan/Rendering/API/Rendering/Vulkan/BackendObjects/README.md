# Vulkan BackendObjects

Owns Vulkan API wrappers around engine resources such as buffers, textures,
framebuffers, materials, mesh renderers, programs, queries, and samplers. A
wrapper may own the native handles and caches required by that engine object,
but renderer-global allocation, upload scheduling, command queues, and lifetime
registries belong in their corresponding `Resources/` or `Commands/` owners.
Small enums and interop structs belong in `Types/`.
