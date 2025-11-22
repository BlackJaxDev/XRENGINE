using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        public class VkRenderProgram(VulkanRenderer api, XRRenderProgram data) : VkObject<XRRenderProgram>(api, data)
        {
            public override VkObjectType Type => VkObjectType.Program;
            public override bool IsGenerated => true;
            protected override uint CreateObjectInternal() => CacheObject(this);
            protected override void DeleteObjectInternal() { }
            protected override void LinkData() { }
            protected override void UnlinkData() { }
        }

        public class VkRenderProgramPipeline(VulkanRenderer api, XRRenderProgramPipeline data) : VkObject<XRRenderProgramPipeline>(api, data)
        {
            public override VkObjectType Type => VkObjectType.ProgramPipeline;
            public override bool IsGenerated => true;
            protected override uint CreateObjectInternal() => CacheObject(this);
            protected override void DeleteObjectInternal() { }
            protected override void LinkData() { }
            protected override void UnlinkData() { }
        }

        public class VkSampler(VulkanRenderer api, XRSampler data) : VkObject<XRSampler>(api, data)
        {
            public override VkObjectType Type => VkObjectType.Sampler;
            public override bool IsGenerated => true;
            protected override uint CreateObjectInternal() => CacheObject(this);
            protected override void DeleteObjectInternal() { }
            protected override void LinkData() { }
            protected override void UnlinkData() { }
        }

        public class VkRenderQuery(VulkanRenderer api, XRRenderQuery data) : VkObject<XRRenderQuery>(api, data)
        {
            public override VkObjectType Type => VkObjectType.Query;
            public override bool IsGenerated => true;
            protected override uint CreateObjectInternal() => CacheObject(this);
            protected override void DeleteObjectInternal() { }
            protected override void LinkData() { }
            protected override void UnlinkData() { }
        }

        public class VkTextureView(VulkanRenderer api, XRTextureViewBase data) : VkObject<XRTextureViewBase>(api, data)
        {
            public override VkObjectType Type => VkObjectType.Texture;
            public override bool IsGenerated => true;
            protected override uint CreateObjectInternal() => CacheObject(this);
            protected override void DeleteObjectInternal() { }
            protected override void LinkData() { }
            protected override void UnlinkData() { }
        }

        public class VkTransformFeedback(VulkanRenderer api, XRTransformFeedback data) : VkObject<XRTransformFeedback>(api, data)
        {
            public override VkObjectType Type => VkObjectType.TransformFeedback;
            public override bool IsGenerated => true;
            protected override uint CreateObjectInternal() => CacheObject(this);
            protected override void DeleteObjectInternal() { }
            protected override void LinkData() { }
            protected override void UnlinkData() { }
        }
        
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
