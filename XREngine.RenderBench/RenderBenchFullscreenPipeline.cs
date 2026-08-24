using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Profiling;
using XREngine.Rendering.Vulkan;

namespace XREngine.RenderBench;

/// <summary>Precompiled fullscreen-triangle pipeline used by deterministic one-pass GPU fixtures.</summary>
internal sealed unsafe class RenderBenchFullscreenPipeline : IDisposable
{
    private readonly VulkanExplicitTargetRendererHost _host;
    private readonly Format _colorFormat;
    private readonly SampleCountFlags _samples;
    private readonly ExtDebugUtils? _debugUtils;
    private readonly nint[] _labelNames;
    private ShaderModule _vertexShader;
    private ShaderModule _fragmentShader;
    private PipelineLayout _layout;
    private Pipeline _pipeline;

    public RenderBenchFullscreenPipeline(
        VulkanExplicitTargetRendererHost host,
        RenderProfileRecipe recipe,
        string fixtureName,
        int passIterations)
    {
        _host = host;
        if (!host.SupportsDynamicRendering)
            throw new NotSupportedException("GPU-pass fixtures require Vulkan dynamic rendering; no legacy fallback is selected.");
        _colorFormat = ResolveColorFormat(recipe.ColorFormat);
        _samples = ResolveSamples(recipe.SampleCount);
        _debugUtils = recipe.LabelPolicy == RenderProfileLabelPolicy.Disabled ? null : host.DebugUtils;
        _labelNames = _debugUtils is null
            ? []
            : Enumerable.Range(0, passIterations)
                .Select(pass => SilkMarshal.StringToPtr($"{fixtureName}.Pass{pass}"))
                .ToArray();
        try
        {
            _vertexShader = CreateShader(ShaderCrossCompiler.CompileGlslToSpirv(
                "#version 450\nvoid main(){ vec2 p=vec2((gl_VertexIndex<<1)&2,gl_VertexIndex&2); gl_Position=vec4(p*2.0-1.0,0.0,1.0); }",
                EShaderType.Vertex,
                "RenderBench.Fullscreen.vs"));
            _fragmentShader = CreateShader(ShaderCrossCompiler.CompileGlslToSpirv(
                "#version 450\nlayout(push_constant) uniform C{vec4 color;} pc; layout(location=0) out vec4 o; void main(){o=pc.color;}",
                EShaderType.Fragment,
                "RenderBench.Fullscreen.fs"));
            PushConstantRange range = new() { StageFlags = ShaderStageFlags.FragmentBit, Size = 16 };
            PipelineLayoutCreateInfo layoutInfo = new()
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &range,
            };
            Ensure(host.Api.CreatePipelineLayout(host.Device, in layoutInfo, null, out _layout), "create fixture pipeline layout");
            CreatePipeline();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int Record(
        Vk api,
        CommandBuffer commandBuffer,
        VulkanRenderFrameTarget target,
        int passIterations,
        int drawsPerFrame,
        uint colorSeed)
    {
        ImageLayout currentLayout = target.InitialColorLayout;
        int barriers = 0;
        for (int pass = 0; pass < passIterations; pass++)
        {
            if (_debugUtils is not null)
            {
                DebugUtilsLabelEXT label = new()
                {
                    SType = StructureType.DebugUtilsLabelExt,
                    PLabelName = (byte*)_labelNames[pass],
                };
                _debugUtils.CmdBeginDebugUtilsLabel(commandBuffer, in label);
            }
            Transition(api, commandBuffer, target, currentLayout, ImageLayout.ColorAttachmentOptimal);
            barriers++;
            currentLayout = ImageLayout.ColorAttachmentOptimal;

            ClearValue clear = new() { Color = Color(colorSeed, pass, 0.2f) };
            RenderingAttachmentInfo attachment = new()
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = target.ColorView,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = clear,
            };
            Rect2D area = new(new Offset2D(0, 0), target.Extent);
            RenderingInfo rendering = new()
            {
                SType = StructureType.RenderingInfo,
                RenderArea = area,
                LayerCount = target.Layers,
                ColorAttachmentCount = 1,
                PColorAttachments = &attachment,
            };
            api.CmdBeginRendering(commandBuffer, in rendering);
            api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipeline);
            Viewport viewport = new(0, 0, target.Extent.Width, target.Extent.Height, 0, 1);
            Rect2D scissor = area;
            api.CmdSetViewport(commandBuffer, 0, 1, in viewport);
            api.CmdSetScissor(commandBuffer, 0, 1, in scissor);
            int firstDraw = pass * drawsPerFrame / passIterations;
            int finalDraw = (pass + 1) * drawsPerFrame / passIterations;
            for (int draw = firstDraw; draw < finalDraw; draw++)
            {
                ClearColorValue color = Color(colorSeed, pass, 0.35f + (draw & 7) * 0.05f);
                api.CmdPushConstants(commandBuffer, _layout, ShaderStageFlags.FragmentBit, 0, 16, &color);
                api.CmdDraw(commandBuffer, 3, 1, 0, 0);
            }
            api.CmdEndRendering(commandBuffer);
            ImageLayout next = pass + 1 == passIterations ? target.RequiredFinalColorLayout : ImageLayout.General;
            Transition(api, commandBuffer, target, currentLayout, next);
            barriers++;
            currentLayout = next;
            _debugUtils?.CmdEndDebugUtilsLabel(commandBuffer);
        }
        return barriers;
    }

    public void RecreatePipeline()
    {
        _host.Api.DestroyPipeline(_host.Device, _pipeline, null);
        _pipeline = default;
        CreatePipeline();
    }

    private void CreatePipeline()
    {
        byte* main = stackalloc byte[5] { (byte)'m', (byte)'a', (byte)'i', (byte)'n', 0 };
        PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2]
        {
            new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = _vertexShader, PName = main },
            new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = _fragmentShader, PName = main },
        };
        PipelineVertexInputStateCreateInfo vertex = new() { SType = StructureType.PipelineVertexInputStateCreateInfo };
        PipelineInputAssemblyStateCreateInfo assembly = new() { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
        PipelineViewportStateCreateInfo viewport = new() { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
        PipelineRasterizationStateCreateInfo raster = new() { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.None, FrontFace = FrontFace.CounterClockwise, LineWidth = 1 };
        PipelineMultisampleStateCreateInfo multisample = new() { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = _samples };
        PipelineColorBlendAttachmentState blend = new() { ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit };
        PipelineColorBlendStateCreateInfo blendState = new() { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &blend };
        DynamicState* dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
        PipelineDynamicStateCreateInfo dynamic = new() { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates };
        Format colorFormat = _colorFormat;
        PipelineRenderingCreateInfo rendering = new() { SType = StructureType.PipelineRenderingCreateInfo, ColorAttachmentCount = 1, PColorAttachmentFormats = &colorFormat };
        GraphicsPipelineCreateInfo info = new()
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            PNext = &rendering,
            StageCount = 2,
            PStages = stages,
            PVertexInputState = &vertex,
            PInputAssemblyState = &assembly,
            PViewportState = &viewport,
            PRasterizationState = &raster,
            PMultisampleState = &multisample,
            PColorBlendState = &blendState,
            PDynamicState = &dynamic,
            Layout = _layout,
        };
        Ensure(_host.Api.CreateGraphicsPipelines(_host.Device, default, 1, in info, null, out _pipeline), "create fixture graphics pipeline");
    }

    private ShaderModule CreateShader(byte[] spirv)
    {
        fixed (byte* code = spirv)
        {
            ShaderModuleCreateInfo info = new() { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spirv.Length, PCode = (uint*)code };
            Ensure(_host.Api.CreateShaderModule(_host.Device, in info, null, out ShaderModule shader), "create fixture shader module");
            return shader;
        }
    }

    private static void Transition(Vk api, CommandBuffer commandBuffer, VulkanRenderFrameTarget target, ImageLayout oldLayout, ImageLayout newLayout)
    {
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcAccessMask = oldLayout == ImageLayout.Undefined ? 0 : AccessFlags.MemoryWriteBit,
            DstAccessMask = newLayout == ImageLayout.ColorAttachmentOptimal ? AccessFlags.ColorAttachmentWriteBit : AccessFlags.TransferReadBit,
            Image = target.ColorImage,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, target.Layers),
        };
        api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit, 0, 0, null, 0, null, 1, in barrier);
    }

    private static ClearColorValue Color(uint seed, int pass, float bias)
    {
        float red = ((seed >> 0) & 255) / 1020.0f + bias;
        float green = ((seed >> 8) & 255) / 1020.0f + pass * 0.015625f + bias * 0.5f;
        float blue = ((seed >> 16) & 255) / 1020.0f + bias * 0.25f;
        return new ClearColorValue(Math.Clamp(red, 0, 1), Math.Clamp(green, 0, 1), Math.Clamp(blue, 0, 1), 1);
    }

    private static Format ResolveColorFormat(string value)
        => value.ToLowerInvariant() switch
        {
            "rgba8" => Format.R8G8B8A8Unorm,
            "rgba16f" => Format.R16G16B16A16Sfloat,
            "rgba32f" => Format.R32G32B32A32Sfloat,
            _ => throw new NotSupportedException($"GPU fixture color format '{value}' is unsupported."),
        };

    private static SampleCountFlags ResolveSamples(uint samples)
        => samples switch
        {
            1 => SampleCountFlags.Count1Bit,
            2 => SampleCountFlags.Count2Bit,
            4 => SampleCountFlags.Count4Bit,
            8 => SampleCountFlags.Count8Bit,
            _ => throw new NotSupportedException($"GPU fixture sample count {samples} is unsupported."),
        };

    private static void Ensure(Result result, string operation)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to {operation}: {result}.");
    }

    public void Dispose()
    {
        if (_pipeline.Handle != 0)
            _host.Api.DestroyPipeline(_host.Device, _pipeline, null);
        if (_layout.Handle != 0)
            _host.Api.DestroyPipelineLayout(_host.Device, _layout, null);
        if (_fragmentShader.Handle != 0)
            _host.Api.DestroyShaderModule(_host.Device, _fragmentShader, null);
        if (_vertexShader.Handle != 0)
            _host.Api.DestroyShaderModule(_host.Device, _vertexShader, null);
        _pipeline = default;
        _layout = default;
        _fragmentShader = default;
        _vertexShader = default;
        for (int index = 0; index < _labelNames.Length; index++)
            SilkMarshal.Free(_labelNames[index]);
    }
}
