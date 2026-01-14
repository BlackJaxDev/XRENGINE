using System;
using Silk.NET.Vulkan;
using XREngine.Data;
using XREngine.Rendering;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
	private ShaderModule _debugTriangleVert;
	private ShaderModule _debugTriangleFrag;
	private PipelineLayout _debugTriangleLayout;
	private Pipeline _debugTrianglePipeline;
	private ulong _debugTriangleRenderPassHandle;

	private static bool DebugTriangleEnabled
		=> string.Equals(Environment.GetEnvironmentVariable("XRE_VK_DEBUG_TRIANGLE"), "1", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(Environment.GetEnvironmentVariable("XRE_VK_DEBUG_TRIANGLE"), "true", StringComparison.OrdinalIgnoreCase);

	private void DestroyDebugTriangleResources()
	{
		if (Api is null)
			return;

		if (_debugTrianglePipeline.Handle != 0)
			Api.DestroyPipeline(device, _debugTrianglePipeline, null);
		_debugTrianglePipeline = default;

		if (_debugTriangleLayout.Handle != 0)
			Api.DestroyPipelineLayout(device, _debugTriangleLayout, null);
		_debugTriangleLayout = default;

		if (_debugTriangleVert.Handle != 0)
			Api.DestroyShaderModule(device, _debugTriangleVert, null);
		_debugTriangleVert = default;

		if (_debugTriangleFrag.Handle != 0)
			Api.DestroyShaderModule(device, _debugTriangleFrag, null);
		_debugTriangleFrag = default;

		_debugTriangleRenderPassHandle = 0;
	}

	private void EnsureDebugTrianglePipeline()
	{
		if (!DebugTriangleEnabled)
			return;

		ulong currentRp = _renderPass.Handle;
		if (_debugTrianglePipeline.Handle != 0 && _debugTriangleRenderPassHandle == currentRp)
			return;

		DestroyDebugTriangleResources();
		_debugTriangleRenderPassHandle = currentRp;

		const string vertSource = "#version 450\n"
			+ "vec2 positions[3] = vec2[](vec2(0.0, -0.5), vec2(0.5, 0.5), vec2(-0.5, 0.5));\n"
			+ "void main(){ gl_Position = vec4(positions[gl_VertexIndex], 0.0, 1.0); }\n";

		const string fragSource = "#version 450\n"
			+ "layout(location=0) out vec4 outColor;\n"
			+ "void main(){ outColor = vec4(1.0, 0.0, 1.0, 1.0); }\n";

		XRShader vs = new(EShaderType.Vertex, vertSource) { Name = "VkDebugTriangle.vs" };
		XRShader fs = new(EShaderType.Fragment, fragSource) { Name = "VkDebugTriangle.fs" };

		byte[] vsSpv = VulkanShaderCompiler.Compile(vs, out _);
		byte[] fsSpv = VulkanShaderCompiler.Compile(fs, out _);

		fixed (byte* vsPtr = vsSpv)
		fixed (byte* fsPtr = fsSpv)
		{
			ShaderModuleCreateInfo vsInfo = new()
			{
				SType = StructureType.ShaderModuleCreateInfo,
				CodeSize = (nuint)vsSpv.Length,
				PCode = (uint*)vsPtr,
			};

			ShaderModuleCreateInfo fsInfo = new()
			{
				SType = StructureType.ShaderModuleCreateInfo,
				CodeSize = (nuint)fsSpv.Length,
				PCode = (uint*)fsPtr,
			};

			if (Api!.CreateShaderModule(device, ref vsInfo, null, out _debugTriangleVert) != Result.Success)
				throw new InvalidOperationException("Failed to create debug triangle vertex shader module.");
			if (Api.CreateShaderModule(device, ref fsInfo, null, out _debugTriangleFrag) != Result.Success)
				throw new InvalidOperationException("Failed to create debug triangle fragment shader module.");
		}

		PipelineLayoutCreateInfo layoutInfo = new()
		{
			SType = StructureType.PipelineLayoutCreateInfo,
			SetLayoutCount = 0,
			PushConstantRangeCount = 0,
		};

		if (Api!.CreatePipelineLayout(device, ref layoutInfo, null, out _debugTriangleLayout) != Result.Success)
			throw new InvalidOperationException("Failed to create debug triangle pipeline layout.");

		PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];
		stages[0] = new PipelineShaderStageCreateInfo
		{
			SType = StructureType.PipelineShaderStageCreateInfo,
			Stage = ShaderStageFlags.VertexBit,
			Module = _debugTriangleVert,
			PName = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr("main"),
		};
		stages[1] = new PipelineShaderStageCreateInfo
		{
			SType = StructureType.PipelineShaderStageCreateInfo,
			Stage = ShaderStageFlags.FragmentBit,
			Module = _debugTriangleFrag,
			PName = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr("main"),
		};

		try
		{
			PipelineVertexInputStateCreateInfo vertexInput = new()
			{
				SType = StructureType.PipelineVertexInputStateCreateInfo,
				VertexBindingDescriptionCount = 0,
				VertexAttributeDescriptionCount = 0,
			};

			PipelineInputAssemblyStateCreateInfo inputAssembly = new()
			{
				SType = StructureType.PipelineInputAssemblyStateCreateInfo,
				Topology = PrimitiveTopology.TriangleList,
				PrimitiveRestartEnable = Vk.False,
			};

			PipelineViewportStateCreateInfo viewportState = new()
			{
				SType = StructureType.PipelineViewportStateCreateInfo,
				ViewportCount = 1,
				ScissorCount = 1,
			};

			PipelineRasterizationStateCreateInfo rasterizer = new()
			{
				SType = StructureType.PipelineRasterizationStateCreateInfo,
				DepthClampEnable = Vk.False,
				RasterizerDiscardEnable = Vk.False,
				PolygonMode = PolygonMode.Fill,
				LineWidth = 1.0f,
				CullMode = CullModeFlags.None,
				FrontFace = FrontFace.Clockwise,
				DepthBiasEnable = Vk.False,
			};

			PipelineMultisampleStateCreateInfo multisampling = new()
			{
				SType = StructureType.PipelineMultisampleStateCreateInfo,
				SampleShadingEnable = Vk.False,
				RasterizationSamples = SampleCountFlags.Count1Bit,
			};

			PipelineDepthStencilStateCreateInfo depthStencil = new()
			{
				SType = StructureType.PipelineDepthStencilStateCreateInfo,
				DepthTestEnable = Vk.False,
				DepthWriteEnable = Vk.False,
				DepthCompareOp = CompareOp.Always,
				DepthBoundsTestEnable = Vk.False,
				StencilTestEnable = Vk.False,
			};

			PipelineColorBlendAttachmentState colorBlendAttachment = new()
			{
				ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
				BlendEnable = Vk.False,
			};

			PipelineColorBlendStateCreateInfo colorBlending = new()
			{
				SType = StructureType.PipelineColorBlendStateCreateInfo,
				LogicOpEnable = Vk.False,
				AttachmentCount = 1,
				PAttachments = &colorBlendAttachment,
			};

			DynamicState* dynamicStates = stackalloc DynamicState[2];
			dynamicStates[0] = DynamicState.Viewport;
			dynamicStates[1] = DynamicState.Scissor;

			PipelineDynamicStateCreateInfo dynamicState = new()
			{
				SType = StructureType.PipelineDynamicStateCreateInfo,
				DynamicStateCount = 2,
				PDynamicStates = dynamicStates,
			};

			GraphicsPipelineCreateInfo pipelineInfo = new()
			{
				SType = StructureType.GraphicsPipelineCreateInfo,
				StageCount = 2,
				PStages = stages,
				PVertexInputState = &vertexInput,
				PInputAssemblyState = &inputAssembly,
				PViewportState = &viewportState,
				PRasterizationState = &rasterizer,
				PMultisampleState = &multisampling,
				PDepthStencilState = &depthStencil,
				PColorBlendState = &colorBlending,
				PDynamicState = &dynamicState,
				Layout = _debugTriangleLayout,
				RenderPass = _renderPass,
				Subpass = 0,
			};

			if (Api!.CreateGraphicsPipelines(device, default, 1, ref pipelineInfo, null, out _debugTrianglePipeline) != Result.Success)
				throw new InvalidOperationException("Failed to create debug triangle pipeline.");
		}
		finally
		{
			Silk.NET.Core.Native.SilkMarshal.Free((nint)stages[0].PName);
			Silk.NET.Core.Native.SilkMarshal.Free((nint)stages[1].PName);
		}
	}

	private void RenderDebugTriangle(CommandBuffer commandBuffer)
	{
		if (!DebugTriangleEnabled)
			return;

		EnsureDebugTrianglePipeline();
		if (_debugTrianglePipeline.Handle == 0)
			return;

		Api!.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _debugTrianglePipeline);
		Api.CmdDraw(commandBuffer, 3, 1, 0, 0);
	}
}
