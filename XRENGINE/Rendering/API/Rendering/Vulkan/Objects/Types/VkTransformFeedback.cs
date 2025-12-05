namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        public class VkTransformFeedback(VulkanRenderer api, XRTransformFeedback data) : VkObject<XRTransformFeedback>(api, data)
        {
            public override VkObjectType Type => VkObjectType.TransformFeedback;
            public override bool IsGenerated => true;
            protected override uint CreateObjectInternal() => CacheObject(this);
            protected override void DeleteObjectInternal() { }
            protected override void LinkData() { }
            protected override void UnlinkData() { }
        }
    }
}
