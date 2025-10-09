using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using System.Text;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    public class VkShader(VulkanRenderer api, XRShader data) : VkObject<XRShader>(api, data)
    {
        public override VkObjectType Type => VkObjectType.ShaderModule;
        
        public PipelineShaderStageCreateInfo ShaderStageCreateInfo { get; set; }

        public override bool IsGenerated { get; }

        protected override uint CreateObjectInternal()
        {
            byte[] code = Encoding.UTF8.GetBytes(Data.Source);
            ShaderModuleCreateInfo createInfo = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
            };

            ShaderModule shaderModule;
            fixed (byte* codePtr = code)
            {
                createInfo.PCode = (uint*)codePtr;

                if (Api!.CreateShaderModule(Device, ref createInfo, null, out shaderModule) != Result.Success)
                    throw new Exception("Problem creating shader module");
            }

            ShaderStageCreateInfo = new()
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ToVulkan(Data.Type),
                Module = shaderModule,
                PName = (byte*)SilkMarshal.StringToPtr(Data.Name ?? string.Empty)
            };

            return CacheObject(this);
        }

        private static ShaderStageFlags ToVulkan(EShaderType type)
            => type switch
            {
                EShaderType.Vertex => ShaderStageFlags.VertexBit,
                EShaderType.Fragment => ShaderStageFlags.FragmentBit,
                EShaderType.Geometry => ShaderStageFlags.GeometryBit,
                EShaderType.TessControl => ShaderStageFlags.TessellationControlBit,
                EShaderType.TessEvaluation => ShaderStageFlags.TessellationEvaluationBit,
                EShaderType.Compute => ShaderStageFlags.ComputeBit,
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
            };

        protected override void DeleteObjectInternal()
        {
            Api.DestroyShaderModule(Renderer.device, ShaderStageCreateInfo.Module, null);
            SilkMarshal.Free((nint)ShaderStageCreateInfo.PName);
        }

        protected override void UnlinkData()
        {
            throw new NotImplementedException();
        }

        protected override void LinkData()
        {
            throw new NotImplementedException();
        }
    }
}