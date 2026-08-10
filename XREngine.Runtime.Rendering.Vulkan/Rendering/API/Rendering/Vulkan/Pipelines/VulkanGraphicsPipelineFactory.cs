using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Creates native graphics pipelines solely from immutable build requests and
/// generation-owned pipeline/program services.
/// </summary>
internal static unsafe class VulkanGraphicsPipelineFactory
{
	internal static Pipeline Create(
		VulkanPipelineManager manager,
		VulkanGraphicsPipelineBuildRequest request,
		PipelineCache pipelineCache,
		bool backgroundCompile)
	{
		if (!manager.IsCompilationDependencyGenerationCurrent(
				request.DependencyGeneration))
		{
			throw new VulkanPipelineCompilationDeferredException(
				"Graphics pipeline request captured shader or layout handles from a retired dependency generation.");
		}

		PipelineVertexInputStateCreateInfo vertexInput = new()
		{
			SType = StructureType.PipelineVertexInputStateCreateInfo,
			VertexBindingDescriptionCount = (uint)request.VertexBindings.Length,
			VertexAttributeDescriptionCount = (uint)request.VertexAttributes.Length,
		};

		PipelineViewportStateCreateInfo viewportState = new()
		{
			SType = StructureType.PipelineViewportStateCreateInfo,
			ViewportCount = request.ViewportScissorCount,
			ScissorCount = request.ViewportScissorCount,
		};

		PipelineViewportDepthClipControlCreateInfoEXTNative depthClipControlInfo = new()
		{
			SType = VulkanDepthClipControlExt.PipelineViewportCreateInfoSType,
			PNext = null,
			NegativeOneToOne = request.NativeNegativeOneToOneDepth,
		};

		if (request.NativeNegativeOneToOneDepth)
			viewportState.PNext = &depthClipControlInfo;

		PipelineColorBlendStateCreateInfo colorBlending = new()
		{
			SType = StructureType.PipelineColorBlendStateCreateInfo,
			LogicOpEnable = Vk.False,
			LogicOp = LogicOp.Copy,
			AttachmentCount = (uint)request.BlendAttachments.Length,
		};

		fixed (VertexInputBindingDescription* bindingsPtr = request.VertexBindings)
		fixed (VertexInputAttributeDescription* attrsPtr = request.VertexAttributes)
		fixed (PipelineColorBlendAttachmentState* blendPtr = request.BlendAttachments)
		fixed (DynamicState* dynPtr = request.DynamicStates)
		{
			vertexInput.PVertexBindingDescriptions = request.VertexBindings.Length > 0 ? bindingsPtr : null;
			vertexInput.PVertexAttributeDescriptions = request.VertexAttributes.Length > 0 ? attrsPtr : null;
			colorBlending.PAttachments = request.BlendAttachments.Length > 0 ? blendPtr : null;

			PipelineDynamicStateCreateInfo dynamicState = new()
			{
				SType = StructureType.PipelineDynamicStateCreateInfo,
				DynamicStateCount = (uint)request.DynamicStates.Length,
				PDynamicStates = request.DynamicStates.Length > 0 ? dynPtr : null,
			};

			PipelineInputAssemblyStateCreateInfo inputAssembly = request.InputAssembly;
			PipelineRasterizationStateCreateInfo rasterizer = request.Rasterizer;
			PipelineMultisampleStateCreateInfo multisampling = request.Multisampling;
			PipelineDepthStencilStateCreateInfo depthStencil = request.DepthStencil;

			GraphicsPipelineCreateInfo pipelineInfo = new()
			{
				SType = StructureType.GraphicsPipelineCreateInfo,
				PVertexInputState = &vertexInput,
				PInputAssemblyState = &inputAssembly,
				PViewportState = &viewportState,
				PRasterizationState = &rasterizer,
				PMultisampleState = &multisampling,
				PDepthStencilState = &depthStencil,
				PColorBlendState = &colorBlending,
				PDynamicState = &dynamicState,
				RenderPass = request.Key.UseDynamicRendering ? default : request.RenderPass,
				Subpass = 0,
			};

			if (request.Key.UseDynamicRendering)
			{
				Format* colorFormats = stackalloc Format[(int)request.ColorAttachmentCount];
				request.DynamicRenderingFormats.CopyColorAttachmentFormats(colorFormats, request.ColorAttachmentCount);

				PipelineRenderingCreateInfo renderingInfo = new()
				{
					SType = StructureType.PipelineRenderingCreateInfo,
					ViewMask = request.DynamicRenderingFormats.ViewMask,
					ColorAttachmentCount = request.ColorAttachmentCount,
					PColorAttachmentFormats = request.ColorAttachmentCount > 0 ? colorFormats : null,
					DepthAttachmentFormat = request.DynamicRenderingFormats.DepthAttachmentFormat,
					StencilAttachmentFormat = request.DynamicRenderingFormats.StencilAttachmentFormat,
				};

				pipelineInfo.PNext = &renderingInfo;
				return CreateGraphicsPipeline(manager, request, ref pipelineInfo, pipelineCache, backgroundCompile);
			}

			return CreateGraphicsPipeline(manager, request, ref pipelineInfo, pipelineCache, backgroundCompile);
		}
	}

	private static Pipeline CreateGraphicsPipeline(
		VulkanPipelineManager manager,
		VulkanGraphicsPipelineBuildRequest request,
		ref GraphicsPipelineCreateInfo pipelineInfo,
		PipelineCache pipelineCache,
		bool backgroundCompile)
	{
		if (request.Key.UseDynamicRendering && request.ColorAttachmentCount == 0)
		{
			Debug.VulkanWarningEvery(
				"Vulkan.PipelineLibrary.DepthOnlyMonolithic",
				TimeSpan.FromSeconds(5),
				"[Vulkan] Using monolithic dynamic-rendering pipeline for depth-only pass '{0}' program='{1}'; graphics pipeline libraries are bypassed for zero-color pipelines to keep depth/stencil validation correct.",
				request.PipelineName,
				request.Program.Data.Name ?? "<unnamed program>");
			return CreateMonolithicGraphicsPipeline(manager, request, ref pipelineInfo, pipelineCache, backgroundCompile);
		}

		if (!request.UseGraphicsPipelineLibraries)
			return CreateMonolithicGraphicsPipeline(manager, request, ref pipelineInfo, pipelineCache, backgroundCompile);

		try
		{
			return CreateGraphicsPipelineFromLibraries(manager, request, ref pipelineInfo, pipelineCache, backgroundCompile);
		}
		catch (InvalidOperationException ex)
		{
			Debug.VulkanWarningEvery(
				$"Vulkan.PipelineLibrary.Fallback.{request.Program.Data.Name ?? "UnknownProgram"}",
				TimeSpan.FromSeconds(5),
				"[Vulkan] Graphics pipeline library creation failed for pipeline '{0}' program='{1}'; falling back to monolithic pipeline. {2}",
				request.PipelineName,
				request.Program.Data.Name ?? "<unnamed program>",
				ex.Message);
			return CreateMonolithicGraphicsPipeline(manager, request, ref pipelineInfo, pipelineCache, backgroundCompile);
		}
	}

	private static Pipeline CreateMonolithicGraphicsPipeline(
		VulkanPipelineManager manager,
		VulkanGraphicsPipelineBuildRequest request,
		ref GraphicsPipelineCreateInfo pipelineInfo,
		PipelineCache pipelineCache,
		bool backgroundCompile)
	{
		if (request.GraphicsStages.Length == 0)
			throw new InvalidOperationException("graphics pipeline creation requires at least one graphics shader stage.");

		fixed (PipelineShaderStageCreateInfo* stagesPtr = request.GraphicsStages)
		{
			pipelineInfo.StageCount = (uint)request.GraphicsStages.Length;
			pipelineInfo.PStages = stagesPtr;
			pipelineInfo.Layout = request.PipelineLayout;

			Result result = manager.CreateGraphicsPipelineWithCachePolicy(
				ref pipelineInfo,
				pipelineCache,
				backgroundCompile,
				out Pipeline pipeline);
			if (result != Result.Success)
				throw new InvalidOperationException($"failed to create graphics pipeline ({result}).");

			request.ProgramServices.RegisterPipeline(pipeline, "VkMeshRenderer.Graphics");
			request.ProgramServices.NotifyPipelineCreated("graphics");
			return pipeline;
		}
	}

	private static Pipeline CreateGraphicsPipelineFromLibraries(
		VulkanPipelineManager manager,
		VulkanGraphicsPipelineBuildRequest request,
		ref GraphicsPipelineCreateInfo pipelineInfo,
		PipelineCache pipelineCache,
		bool backgroundCompile)
	{
		if (request.PreRasterStages.Length == 0)
			throw new InvalidOperationException("graphics pipeline libraries require a pre-rasterization shader stage.");

		if (!request.PreRasterStages.Any(static stage => stage.Stage == ShaderStageFlags.VertexBit))
			throw new InvalidOperationException("graphics pipeline library path currently supports vertex-input mesh pipelines only.");

		Pipeline vertexInput = EnsureGraphicsPipelineLibrary(
			manager,
			request,
			CreateGraphicsPipelineLibraryKey(VulkanGraphicsPipelineLibrarySubset.VertexInputInterface, request.Key),
			ref pipelineInfo,
			Array.Empty<PipelineShaderStageCreateInfo>(),
			GraphicsPipelineLibraryFlagsEXT.VertexInputInterfaceBitExt,
			pipelineCache,
			backgroundCompile);

		Pipeline preRasterization = EnsureGraphicsPipelineLibrary(
			manager,
			request,
			CreateGraphicsPipelineLibraryKey(VulkanGraphicsPipelineLibrarySubset.PreRasterizationShaders, request.Key),
			ref pipelineInfo,
			request.PreRasterStages,
			GraphicsPipelineLibraryFlagsEXT.PreRasterizationShadersBitExt,
			pipelineCache,
			backgroundCompile);

		List<Pipeline> libraries =
		[
			vertexInput,
			preRasterization,
		];

		if (request.FragmentStages.Length > 0)
		{
			Pipeline fragmentShader = EnsureGraphicsPipelineLibrary(
				manager,
				request,
				CreateGraphicsPipelineLibraryKey(VulkanGraphicsPipelineLibrarySubset.FragmentShader, request.Key),
				ref pipelineInfo,
				request.FragmentStages,
				GraphicsPipelineLibraryFlagsEXT.FragmentShaderBitExt,
				pipelineCache,
				backgroundCompile);
			libraries.Add(fragmentShader);
		}

		Pipeline fragmentOutput = EnsureGraphicsPipelineLibrary(
			manager,
			request,
			CreateGraphicsPipelineLibraryKey(VulkanGraphicsPipelineLibrarySubset.FragmentOutputInterface, request.Key),
			ref pipelineInfo,
			Array.Empty<PipelineShaderStageCreateInfo>(),
			GraphicsPipelineLibraryFlagsEXT.FragmentOutputInterfaceBitExt,
			pipelineCache,
			backgroundCompile);
		libraries.Add(fragmentOutput);

		Pipeline[] libraryArray = [.. libraries];
		fixed (Pipeline* librariesPtr = libraryArray)
		{
			PipelineLibraryCreateInfoKHR libraryInfo = new()
			{
				SType = StructureType.PipelineLibraryCreateInfoKhr,
				LibraryCount = (uint)libraryArray.Length,
				PLibraries = librariesPtr,
			};

			bool linkUsesDynamicRenderingInfo =
				request.Key.UseDynamicRendering &&
				pipelineInfo.PNext != null &&
				((PipelineRenderingCreateInfo*)pipelineInfo.PNext)->SType == StructureType.PipelineRenderingCreateInfo;
			PipelineRenderingCreateInfo linkedRenderingInfo = default;
			if (linkUsesDynamicRenderingInfo)
			{
				linkedRenderingInfo = *((PipelineRenderingCreateInfo*)pipelineInfo.PNext);
				linkedRenderingInfo.PNext = &libraryInfo;
			}

			GraphicsPipelineCreateInfo linkedInfo = pipelineInfo;
			linkedInfo.PNext = &libraryInfo;
			if (linkUsesDynamicRenderingInfo)
				linkedInfo.PNext = &linkedRenderingInfo;
			linkedInfo.StageCount = 0;
			linkedInfo.PStages = null;
			linkedInfo.PVertexInputState = null;
			linkedInfo.PInputAssemblyState = null;
			linkedInfo.PViewportState = null;
			linkedInfo.PRasterizationState = null;
			linkedInfo.PDynamicState = null;
			linkedInfo.Layout = request.PipelineLayout;

			long linkStart = global::System.Diagnostics.Stopwatch.GetTimestamp();
			Result result = manager.CreateGraphicsPipelineWithCachePolicy(
				ref linkedInfo,
				pipelineCache,
				backgroundCompile,
				out Pipeline pipeline);
			TimeSpan linkElapsed = global::System.Diagnostics.Stopwatch.GetElapsedTime(linkStart);
			if (result != Result.Success)
				throw new InvalidOperationException($"failed to link graphics pipeline libraries ({result}) after {linkElapsed.TotalMilliseconds:F2} ms.");

			request.ProgramServices.RegisterPipeline(pipeline, "VkMeshRenderer.GraphicsLibraryLink");
			if (linkElapsed.TotalMilliseconds >= 16.0)
			{
				Debug.VulkanWarningEvery(
					$"Vulkan.PipelineLibrary.LinkSlow.{request.Program.Data.Name ?? "UnknownProgram"}",
					TimeSpan.FromSeconds(2),
					"[Vulkan] Graphics pipeline library link took {0:F2} ms: program='{1}' libraries={2} dynamicRendering={3} renderPass=0x{4:X}",
					linkElapsed.TotalMilliseconds,
					request.Program.Data.Name ?? "<unnamed program>",
					libraryArray.Length,
					request.Key.UseDynamicRendering,
					request.Key.RenderPassHandle);
			}

			request.ProgramServices.NotifyPipelineCreated("graphics-library-linked");
			return pipeline;
		}
	}

	private static VulkanGraphicsPipelineLibraryKey CreateGraphicsPipelineLibraryKey(
		VulkanGraphicsPipelineLibrarySubset subset,
		in VulkanGraphicsPipelineKey pipeline)
	{
		bool hasRenderPassIdentity = subset is
			VulkanGraphicsPipelineLibrarySubset.PreRasterizationShaders or
			VulkanGraphicsPipelineLibrarySubset.FragmentShader or
			VulkanGraphicsPipelineLibrarySubset.FragmentOutputInterface;
		bool usesDynamicRenderingIdentity = hasRenderPassIdentity && pipeline.UseDynamicRendering;
		DynamicRenderingFormatSignature dynamicRenderingFormats = CreateGraphicsPipelineLibraryDynamicRenderingFormatSignature(subset, pipeline);
		bool hasTopology = subset == VulkanGraphicsPipelineLibrarySubset.VertexInputInterface;
		bool hasProgram = subset is VulkanGraphicsPipelineLibrarySubset.PreRasterizationShaders or VulkanGraphicsPipelineLibrarySubset.FragmentShader;
		bool hasVertexLayout = subset == VulkanGraphicsPipelineLibrarySubset.VertexInputInterface;
		bool hasDepthStencil = subset is VulkanGraphicsPipelineLibrarySubset.FragmentShader or VulkanGraphicsPipelineLibrarySubset.FragmentOutputInterface;
		bool hasRasterState = subset == VulkanGraphicsPipelineLibrarySubset.PreRasterizationShaders;
		bool hasBlendState = subset == VulkanGraphicsPipelineLibrarySubset.FragmentOutputInterface;
		bool hasSampleState = subset is VulkanGraphicsPipelineLibrarySubset.FragmentShader or VulkanGraphicsPipelineLibrarySubset.FragmentOutputInterface;

		return new VulkanGraphicsPipelineLibraryKey(
			subset,
			usesDynamicRenderingIdentity,
			hasRenderPassIdentity && !pipeline.UseDynamicRendering ? pipeline.RenderPassHandle : 0UL,
			usesDynamicRenderingIdentity ? dynamicRenderingFormats : default,
			hasTopology ? pipeline.Topology : default,
			hasProgram ? pipeline.ProgramPipelineHash : 0UL,
			hasProgram ? pipeline.ProgramLinkGeneration : 0UL,
			hasVertexLayout ? pipeline.VertexLayoutHash : 0UL,
			hasProgram ? pipeline.DescriptorLayoutHash : 0UL,
			hasProgram || hasVertexLayout || hasRasterState ? pipeline.FeatureProfileHash : 0UL,
			hasSampleState ? pipeline.RasterizationSamples : default,
			hasDepthStencil && pipeline.DepthTestEnabled,
			hasDepthStencil && pipeline.DepthWriteEnabled,
			hasDepthStencil ? pipeline.DepthCompareOp : default,
			hasDepthStencil && pipeline.StencilTestEnabled,
			hasDepthStencil ? pipeline.FrontStencilState : default,
			hasDepthStencil ? pipeline.BackStencilState : default,
			hasDepthStencil ? pipeline.StencilWriteMask : 0u,
			hasRasterState ? pipeline.CullMode : default,
			hasRasterState ? pipeline.FrontFace : default,
			hasBlendState && pipeline.BlendEnabled,
			hasBlendState && pipeline.AlphaToCoverageEnabled,
			hasBlendState ? pipeline.ColorBlendOp : default,
			hasBlendState ? pipeline.AlphaBlendOp : default,
			hasBlendState ? pipeline.SrcColorBlendFactor : default,
			hasBlendState ? pipeline.DstColorBlendFactor : default,
			hasBlendState ? pipeline.SrcAlphaBlendFactor : default,
			hasBlendState ? pipeline.DstAlphaBlendFactor : default,
			hasBlendState ? pipeline.ColorWriteMask : default,
			hasRasterState ? Math.Max(pipeline.ViewportScissorCount, 1u) : 1u,
			hasRasterState && pipeline.NativeNegativeOneToOneDepth);
	}

	private static DynamicRenderingFormatSignature CreateGraphicsPipelineLibraryDynamicRenderingFormatSignature(
		VulkanGraphicsPipelineLibrarySubset subset,
		in VulkanGraphicsPipelineKey pipeline)
	{
		if (!pipeline.UseDynamicRendering)
			return default;

		return subset switch
		{
			VulkanGraphicsPipelineLibrarySubset.PreRasterizationShaders or
			VulkanGraphicsPipelineLibrarySubset.FragmentShader => new DynamicRenderingFormatSignature(
				ReadOnlySpan<Format>.Empty,
				Format.Undefined,
				Format.Undefined,
				pipeline.DynamicRenderingFormats.ViewMask,
				pipeline.DynamicRenderingFormats.LayerCount),
			VulkanGraphicsPipelineLibrarySubset.FragmentOutputInterface => pipeline.DynamicRenderingFormats,
			_ => default,
		};
	}

	private static Pipeline EnsureGraphicsPipelineLibrary(
		VulkanPipelineManager manager,
		VulkanGraphicsPipelineBuildRequest request,
		VulkanGraphicsPipelineLibraryKey key,
		ref GraphicsPipelineCreateInfo baseInfo,
		PipelineShaderStageCreateInfo[] stages,
		GraphicsPipelineLibraryFlagsEXT libraryFlags,
		PipelineCache pipelineCache,
		bool backgroundCompile)
	{
		if (manager.TryGetOrReserveSharedGraphicsPipelineLibrary(
				key,
				out Pipeline cachedLibrary,
				out bool creationReserved))
		{
			return cachedLibrary;
		}

		if (!creationReserved)
		{
			throw new VulkanPipelineCompilationDeferredException(
				$"{key.Subset} graphics pipeline library creation is already in flight.");
		}

		try
		{
			fixed (PipelineShaderStageCreateInfo* stagesPtr = stages)
			{
				bool includeDynamicRenderingInfo = key.UseDynamicRendering;
				PipelineRenderingCreateInfo libraryRenderingInfo = default;
				uint libraryColorAttachmentCount = includeDynamicRenderingInfo
					? key.DynamicRenderingFormats.ColorAttachmentCount
					: 0u;
				Format* libraryColorFormats = stackalloc Format[(int)Math.Max(libraryColorAttachmentCount, 1u)];
				if (libraryColorAttachmentCount > 0u)
					key.DynamicRenderingFormats.CopyColorAttachmentFormats(libraryColorFormats, libraryColorAttachmentCount);

				if (includeDynamicRenderingInfo)
				{
					libraryRenderingInfo = new PipelineRenderingCreateInfo
					{
						SType = StructureType.PipelineRenderingCreateInfo,
						ViewMask = key.DynamicRenderingFormats.ViewMask,
						ColorAttachmentCount = libraryColorAttachmentCount,
						PColorAttachmentFormats = libraryColorAttachmentCount > 0u ? libraryColorFormats : null,
						DepthAttachmentFormat = key.DynamicRenderingFormats.DepthAttachmentFormat,
						StencilAttachmentFormat = key.DynamicRenderingFormats.StencilAttachmentFormat,
					};
				}

				GraphicsPipelineLibraryCreateInfoEXT libraryInfo = new()
				{
					SType = StructureType.GraphicsPipelineLibraryCreateInfoExt,
					PNext = includeDynamicRenderingInfo ? &libraryRenderingInfo : null,
					Flags = libraryFlags,
				};

				GraphicsPipelineCreateInfo libraryPipelineInfo = baseInfo;
				libraryPipelineInfo.Flags |= PipelineCreateFlags.CreateLibraryBitKhr;
				libraryPipelineInfo.PNext = &libraryInfo;
				libraryPipelineInfo.StageCount = (uint)stages.Length;
				libraryPipelineInfo.PStages = stages.Length > 0 ? stagesPtr : null;
				libraryPipelineInfo.Layout = request.PipelineLayout;

				ApplyGraphicsPipelineLibrarySubset(ref libraryPipelineInfo, key.Subset);

				long createStart = global::System.Diagnostics.Stopwatch.GetTimestamp();
				Result result = manager.CreateGraphicsPipelineWithCachePolicy(
					ref libraryPipelineInfo,
					pipelineCache,
					backgroundCompile,
					out Pipeline library);
				TimeSpan createElapsed = global::System.Diagnostics.Stopwatch.GetElapsedTime(createStart);
				if (result != Result.Success)
					throw new InvalidOperationException($"failed to create {key.Subset} graphics pipeline library ({result}) after {createElapsed.TotalMilliseconds:F2} ms.");

				request.ProgramServices.RegisterPipeline(library, $"VkMeshRenderer.GraphicsLibrary.{key.Subset}");
				Pipeline cachedOrCreated = manager.CompleteSharedGraphicsPipelineLibraryCreation(key, library);
				if (cachedOrCreated.Handle != library.Handle)
				{
					request.ProgramServices.RetirePipeline(library);
					return cachedOrCreated;
				}

				if (createElapsed.TotalMilliseconds >= 16.0)
				{
					Debug.VulkanWarningEvery(
						$"Vulkan.PipelineLibrary.CreateSlow.{key.Subset}.{request.Program.Data.Name ?? "UnknownProgram"}",
						TimeSpan.FromSeconds(2),
						"[Vulkan] Graphics pipeline library create took {0:F2} ms: subset={1} program='{2}' dynamicRendering={3} renderPass=0x{4:X} colors={5} depth={6} stencil={7}",
						createElapsed.TotalMilliseconds,
						key.Subset,
						request.Program.Data.Name ?? "<unnamed program>",
						key.UseDynamicRendering,
						key.RenderPassHandle,
						key.DynamicRenderingFormats.DescribeColorFormats(),
						key.DynamicRenderingFormats.DepthAttachmentFormat,
						key.DynamicRenderingFormats.StencilAttachmentFormat);
				}

				request.ProgramServices.NotifyPipelineCreated($"graphics-library:{key.Subset}");
				return library;
			}
		}
		catch
		{
			manager.CancelSharedGraphicsPipelineLibraryCreation(key);
			throw;
		}
	}

	internal static PipelineShaderStageCreateInfo[] GetGraphicsPipelineLibraryStages(
		VkRenderProgram program,
		EProgramStageMask mask,
		uint colorAttachmentCount)
	{
		PipelineShaderStageCreateInfo[] stages = program.GetShaderStages(mask).ToArray();
		if (colorAttachmentCount == 0)
			stages = stages.Where(static stage => stage.Stage != ShaderStageFlags.FragmentBit).ToArray();

		return stages;
	}

	private static void ApplyGraphicsPipelineLibrarySubset(
		ref GraphicsPipelineCreateInfo pipelineInfo,
		VulkanGraphicsPipelineLibrarySubset subset)
	{
		switch (subset)
		{
			case VulkanGraphicsPipelineLibrarySubset.VertexInputInterface:
				pipelineInfo.PViewportState = null;
				pipelineInfo.PRasterizationState = null;
				pipelineInfo.PMultisampleState = null;
				pipelineInfo.PDepthStencilState = null;
				pipelineInfo.PColorBlendState = null;
				pipelineInfo.PDynamicState = null;
				break;
			case VulkanGraphicsPipelineLibrarySubset.PreRasterizationShaders:
				pipelineInfo.PVertexInputState = null;
				pipelineInfo.PInputAssemblyState = null;
				pipelineInfo.PDepthStencilState = null;
				pipelineInfo.PColorBlendState = null;
				break;
			case VulkanGraphicsPipelineLibrarySubset.FragmentShader:
				pipelineInfo.PVertexInputState = null;
				pipelineInfo.PInputAssemblyState = null;
				pipelineInfo.PViewportState = null;
				pipelineInfo.PRasterizationState = null;
				pipelineInfo.PColorBlendState = null;
				pipelineInfo.PDynamicState = null;
				break;
			case VulkanGraphicsPipelineLibrarySubset.FragmentOutputInterface:
				pipelineInfo.StageCount = 0;
				pipelineInfo.PStages = null;
				pipelineInfo.PVertexInputState = null;
				pipelineInfo.PInputAssemblyState = null;
				pipelineInfo.PViewportState = null;
				pipelineInfo.PRasterizationState = null;
				pipelineInfo.PDynamicState = null;
				break;
		}
	}

}
