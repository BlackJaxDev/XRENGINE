namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        public class VkRenderQuery(VulkanRenderer api, XRRenderQuery data) : VkObject<XRRenderQuery>(api, data)
        {
            public override VkObjectType Type => VkObjectType.Query;
            public override bool IsGenerated => true;
            protected override uint CreateObjectInternal() => CacheObject(this);
            protected override void DeleteObjectInternal() { }
            protected override void LinkData() { }
            protected override void UnlinkData() { }
        }
    }
}
