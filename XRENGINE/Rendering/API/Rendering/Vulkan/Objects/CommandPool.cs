using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private CommandPool commandPool;

        private void DestroyCommandPool()
            => Api!.DestroyCommandPool(device, commandPool, null);

        private void CreateCommandPool()
        {
            var queueFamiliyIndicies = FamilyQueueIndices;

            CommandPoolCreateInfo poolInfo = new()
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = queueFamiliyIndicies.GraphicsFamilyIndex!.Value,
            };

            if (Api!.CreateCommandPool(device, ref poolInfo, null, out commandPool) != Result.Success)
                throw new Exception("failed to create command pool!");
        }
    }
}