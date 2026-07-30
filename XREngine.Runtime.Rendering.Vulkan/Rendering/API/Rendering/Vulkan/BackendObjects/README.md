# Vulkan Backend Objects

Owns namespace-level internal Vulkan wrappers around engine buffers, textures,
framebuffers, materials, mesh renderers, programs, queries, and samplers.
Wrappers own only the native handles and object-local caches required by their
engine object.

Identity and binding slots come from `VulkanBackendObjectRegistry`. Device,
retirement, descriptor, and pipeline services come from
`VulkanBackendObjectContext`; renderer calls that remain are native operation
adapters, not cache/service discovery. Renderer-global allocation, upload,
command queues, diagnostics, and lifetime registries belong to their focused
owners. Domain contracts are one top-level type per matching file.
