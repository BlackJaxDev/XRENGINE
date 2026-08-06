using Silk.NET.Vulkan;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const string VulkanPipelinePrewarmCaptureEnvVar = XREngineEnvironmentVariables.VulkanPipelinePrewarmCapture;
    private const int VulkanPipelinePrewarmAutoSaveEntryThreshold = 16;

    private void InitializeVulkanPipelinePrewarmDatabase(PhysicalDeviceProperties properties)
    {
        string cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XREngine",
            "Vulkan",
            "PipelinePrewarm");

        string deviceProfile =
            $"v{VulkanPipelinePrewarmDatabase.CurrentVersion}_{properties.VendorID:X8}_{properties.DeviceID:X8}_{properties.DriverVersion:X8}_{properties.ApiVersion:X8}_{VulkanFeatureProfile.ActiveProfile}";

        string filePath = Path.Combine(cacheDir, $"prewarm_{deviceProfile}.json");
        bool captureEnabled = string.Equals(
            Environment.GetEnvironmentVariable(VulkanPipelinePrewarmCaptureEnvVar),
            "0",
            StringComparison.OrdinalIgnoreCase) == false;
        VulkanPipelinePrewarmDatabase database =
            VulkanPipelinePrewarmDatabase.LoadOrCreate(filePath, deviceProfile);
        ResourceRuntime.PipelineManager.ConfigurePrewarmDatabase(database, filePath, captureEnabled);

        Debug.Vulkan(
            "[Vulkan] Pipeline prewarm database loaded (path={0}, entries={1}, capture={2}).",
            filePath,
            database.EntryCount,
            captureEnabled);
    }

    private void SaveVulkanPipelinePrewarmDatabase()
    {
        if (!ResourceRuntime.PipelineManager.PrewarmCaptureEnabled ||
            ResourceRuntime.PipelineManager.PrewarmDatabase is null ||
            !ResourceRuntime.PipelineManager.PrewarmDatabase.Dirty ||
            string.IsNullOrWhiteSpace(ResourceRuntime.PipelineManager.PrewarmDatabaseFilePath))
        {
            return;
        }

        try
        {
            ResourceRuntime.PipelineManager.PrewarmDatabase.Save(ResourceRuntime.PipelineManager.PrewarmDatabaseFilePath);
            Debug.Vulkan(
                "[Vulkan] Pipeline prewarm database saved ({0} entries).",
                ResourceRuntime.PipelineManager.PrewarmDatabase.EntryCount);
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning($"[Vulkan] Failed to save pipeline prewarm database '{ResourceRuntime.PipelineManager.PrewarmDatabaseFilePath}': {ex.Message}");
        }
    }

    private void QueueVulkanPipelinePrewarmDatabaseAutoSave()
    {
        if (!ResourceRuntime.PipelineManager.TryBeginPrewarmAutoSave(
                VulkanPipelinePrewarmAutoSaveEntryThreshold,
                out VulkanPipelinePrewarmDatabase database,
                out string path))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                database.Save(path);
                Debug.Vulkan("[Vulkan] Pipeline prewarm database auto-saved ({0} entries).", database.EntryCount);
            }
            catch (Exception ex)
            {
                Debug.VulkanWarning($"[Vulkan] Failed to auto-save pipeline prewarm database '{path}': {ex.Message}");
            }
            finally
            {
                if (ResourceRuntime.PipelineManager.CompletePrewarmAutoSave(VulkanPipelinePrewarmAutoSaveEntryThreshold))
                    QueueVulkanPipelinePrewarmDatabaseAutoSave();
            }
        });
    }
    internal bool RecordVulkanGraphicsPipelineCacheMiss(
        int passIndex,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        string pipelineName,
        string? meshName,
        XRMaterial material,
        string? programName,
        PrimitiveTopology topology,
        bool useDynamicRendering,
        RenderPass renderPass,
        DynamicRenderingFormatSignature dynamicRenderingFormats,
        ulong programPipelineHash,
        ulong vertexLayoutHash,
        ulong descriptorLayoutHash,
        ulong passMetadataHash,
        ulong featureProfileHash,
        ulong fixedFunctionStateHash,
        SampleCountFlags rasterizationSamples,
        bool depthTestEnabled,
        bool blendEnabled,
        bool alphaToCoverageEnabled,
        ColorComponentFlags colorWriteMask)
    {
        string passName = ResolveRenderPassName(passIndex, passMetadata);
        string resolvedProgramName = string.IsNullOrWhiteSpace(programName) ? "UnnamedProgram" : programName!;
        string resolvedMeshName = string.IsNullOrWhiteSpace(meshName) ? "UnnamedMesh" : meshName!;
        string materialName = string.IsNullOrWhiteSpace(material.Name) ? "UnnamedMaterial" : material.Name!;
        string effectName = ResolveMaterialEffectName(material);
        string profileName = VulkanFeatureProfile.ActiveProfile.ToString();
        string renderPassSignature = useDynamicRendering
            ? BuildDynamicRenderingSignature(dynamicRenderingFormats)
            : GetRenderPassSemanticSignature(renderPass);
        string colorAttachmentFormats = useDynamicRendering
            ? dynamicRenderingFormats.DescribeColorFormats()
            : Format.Undefined.ToString();
        string depthAttachmentFormat = useDynamicRendering
            ? dynamicRenderingFormats.DepthAttachmentFormat.ToString()
            : Format.Undefined.ToString();

        VulkanPipelinePrewarmEntry entry = VulkanPipelinePrewarmDatabase.CreateGraphicsEntry(
            passIndex,
            passName,
            pipelineName,
            resolvedMeshName,
            materialName,
            resolvedProgramName,
            effectName,
            topology,
            useDynamicRendering,
            renderPassSignature,
            colorAttachmentFormats,
            depthAttachmentFormat,
            programPipelineHash,
            vertexLayoutHash,
            descriptorLayoutHash,
            passMetadataHash,
            featureProfileHash,
            fixedFunctionStateHash,
            rasterizationSamples,
            depthTestEnabled,
            blendEnabled,
            alphaToCoverageEnabled,
            colorWriteMask,
            profileName);

        bool shouldAutoSave = ResourceRuntime.PipelineManager.RecordPrewarmEntry(
            entry,
            countForAutoSave: true,
            out bool knownAtStartup);
        if (shouldAutoSave)
            QueueVulkanPipelinePrewarmDatabaseAutoSave();
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineCacheMiss(entry.ToProfilerSummary(knownAtStartup));
        return knownAtStartup;
    }

    internal void RecordVulkanComputePipelineCacheMiss(
        int passIndex,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        VkRenderProgram program,
        ulong programPipelineHash)
    {
        string passName = ResolveRenderPassName(passIndex, passMetadata);
        string pipelineName = RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.DebugName ?? "<no pipeline>";
        string programName = program.Data.Name ?? "UnnamedProgram";
        string profileName = VulkanFeatureProfile.ActiveProfile.ToString();

        VulkanPipelinePrewarmEntry entry = VulkanPipelinePrewarmDatabase.CreateComputeEntry(
            passIndex,
            passName,
            pipelineName,
            programName,
            programPipelineHash,
            profileName);

        ResourceRuntime.PipelineManager.RecordPrewarmEntry(
            entry,
            countForAutoSave: false,
            out bool knownAtStartup);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineCacheMiss(entry.ToProfilerSummary(knownAtStartup));
    }

    private static string ResolveRenderPassName(int passIndex, IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        if (passMetadata is not null)
        {
            foreach (RenderPassMetadata metadata in passMetadata)
            {
                if (metadata.PassIndex == passIndex)
                    return metadata.Name;
            }
        }

        return passIndex == VulkanBarrierPlanner.SwapchainPassIndex
            ? "Swapchain"
            : "UnknownPass";
    }

    private static string ResolveMaterialEffectName(XRMaterial material)
    {
        if (material.Shaders.Count == 0)
            return "<no shaders>";

        return string.Join("+", material.Shaders.Select(static shader =>
            shader.Name ??
            shader.Source?.Name ??
            shader.Type.ToString()));
    }

}
