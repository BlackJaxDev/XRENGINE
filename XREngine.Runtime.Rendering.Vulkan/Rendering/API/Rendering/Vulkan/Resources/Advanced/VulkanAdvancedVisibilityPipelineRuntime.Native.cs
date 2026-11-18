using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanAdvancedVisibilityPipelineRuntime
{
    private const int RvcShadeletTelemetryProgramIndex = 7;
    internal const string RvcShadeletTelemetryShaderPath = "Advanced/Rvc/RvcShadeletTelemetry.comp";
    private const int FoveatedQuadFillProgramIndex = 8;
    internal const string FoveatedQuadFillShaderPath = "Advanced/Shading/FoveatedQuadFill.comp";

    private const int ShadeStandardProgramIndex = 5;
    private const int ShadeStandardMultisampleProgramIndex = 6;
    private const int ShadeUberProgramIndex = 9;
    private const int ShadeCombinedMultisampleProgramIndex = 10;
    private const int OcclusionCulledBoundsProgramIndex = 11;
    private const int ShadeStandardLeanProgramIndex = 12;
    private const string OcclusionCulledBoundsShaderPath = "Advanced/Visibility/OcclusionCulledBounds.comp";
    private const string ShadeShaderPath = "Advanced/Shading/ShadeNativeOpaque.comp";
    private const string ShadeMultisampleShaderPath = "Advanced/Shading/ShadeNativeOpaqueMsaa.comp";
    // Matches XR_ADV_NATIVE_SHADING_FAMILY_* in ShadeNativeOpaqueEvaluator.glslinc.
    private const string StandardFamilyPreamble = "#define XR_ADV_NATIVE_SHADING_FAMILY 1\n";
    private const string UberFamilyPreamble = "#define XR_ADV_NATIVE_SHADING_FAMILY 2\n";
    // Matches XR_ADV_SHADE_LEAN in ShadeNativeOpaqueEvaluator.glslinc.
    private const string StandardLeanPreamble = StandardFamilyPreamble + "#define XR_ADV_SHADE_LEAN 1\n";

    private readonly XRRenderProgram?[] _nativeComputePrograms = new XRRenderProgram?[13];

    /// <summary>
    /// By default the native Uber kernel compiles in the background after the standard
    /// kernel is ready, also in scenes without Uber materials, so an Uber model that loads
    /// later shades at once. <c>XRE_ADVANCED_UBER_KERNEL_PREWARM=0</c> compiles it only when
    /// the scene has Uber shading kernels.
    /// </summary>
    internal static bool PrewarmUberShadingKernels { get; } =
        XREnvironment.IsEnabled(XREngineEnvironmentVariables.AdvancedUberKernelPrewarm, defaultValue: true);

    /// <summary>
    /// Returns the native Uber kernels for a scene that has Uber shading kernels. The first
    /// call creates the programs and requests their compile; it does not wait. Call it only
    /// after the standard family is ready, so the Uber compile never delays standard shading.
    /// A pipeline that is not ready stays default; the standard kernel then uses its fallback.
    /// Only the kernel for the current visibility mode is requested, so a single-sample view
    /// never compiles the multisample kernel, and the reverse.
    /// </summary>
    internal void TryGetUberShadingPipelines(bool multisample,
        out VulkanAdvancedComputePipeline uber, out VulkanAdvancedComputePipeline combinedMultisample)
    {
        uber = default;
        combinedMultisample = default;
        if (multisample)
        {
            if (TryGetNativeComputePipeline(ShadeCombinedMultisampleProgramIndex, ShadeMultisampleShaderPath,
                    out combinedMultisample, out _) != VulkanAdvancedVisibilityPipelineReadiness.Ready)
                combinedMultisample = default;
            return;
        }
        if (TryGetNativeComputePipeline(ShadeUberProgramIndex, ShadeShaderPath, out uber, out _) !=
            VulkanAdvancedVisibilityPipelineReadiness.Ready)
            uber = default;
    }

    /// <summary>
    /// Returns the occlusion-culled bounds overlay kernel. The first call creates the
    /// program and requests its compile; it does not wait. A kernel that is not ready
    /// returns default, and shading then records no overlay for that frame.
    /// </summary>
    internal VulkanAdvancedComputePipeline TryGetOcclusionCulledBoundsPipeline()
        => TryGetNativeComputePipeline(OcclusionCulledBoundsProgramIndex, OcclusionCulledBoundsShaderPath,
            out VulkanAdvancedComputePipeline pipeline, out _) == VulkanAdvancedVisibilityPipelineReadiness.Ready
            ? pipeline
            : default;

    /// <summary>Program name and preamble prefix for one native program slot.</summary>
    private static (string Name, string Preamble) ResolveNativeProgramIdentity(int index, string path)
        => index switch
        {
            ShadeStandardProgramIndex => ("Advanced/Shading/ShadeNativeOpaqueStandard", StandardFamilyPreamble),
            ShadeStandardMultisampleProgramIndex => ("Advanced/Shading/ShadeNativeOpaqueStandardMsaa", StandardFamilyPreamble),
            ShadeUberProgramIndex => ("Advanced/Shading/ShadeNativeOpaqueUber", UberFamilyPreamble),
            ShadeStandardLeanProgramIndex => ("Advanced/Shading/ShadeNativeOpaqueStandardLean", StandardLeanPreamble),
            ShadeCombinedMultisampleProgramIndex => ("Advanced/Shading/ShadeNativeOpaqueCombinedMsaa", string.Empty),
            // The overlay applies the same cluster LOD cut as the meshlet task stage, so it
            // compiles with the same virtual geometry defines.
            OcclusionCulledBoundsProgramIndex => (path, global::XREngine.Rendering.Commands.AdvancedVirtualGeometryPolicy.TaskPreamble),
            _ => (path, string.Empty),
        };

    /// <summary>
    /// Optional slots (the Uber kernels and the occlusion-culled bounds overlay): created
    /// lazily and never part of admission.
    /// </summary>
    private static bool IsOptionalNativeProgram(int index)
        => index is ShadeUberProgramIndex or ShadeCombinedMultisampleProgramIndex or
            OcclusionCulledBoundsProgramIndex;

    private static bool IsNativeShadingProgram(int index)
        => index is ShadeStandardProgramIndex or ShadeStandardMultisampleProgramIndex or
            ShadeUberProgramIndex or ShadeCombinedMultisampleProgramIndex or ShadeStandardLeanProgramIndex;

    internal VulkanAdvancedVisibilityPipelineReadiness TryGetNativeComputePipelines(
        bool multisampleVisibility,
        out VulkanAdvancedNativeComputePipelines pipelines, out string reason)
    {
        pipelines = default;
        VulkanAdvancedVisibilityPipelineReadiness readiness = GetReadiness(out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
            return readiness;
        pipelines = _preparedNativeComputePipelines;
        if (multisampleVisibility)
        {
            readiness = GetMultisampleNativeShadingReadiness(out VulkanAdvancedComputePipeline shade, out reason);
            if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
            {
                pipelines = default;
                return readiness;
            }
            pipelines = pipelines with { ShadeMultisample = shade };
        }
        return readiness;
    }

    private VulkanAdvancedVisibilityPipelineReadiness PrepareNativeComputePipelines(
        out VulkanAdvancedNativeComputePipelines pipelines, out string reason)
    {
        pipelines = default;
        VulkanAdvancedVisibilityPipelineReadiness readiness = TryGetNativeComputePipeline(0,
            "Advanced/Classification/ClassifyTiles.comp", out VulkanAdvancedComputePipeline classify, out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready) return readiness;
        readiness = TryGetNativeComputePipeline(1, "Advanced/Classification/BuildClassificationIndirect.comp",
            out VulkanAdvancedComputePipeline arguments, out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready) return readiness;
        readiness = TryGetNativeComputePipeline(2, "Advanced/AO/Gtao.comp",
            out VulkanAdvancedComputePipeline ambientOcclusion, out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready) return readiness;
        readiness = TryGetNativeComputePipeline(3, "Advanced/Lighting/BuildFroxels.comp",
            out VulkanAdvancedComputePipeline froxels, out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready) return readiness;
        readiness = TryGetNativeComputePipeline(4, "Advanced/Shading/ShadeBackground.comp",
            out VulkanAdvancedComputePipeline background, out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready) return readiness;
        readiness = TryGetNativeComputePipeline(5, "Advanced/Shading/ShadeNativeOpaque.comp",
            out VulkanAdvancedComputePipeline shade, out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready) return readiness;
        // The lean standard kernel is prepared with the family, so the first frame that can
        // use it does not wait for its compile.
        readiness = TryGetNativeComputePipeline(ShadeStandardLeanProgramIndex, ShadeShaderPath,
            out VulkanAdvancedComputePipeline shadeLean, out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready) return readiness;
        // The RVC telemetry kernel is small. It is prepared with the family so
        // a compile failure is loud at admission, not a silent missing counter.
        readiness = TryGetNativeComputePipeline(RvcShadeletTelemetryProgramIndex, RvcShadeletTelemetryShaderPath,
            out VulkanAdvancedComputePipeline telemetry, out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready) return readiness;
        readiness = TryGetNativeComputePipeline(FoveatedQuadFillProgramIndex, FoveatedQuadFillShaderPath,
            out VulkanAdvancedComputePipeline foveatedFill, out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready) return readiness;
        pipelines = new(classify, arguments, ambientOcclusion, froxels, background, shade, default)
        {
            RvcShadeletTelemetry = telemetry,
            FoveatedQuadFill = foveatedFill,
            ShadeLean = shadeLean,
        };
        return VulkanAdvancedVisibilityPipelineReadiness.Ready;
    }

    private VulkanAdvancedVisibilityPipelineReadiness TryGetNativeComputePipeline(
        int index, string path, out VulkanAdvancedComputePipeline binding, out string reason)
    {
        binding = default;
        try
        {
            bool addressRoot = IsNativeShadingProgram(index) && VulkanNativeShadingRootPolicy.UsesAddress;
            if (addressRoot && _resources.BackendObjectContext?.Supports(EVulkanDeviceCapability.BufferDeviceAddress) != true)
                throw new NotSupportedException("The requested native shading BufferDeviceAddress root requires an enabled Vulkan bufferDeviceAddress feature.");
            (string programName, string familyPreamble) = ResolveNativeProgramIdentity(index, path);
            XRRenderProgram source = _nativeComputePrograms[index] ??= CreateComputeProgram(
                path, programName, index == 0 ? ResolveClassificationPreamble() : familyPreamble + (addressRoot
                    ? $"#extension GL_EXT_buffer_reference : require\n#define XR_ADV_SHADING_ADDRESS_ROOT_VERSION {VulkanNativeShadingRootPolicy.AbiVersion}\n"
                    : string.Empty));
            VulkanAdvancedVisibilityPipelineReadiness linkReadiness = TryPrepareProgram(
                source,
                out VkRenderProgram program,
                out reason,
                path);
            if (linkReadiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
                return linkReadiness;
            VulkanComputePipelineReadiness readiness = program.TryGetOrRequestComputePipeline(
                int.MinValue, null, out Pipeline pipeline, out string detail);
            if (readiness != VulkanComputePipelineReadiness.Ready)
                return DescribeComputePipelineReadiness(readiness, path, detail, out reason);
            binding = new(program, pipeline, program.LinkGeneration, addressRoot ? VulkanNativeShadingRootPolicy.AbiVersion : 0);
            reason = "Ready";
            return VulkanAdvancedVisibilityPipelineReadiness.Ready;
        }
        catch (Exception exception)
        {
            reason = exception.Message;
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }
    }

    /// <summary>Chooses a device-local shader variant once, during cold program creation.</summary>
    private unsafe string ResolveClassificationPreamble()
    {
        VulkanBackendObjectContext context = _resources.BackendObjectContext ??
            throw new InvalidOperationException("Classification requires a published Vulkan device context.");
        PhysicalDeviceSubgroupProperties subgroup = new()
        {
            SType = StructureType.PhysicalDeviceSubgroupProperties,
        };
        PhysicalDeviceProperties2 properties = new()
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &subgroup,
        };
        context.Api.GetPhysicalDeviceProperties2(context.PhysicalDevice, &properties);
        const SubgroupFeatureFlags required = SubgroupFeatureFlags.BasicBit | SubgroupFeatureFlags.BallotBit;
        bool supported = subgroup.SubgroupSize > 1 &&
            (subgroup.SupportedStages & ShaderStageFlags.ComputeBit) != 0 &&
            (subgroup.SupportedOperations & required) == required;
        Debug.Out($"[VulkanAdvancedClassification] mode={(supported ? "SubgroupBallotScan" : "SharedHistogram")} subgroupSize={subgroup.SubgroupSize} operations={subgroup.SupportedOperations}");
        return supported
            ? "#extension GL_KHR_shader_subgroup_basic : require\n" +
              "#extension GL_KHR_shader_subgroup_ballot : require\n" +
              "#define XR_ADV_CLASSIFICATION_SUBGROUP_BALLOT 1\n"
            : string.Empty;
    }
}
