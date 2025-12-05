namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        public class VkTextureView(VulkanRenderer api, XRTextureViewBase data) : VkObject<XRTextureViewBase>(api, data)
        {
            public override VkObjectType Type => VkObjectType.Texture;
            public override bool IsGenerated => true;
            protected override uint CreateObjectInternal() => CacheObject(this);
            protected override void DeleteObjectInternal() { }
            protected override void LinkData() { }
            protected override void UnlinkData() { }
        }
    }
}
