# Vulkan Bootstrap

Owns Vulkan instance, surface, physical-device, logical-device, extension,
validation, and startup compatibility setup. `Device/` contains the
`XREngine.Rendering.Vulkan.DeviceBootstrap` capability, feature-chain,
extension-command, queue-selection, and device-context owners.

Bootstrap publishes immutable enabled capability state and a complete device
context. Runtime command, frame, resource, and wrapper code consumes those
published contracts; it must not read bootstrap-only feature fields or mutate
device creation policy.
