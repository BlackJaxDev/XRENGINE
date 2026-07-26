namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Specifies the type of address involved in a KHR device fault.
    /// </summary>
    private enum VulkanKhrDeviceFaultAddressType : int
    {
        None = 0,
        ReadInvalid = 1,
        WriteInvalid = 2,
        ExecuteInvalid = 3,
        InstructionPointerUnknown = 4,
        InstructionPointerInvalid = 5,
        InstructionPointerFault = 6,
    }
}
