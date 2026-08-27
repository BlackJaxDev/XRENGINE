using ImGuiNET;
using Silk.NET.Vulkan;
using System.Runtime.InteropServices;
using XREngine.Rendering;
using Format = Silk.NET.Vulkan.Format;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns ImGui graphics-pipeline creation for a desktop-output generation.  Old
/// handles retire through the resource-lifetime authority, so recreating WSI
/// never races work submitted with the previous pipeline and does not retain an
/// unbounded superseded-handle list.
/// </summary>
internal sealed unsafe class VulkanImGuiOutputPipelineService(
    VulkanOutputRuntime output,
    VulkanResourceRuntime resources,
    VulkanDeviceContext device)
{
    internal void EnsureCreated()
    {
        VulkanImGuiResources handles = output._imguiResources;
        ulong signature = CreateSignature();
        if (handles.Pipeline.Handle != 0 && handles.PipelineSignature == signature)
            return;
        if (handles.DescriptorSetLayout.Handle == 0)
            throw new InvalidOperationException("ImGui descriptor layout must exist before creating its pipeline.");

        PreserveCurrentPipeline(handles);
        CreatePipeline(handles, signature);
    }

    /// <summary>
    /// Makes the target-compatible terminal UI pipeline resident before a
    /// PresentNow frame can need it. This is deliberately separate from
    /// material pipelines: its compatibility is fully determined by the live
    /// desktop output generation, whereas scene material variants are not.
    /// </summary>
    internal void EnsureMandatoryPresentNowPipeline()
    {
        if (!output._imguiResources.FontReady)
        {
            throw new InvalidOperationException(
                "The mandatory ImGui PresentNow pipeline cannot be initialized before its font descriptor resources are resident.");
        }

        try
        {
            EnsureCreated();
            if (output._imguiResources.Pipeline.Handle == 0)
            {
                throw new InvalidOperationException(
                    "ImGui pipeline creation returned without a resident native pipeline handle.");
            }
        }
        catch (Exception exception)
        {
            InvalidOperationException failure = new(
                $"Mandatory PresentNow ImGui pipeline initialization failed for desktop generation " +
                $"{output.Desktop.Generation} ({output.Desktop.ImageFormat}, " +
                $"{output.Desktop.ImageColorSpace}, dynamicRendering=" +
                $"{device.MutableCapabilities._useDynamicRenderingRenderTargets}).",
                exception);
            Debug.VulkanError(
                $"[Vulkan][PresentNow][InitializationFailed] {failure.Message} " +
                $"Cause={exception.GetType().Name}: {exception.Message}");
            throw failure;
        }
    }

    internal void InvalidateForDesktopOutputMutation()
        => output._imguiResources.PipelineSignature = 0;

    internal void Dispose()
    {
        RetirePipelinePair(
            output._imguiResources.Pipeline,
            output._imguiResources.PipelineLayout);
        output._imguiResources.Pipeline = default;
        output._imguiResources.PipelineLayout = default;
        output._imguiResources.PipelineSignature = 0;
        DestroyShader(ref output._imguiResources.VertShader);
        DestroyShader(ref output._imguiResources.FragShader);
    }

    private ulong CreateSignature()
    {
        HashCode hash = new();
        hash.Add(device.MutableCapabilities._useDynamicRenderingRenderTargets);
        hash.Add(resources.SwapchainRenderPass.Handle);
        hash.Add((int)output.Desktop.ImageFormat);
        hash.Add((int)output.Desktop.ImageColorSpace);
        return unchecked((ulong)hash.ToHashCode());
    }

    private void PreserveCurrentPipeline(VulkanImGuiResources handles)
    {
        Pipeline supersededPipeline = handles.Pipeline;
        PipelineLayout supersededLayout = handles.PipelineLayout;
        handles.Pipeline = default;
        handles.PipelineLayout = default;
        handles.PipelineSignature = 0;
        RetirePipelinePair(supersededPipeline, supersededLayout);
        DestroyShader(ref handles.VertShader);
        DestroyShader(ref handles.FragShader);
    }

    private void CreatePipeline(VulkanImGuiResources handles, ulong signature)
    {
        bool dynamicRendering = device.MutableCapabilities._useDynamicRenderingRenderTargets;
        bool srgb = output.Desktop.ImageFormat is Format.B8G8R8A8Srgb or Format.R8G8B8A8Srgb ||
            output.Desktop.ImageColorSpace is ColorSpaceKHR.SpaceExtendedSrgbLinearExt;
        const string vertex = "#version 450\nlayout(push_constant) uniform PushConstants { vec2 scale; vec2 translate; } pc;\nlayout(location=0) in vec2 inPos; layout(location=1) in vec2 inUv; layout(location=2) in vec4 inColor; layout(location=0) out vec2 outUv; layout(location=1) out vec4 outColor; void main(){ outUv=inUv; outColor=inColor; gl_Position=vec4(inPos*pc.scale+pc.translate,0,1); }";
        string fragment = "#version 450\nlayout(set=0,binding=0) uniform sampler2D sTexture; layout(location=0) in vec2 inUv; layout(location=1) in vec4 inColor; layout(location=0) out vec4 outColor; vec3 SrgbToLinear(vec3 c){ bvec3 cut=lessThanEqual(c,vec3(0.04045)); return mix(pow((c+vec3(0.055))/1.055,vec3(2.4)),c/12.92,cut); } void main(){ vec4 color=inColor*texture(sTexture,inUv);" +
            (srgb ? " color.rgb=SrgbToLinear(color.rgb*color.a);" : string.Empty) + " outColor=color; }";

        handles.VertShader = CreateShaderModule(new XRShader(EShaderType.Vertex, vertex) { Name = "VkImGui.vs" });
        handles.FragShader = CreateShaderModule(new XRShader(EShaderType.Fragment, fragment) { Name = "VkImGui.fs" });
        try
        {
            PushConstantRange range = new() { StageFlags = ShaderStageFlags.VertexBit, Size = (uint)sizeof(VulkanImGuiPushConstants) };
            DescriptorSetLayout layout = handles.DescriptorSetLayout;
            PipelineLayoutCreateInfo layoutInfo = new()
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &layout,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &range,
            };
            Ensure(device.Api.CreatePipelineLayout(device.Device, ref layoutInfo, null, out handles.PipelineLayout), "create ImGui pipeline layout");
            resources.TrackPipelineLayout(
                handles.PipelineLayout,
                "ImGui.PipelineLayout");

            PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = CreateStage(ShaderStageFlags.VertexBit, handles.VertShader);
            stages[1] = CreateStage(ShaderStageFlags.FragmentBit, handles.FragShader);
            try
            {
                VertexInputBindingDescription binding = new() { Binding = 0, Stride = (uint)sizeof(ImDrawVert), InputRate = VertexInputRate.Vertex };
                VertexInputAttributeDescription* attributes = stackalloc VertexInputAttributeDescription[3];
                attributes[0] = new() { Location = 0, Binding = 0, Format = Format.R32G32Sfloat, Offset = (uint)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.pos)) };
                attributes[1] = new() { Location = 1, Binding = 0, Format = Format.R32G32Sfloat, Offset = (uint)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.uv)) };
                attributes[2] = new() { Location = 2, Binding = 0, Format = Format.R8G8B8A8Unorm, Offset = (uint)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.col)) };
                PipelineVertexInputStateCreateInfo vertexInput = new() { SType = StructureType.PipelineVertexInputStateCreateInfo, VertexBindingDescriptionCount = 1, PVertexBindingDescriptions = &binding, VertexAttributeDescriptionCount = 3, PVertexAttributeDescriptions = attributes };
                PipelineInputAssemblyStateCreateInfo assembly = new() { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
                PipelineViewportStateCreateInfo viewport = new() { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
                PipelineRasterizationStateCreateInfo raster = new() { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.None, FrontFace = FrontFace.CounterClockwise, LineWidth = 1f };
                PipelineMultisampleStateCreateInfo multisample = new() { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
                PipelineDepthStencilStateCreateInfo depth = new() { SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthCompareOp = CompareOp.Always };
                PipelineColorBlendAttachmentState blend = new() { BlendEnable = Vk.True, SrcColorBlendFactor = srgb ? BlendFactor.One : BlendFactor.SrcAlpha, DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha, ColorBlendOp = BlendOp.Add, SrcAlphaBlendFactor = BlendFactor.One, DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha, AlphaBlendOp = BlendOp.Add, ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit };
                PipelineColorBlendStateCreateInfo colors = new() { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &blend };
                DynamicState* states = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
                PipelineDynamicStateCreateInfo dynamic = new() { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = states };
                GraphicsPipelineCreateInfo info = new() { SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 2, PStages = stages, PVertexInputState = &vertexInput, PInputAssemblyState = &assembly, PViewportState = &viewport, PRasterizationState = &raster, PMultisampleState = &multisample, PDepthStencilState = &depth, PColorBlendState = &colors, PDynamicState = &dynamic, Layout = handles.PipelineLayout, RenderPass = dynamicRendering ? default : resources.SwapchainRenderPass };
                PipelineRenderingCreateInfo rendering = default;
                Format color = output.Desktop.ImageFormat;
                if (dynamicRendering)
                {
                    rendering = new() { SType = StructureType.PipelineRenderingCreateInfo, ColorAttachmentCount = 1, PColorAttachmentFormats = &color };
                    info.PNext = &rendering;
                }
                Ensure(device.Api.CreateGraphicsPipelines(device.Device, default, 1, ref info, null, out handles.Pipeline), "create ImGui graphics pipeline");
                resources.Lifetime.Tracker.RegisterResource(new VulkanResourceLifetimeKey(ObjectType.Pipeline, handles.Pipeline.Handle), "ImGui.Pipeline", externallyOwned: false);
                handles.PipelineSignature = signature;
            }
            finally
            {
                Silk.NET.Core.Native.SilkMarshal.Free((nint)stages[0].PName);
                Silk.NET.Core.Native.SilkMarshal.Free((nint)stages[1].PName);
            }
        }
        catch
        {
            RetirePipelinePair(handles.Pipeline, handles.PipelineLayout);
            handles.Pipeline = default;
            handles.PipelineLayout = default;
            DestroyShader(ref handles.VertShader);
            DestroyShader(ref handles.FragShader);
            throw;
        }
    }

    private ShaderModule CreateShaderModule(XRShader shader)
    {
        byte[] spirv = VulkanShaderCompiler.Compile(shader, out _, out _, out _);
        fixed (byte* code = spirv)
        {
            ShaderModuleCreateInfo info = new() { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spirv.Length, PCode = (uint*)code };
            Ensure(device.Api.CreateShaderModule(device.Device, ref info, null, out ShaderModule module), "create ImGui shader module");
            return module;
        }
    }

    private static PipelineShaderStageCreateInfo CreateStage(ShaderStageFlags stage, ShaderModule module)
        => new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = stage, Module = module, PName = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr("main") };

    private void RetirePipelinePair(Pipeline pipeline, PipelineLayout layout)
    {
        if (pipeline.Handle != 0)
            resources.RetirePipeline(pipeline, "ImGui.Pipeline");
        if (layout.Handle != 0)
            _ = resources.TryBeginDestroyPipelineLayout(
                layout,
                "ImGui.PipelineLayout");
    }

    private void DestroyShader(ref ShaderModule shader)
    {
        if (shader.Handle != 0)
            device.Api.DestroyShaderModule(device.Device, shader, null);
        shader = default;
    }

    private static void Ensure(Result result, string operation)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to {operation}: {result}.");
    }
}
