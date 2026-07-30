# Vulkan Frame Operations

Owns the typed frame-operation model, capture transactions, immutable operation
snapshots, and `VulkanFrameOperationQueue`. Operations carry the recording
context and resource-plan identity they require; wrapper files may enqueue an
operation but must not own renderer-global queues or signature buffers.
