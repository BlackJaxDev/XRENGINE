# Vulkan Mesh Rendering Objects

Owns the `VkMeshRenderer` wrapper and object-local preparation, descriptor,
uniform, draw, program, and pipeline behavior. Domain value types live beside
their responsibility in `Buffers/`, `Descriptors/`, `FrameData/`, `Pipelines/`,
`Programs/`, and `Uniforms/`; syntax-based `Records`, `Classes`, `Structs`, and
`Enums` groupings are retired.

Frame-operation queues, shared descriptor/pipeline caches, mesh-uniform buffer
lifetime tracking, and command scheduling are owned outside this wrapper.
