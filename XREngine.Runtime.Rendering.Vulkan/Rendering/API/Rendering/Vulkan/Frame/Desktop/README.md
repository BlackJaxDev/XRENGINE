# Vulkan Desktop Frame Coordination

Owns the desktop frame-attempt coordinator and retirement gate integration.
`VulkanDesktopFrameCoordinator` orders preflight, acquire, recording,
submission, presentation, recovery, and finalization over a stack-only
`VulkanFrameAttempt`. OpenXR observes immutable activity snapshots and never
reads mutable desktop frame-loop fields directly.
