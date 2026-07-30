namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    internal interface IVkObject : IRenderAPIObject
    {
        bool IsActive { get; }
        //uint BindingId { get; }
        void Generate();
        void Destroy();
    }
}
