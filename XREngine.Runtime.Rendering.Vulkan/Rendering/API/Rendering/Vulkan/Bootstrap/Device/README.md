# Vulkan Device Bootstrap

Namespace: `XREngine.Rendering.Vulkan.DeviceBootstrap`.

Owns physical-device capability query, enablement policy, feature-chain
construction, logical-device create-info assembly, extension function loading,
queue selection, immutable enabled-capability publication, and device/queue
handle lifetime.

`VulkanRenderer.CreateLogicalDevice` is only the composition entry. Runtime
feature decisions use `VulkanDeviceCapabilities`; native extension commands use
`VulkanDeviceContext.ExtensionFunctions`. Frame, OpenXR, render-graph, and
backend-wrapper state must not be introduced here.
