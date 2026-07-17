using System.Text.Json;
using Silk.NET.Vulkan;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

internal enum VulkanPipelinePrewarmEntryKind
{
    Graphics,
    Compute,
}

internal sealed class VulkanPipelinePrewarmDatabase
{
    internal const int CurrentVersion = 5;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly Dictionary<string, VulkanPipelinePrewarmEntry> _entriesByKey;
    private readonly HashSet<string> _keysLoadedAtStartup;
    private readonly object _sync = new();

    private VulkanPipelinePrewarmDatabase(string deviceProfile, IEnumerable<VulkanPipelinePrewarmEntry> entries)
    {
        DeviceProfile = deviceProfile;
        _entriesByKey = new Dictionary<string, VulkanPipelinePrewarmEntry>(StringComparer.Ordinal);
        _keysLoadedAtStartup = new HashSet<string>(StringComparer.Ordinal);

        foreach (VulkanPipelinePrewarmEntry entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.Key))
            {
                _entriesByKey[entry.Key] = entry;
                _keysLoadedAtStartup.Add(entry.Key);
            }
        }
    }

    public string DeviceProfile { get; }
    public int EntryCount
    {
        get
        {
            lock (_sync)
                return _entriesByKey.Count;
        }
    }

    public bool Dirty { get; private set; }

    public static VulkanPipelinePrewarmDatabase LoadOrCreate(string path, string deviceProfile)
    {
        if (!File.Exists(path))
            return new VulkanPipelinePrewarmDatabase(deviceProfile, Array.Empty<VulkanPipelinePrewarmEntry>());

        try
        {
            string json = File.ReadAllText(path);
            VulkanPipelinePrewarmFile? file = JsonSerializer.Deserialize<VulkanPipelinePrewarmFile>(json, JsonOptions);
            if (file is null || file.Version != CurrentVersion || !string.Equals(file.DeviceProfile, deviceProfile, StringComparison.Ordinal))
                return new VulkanPipelinePrewarmDatabase(deviceProfile, Array.Empty<VulkanPipelinePrewarmEntry>());

            return new VulkanPipelinePrewarmDatabase(deviceProfile, file.Entries ?? Array.Empty<VulkanPipelinePrewarmEntry>());
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning($"[Vulkan] Failed to read pipeline prewarm database '{path}': {ex.Message}");
            return new VulkanPipelinePrewarmDatabase(deviceProfile, Array.Empty<VulkanPipelinePrewarmEntry>());
        }
    }

    public bool Contains(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        lock (_sync)
            return _entriesByKey.ContainsKey(key);
    }

    public bool WasKnownAtStartup(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        lock (_sync)
            return _keysLoadedAtStartup.Contains(key);
    }

    public bool Record(VulkanPipelinePrewarmEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Key))
            return false;

        lock (_sync)
        {
            DateTime now = DateTime.UtcNow;
            if (_entriesByKey.TryGetValue(entry.Key, out VulkanPipelinePrewarmEntry? existing))
            {
                existing.LastSeenUtc = now;
                existing.SeenCount++;
                Dirty = true;
                return false;
            }

            entry.CreatedUtc = now;
            entry.LastSeenUtc = now;
            entry.SeenCount = Math.Max(entry.SeenCount, 1);
            _entriesByKey[entry.Key] = entry;
            Dirty = true;
            return true;
        }
    }

    public void Save(string path)
    {
        lock (_sync)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            VulkanPipelinePrewarmFile file = new()
            {
                Version = CurrentVersion,
                DeviceProfile = DeviceProfile,
                GeneratedUtc = DateTime.UtcNow,
                Entries = [.. _entriesByKey.Values
                    .OrderBy(static entry => entry.Kind)
                    .ThenBy(static entry => entry.PassIndex)
                    .ThenBy(static entry => entry.PipelineName, StringComparer.Ordinal)
                    .ThenBy(static entry => entry.ProgramName, StringComparer.Ordinal)
                    .ThenBy(static entry => entry.MaterialName, StringComparer.Ordinal)
                    .ThenBy(static entry => entry.MeshName, StringComparer.Ordinal)]
            };

            string json = JsonSerializer.Serialize(file, JsonOptions);
            File.WriteAllText(path, json);
            Dirty = false;
        }
    }

    public static VulkanPipelinePrewarmEntry CreateGraphicsEntry(
        int passIndex,
        string passName,
        string pipelineName,
        string meshName,
        string materialName,
        string programName,
        string effectName,
        PrimitiveTopology topology,
        bool useDynamicRendering,
        string renderPassSignature,
        string colorAttachmentFormats,
        string depthAttachmentFormat,
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
        ColorComponentFlags colorWriteMask,
        string featureProfile)
    {
        string key = ComputeKey(
            VulkanPipelinePrewarmEntryKind.Graphics,
            passIndex.ToString(),
            passName,
            pipelineName,
            programName,
            effectName,
            topology.ToString(),
            useDynamicRendering.ToString(),
            renderPassSignature,
            colorAttachmentFormats,
            depthAttachmentFormat,
            programPipelineHash.ToString("X16"),
            vertexLayoutHash.ToString("X16"),
            descriptorLayoutHash.ToString("X16"),
            passMetadataHash.ToString("X16"),
            featureProfileHash.ToString("X16"),
            fixedFunctionStateHash.ToString("X16"),
            rasterizationSamples.ToString(),
            depthTestEnabled.ToString(),
            blendEnabled.ToString(),
            alphaToCoverageEnabled.ToString(),
            colorWriteMask.ToString(),
            featureProfile);

        return new VulkanPipelinePrewarmEntry
        {
            Kind = VulkanPipelinePrewarmEntryKind.Graphics,
            Key = key,
            PassIndex = passIndex,
            PassName = passName,
            PipelineName = pipelineName,
            MeshName = meshName,
            MaterialName = materialName,
            ProgramName = programName,
            EffectName = effectName,
            Topology = topology.ToString(),
            UseDynamicRendering = useDynamicRendering,
            RenderPassSignature = renderPassSignature,
            ColorAttachmentFormat = colorAttachmentFormats,
            DepthAttachmentFormat = depthAttachmentFormat,
            ProgramPipelineHash = programPipelineHash,
            VertexLayoutHash = vertexLayoutHash,
            DescriptorLayoutHash = descriptorLayoutHash,
            PassMetadataHash = passMetadataHash,
            FeatureProfileHash = featureProfileHash,
            FixedFunctionStateHash = fixedFunctionStateHash,
            RasterizationSamples = rasterizationSamples.ToString(),
            DepthTestEnabled = depthTestEnabled,
            BlendEnabled = blendEnabled,
            AlphaToCoverageEnabled = alphaToCoverageEnabled,
            ColorWriteMask = colorWriteMask.ToString(),
            FeatureProfile = featureProfile,
        };
    }

    public static VulkanPipelinePrewarmEntry CreateComputeEntry(
        int passIndex,
        string passName,
        string pipelineName,
        string programName,
        ulong programPipelineHash,
        string featureProfile)
    {
        string key = ComputeKey(
            VulkanPipelinePrewarmEntryKind.Compute,
            passIndex.ToString(),
            passName,
            pipelineName,
            programName,
            programPipelineHash.ToString("X16"),
            featureProfile);

        return new VulkanPipelinePrewarmEntry
        {
            Kind = VulkanPipelinePrewarmEntryKind.Compute,
            Key = key,
            PassIndex = passIndex,
            PassName = passName,
            PipelineName = pipelineName,
            ProgramName = programName,
            ProgramPipelineHash = programPipelineHash,
            FeatureProfile = featureProfile,
        };
    }

    private static string ComputeKey(VulkanPipelinePrewarmEntryKind kind, params string[] parts)
        => $"{kind}:{string.Join('|', parts.Select(static part => SanitizeKeyPart(part)))}";

    private static string SanitizeKeyPart(string? part)
        => string.IsNullOrWhiteSpace(part)
            ? "<none>"
            : part.Replace('|', '/').Trim();
}

internal sealed class VulkanPipelinePrewarmEntry
{
    public VulkanPipelinePrewarmEntryKind Kind { get; set; }
    public string Key { get; set; } = string.Empty;
    public int PassIndex { get; set; }
    public string PassName { get; set; } = string.Empty;
    public string PipelineName { get; set; } = string.Empty;
    public string MeshName { get; set; } = string.Empty;
    public string MaterialName { get; set; } = string.Empty;
    public string ProgramName { get; set; } = string.Empty;
    public string EffectName { get; set; } = string.Empty;
    public string Topology { get; set; } = string.Empty;
    public bool UseDynamicRendering { get; set; }
    public string RenderPassSignature { get; set; } = string.Empty;
    public string ColorAttachmentFormat { get; set; } = string.Empty;
    public string DepthAttachmentFormat { get; set; } = string.Empty;
    public ulong ProgramPipelineHash { get; set; }
    public ulong VertexLayoutHash { get; set; }
    public ulong DescriptorLayoutHash { get; set; }
    public ulong PassMetadataHash { get; set; }
    public ulong FeatureProfileHash { get; set; }
    public ulong FixedFunctionStateHash { get; set; }
    public string RasterizationSamples { get; set; } = string.Empty;
    public bool DepthTestEnabled { get; set; }
    public bool BlendEnabled { get; set; }
    public bool AlphaToCoverageEnabled { get; set; }
    public string ColorWriteMask { get; set; } = string.Empty;
    public string FeatureProfile { get; set; } = string.Empty;
    public int SeenCount { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }

    public string ToProfilerSummary(bool knownAtStartup)
    {
        string status = knownAtStartup ? "known" : "new";
        return Kind == VulkanPipelinePrewarmEntryKind.Compute
            ? $"{status}:compute pass={PassIndex}:{PassName} pipe={PipelineName} program={ProgramName}"
            : $"{status}:graphics pass={PassIndex}:{PassName} pipe={PipelineName} mesh={MeshName} material={MaterialName} program={ProgramName} effect={EffectName}";
    }
}

internal sealed class VulkanPipelinePrewarmFile
{
    public int Version { get; set; }
    public string DeviceProfile { get; set; } = string.Empty;
    public DateTime GeneratedUtc { get; set; }
    public VulkanPipelinePrewarmEntry[]? Entries { get; set; }
}

public unsafe partial class VulkanRenderer
{
    private const string VulkanPipelinePrewarmCaptureEnvVar = XREngineEnvironmentVariables.VulkanPipelinePrewarmCapture;
    private const int VulkanPipelinePrewarmAutoSaveEntryThreshold = 16;

    private VulkanPipelinePrewarmDatabase? _pipelinePrewarmDatabase;
    private string? _pipelinePrewarmDatabaseFilePath;
    private bool _pipelinePrewarmCaptureEnabled;
    private int _pipelinePrewarmNewEntriesSinceSave;
    private int _pipelinePrewarmAutoSaveInFlight;

    private void InitializeVulkanPipelinePrewarmDatabase(PhysicalDeviceProperties properties)
    {
        string cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XREngine",
            "Vulkan",
            "PipelinePrewarm");

        string deviceProfile =
            $"v{VulkanPipelinePrewarmDatabase.CurrentVersion}_{properties.VendorID:X8}_{properties.DeviceID:X8}_{properties.DriverVersion:X8}_{properties.ApiVersion:X8}_{VulkanFeatureProfile.ActiveProfile}";

        _pipelinePrewarmDatabaseFilePath = Path.Combine(cacheDir, $"prewarm_{deviceProfile}.json");
        _pipelinePrewarmCaptureEnabled = string.Equals(
            Environment.GetEnvironmentVariable(VulkanPipelinePrewarmCaptureEnvVar),
            "0",
            StringComparison.OrdinalIgnoreCase) == false;

        _pipelinePrewarmDatabase = VulkanPipelinePrewarmDatabase.LoadOrCreate(_pipelinePrewarmDatabaseFilePath, deviceProfile);

        Debug.Vulkan(
            "[Vulkan] Pipeline prewarm database loaded (path={0}, entries={1}, capture={2}).",
            _pipelinePrewarmDatabaseFilePath,
            _pipelinePrewarmDatabase.EntryCount,
            _pipelinePrewarmCaptureEnabled);
    }

    private void SaveVulkanPipelinePrewarmDatabase()
    {
        if (!_pipelinePrewarmCaptureEnabled ||
            _pipelinePrewarmDatabase is null ||
            !_pipelinePrewarmDatabase.Dirty ||
            string.IsNullOrWhiteSpace(_pipelinePrewarmDatabaseFilePath))
        {
            return;
        }

        try
        {
            _pipelinePrewarmDatabase.Save(_pipelinePrewarmDatabaseFilePath);
            Debug.Vulkan(
                "[Vulkan] Pipeline prewarm database saved ({0} entries).",
                _pipelinePrewarmDatabase.EntryCount);
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning($"[Vulkan] Failed to save pipeline prewarm database '{_pipelinePrewarmDatabaseFilePath}': {ex.Message}");
        }
    }

    private void QueueVulkanPipelinePrewarmDatabaseAutoSave()
    {
        if (!_pipelinePrewarmCaptureEnabled ||
            _pipelinePrewarmDatabase is not { } database ||
            string.IsNullOrWhiteSpace(_pipelinePrewarmDatabaseFilePath) ||
            Volatile.Read(ref _pipelinePrewarmNewEntriesSinceSave) < VulkanPipelinePrewarmAutoSaveEntryThreshold ||
            Interlocked.CompareExchange(ref _pipelinePrewarmAutoSaveInFlight, 1, 0) != 0)
        {
            return;
        }

        string path = _pipelinePrewarmDatabaseFilePath;
        Interlocked.Exchange(ref _pipelinePrewarmNewEntriesSinceSave, 0);
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
                Interlocked.Exchange(ref _pipelinePrewarmAutoSaveInFlight, 0);
                if (Volatile.Read(ref _pipelinePrewarmNewEntriesSinceSave) >= VulkanPipelinePrewarmAutoSaveEntryThreshold)
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

        bool knownAtStartup = _pipelinePrewarmDatabase?.WasKnownAtStartup(entry.Key) == true;
        if (_pipelinePrewarmDatabase?.Record(entry) == true)
        {
            Interlocked.Increment(ref _pipelinePrewarmNewEntriesSinceSave);
            QueueVulkanPipelinePrewarmDatabaseAutoSave();
        }
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

        bool knownAtStartup = _pipelinePrewarmDatabase?.WasKnownAtStartup(entry.Key) == true;
        _pipelinePrewarmDatabase?.Record(entry);
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
