namespace XREngine.Rendering.Vulkan;
[Flags]
internal enum EVulkanResourceLifetimeState : byte { None = 0, CpuOwned = 1 << 0, Recorded = 1 << 1, Submitted = 1 << 2, Completed = 1 << 3, External = 1 << 4, PendingRetirement = 1 << 5, Destroyed = 1 << 6, Queued = 1 << 7 }
