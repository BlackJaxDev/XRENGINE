namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        public class VkMaterial(VulkanRenderer api, XRMaterial data) : VkObject<XRMaterial>(api, data)
        {
            public override VkObjectType Type => VkObjectType.Material;
            public override bool IsGenerated => true;
            protected override uint CreateObjectInternal() => CacheObject(this);
            protected override void DeleteObjectInternal() { }
            protected override void LinkData() { }
            protected override void UnlinkData() { }
        }
    }
}
