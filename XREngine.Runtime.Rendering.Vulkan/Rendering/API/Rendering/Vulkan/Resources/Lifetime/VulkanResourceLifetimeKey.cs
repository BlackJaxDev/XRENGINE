using Silk.NET.Vulkan;
namespace XREngine.Rendering.Vulkan;
internal readonly record struct VulkanResourceLifetimeKey(ObjectType Type, ulong Handle) { public bool IsValid => Handle != 0; public override string ToString() => $"{Type}:0x{Handle:X}"; }
