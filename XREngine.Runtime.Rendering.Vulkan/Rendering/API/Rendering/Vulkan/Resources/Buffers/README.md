# Vulkan Buffer Resources

Owns renderer-level Vulkan buffer allocation, memory mapping, transfers,
readback, destruction, and allocation registries. `VkDataBuffer` remains an
engine-resource wrapper under `BackendObjects/Buffers`; renderer-global state
must not be added to that wrapper file.
