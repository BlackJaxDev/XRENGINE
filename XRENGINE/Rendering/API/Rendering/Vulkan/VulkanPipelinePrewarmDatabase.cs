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
    internal const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly Dictionary<string, VulkanPipelinePrewarmEntry> _entriesByKey;

    private VulkanPipelinePrewarmDatabase(string deviceProfile, IEnumerable<VulkanPipelinePrewarmEntry> entries)
    {
        DeviceProfile = deviceProfile;
        _entriesByKey = new Dictionary<string, VulkanPipelinePrewarmEntry>(StringComparer.Ordinal);

        foreach (VulkanPipelinePrewarmEntry entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.Key))
                _entriesByKey[entry.Key] = entry;
        }
    }

    public string DeviceProfile { get; }
    public int EntryCount => _entriesByKey.Count;
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
        => !string.IsNullOrWhiteSpace(key) && _entriesByKey.ContainsKey(key);

    public bool Record(VulkanPipelinePrewarmEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Key))
            return false;

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

    public void Save(string path)
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
        RenderPass renderPass,
        Format colorAttachmentFormat,
        Format depthAttachmentFormat,
        ulong programPipelineHash,
        ulong vertexLayoutHash,
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
            renderPass.Handle.ToString("X"),
            colorAttachmentFormat.ToString(),
            depthAttachmentFormat.ToString(),
            programPipelineHash.ToString("X16"),
            vertexLayoutHash.ToString("X16"),
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
            RenderPassHandle = renderPass.Handle,
            ColorAttachmentFormat = colorAttachmentFormat.ToString(),
            DepthAttachmentFormat = depthAttachmentFormat.ToString(),
            ProgramPipelineHash = programPipelineHash,
            VertexLayoutHash = vertexLayoutHash,
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
    public ulong RenderPassHandle { get; set; }
    public string ColorAttachmentFormat { get; set; } = string.Empty;
    public string DepthAttachmentFormat { get; set; } = string.Empty;
    public ulong ProgramPipelineHash { get; set; }
    public ulong VertexLayoutHash { get; set; }
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
    private const string VulkanPipelinePrewarmCaptureEnvVar = "XRE_VK_PIPELINE_PREWARM_CAPTURE";

    private VulkanPipelinePrewarmDatabase? _pipelinePrewarmDatabase;
    private string? _pipelinePrewarmDatabaseFilePath;
    private bool _pipelinePrewarmCaptureEnabled;

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
            "1",
            StringComparison.OrdinalIgnoreCase);

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

    internal void RecordVulkanGraphicsPipelineCacheMiss(
        int passIndex,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        string pipelineName,
        string? meshName,
        XRMaterial material,
        string? programName,
        PrimitiveTopology topology,
        bool useDynamicRendering,
        RenderPass renderPass,
        Format colorAttachmentFormat,
        Format depthAttachmentFormat,
        ulong programPipelineHash,
        ulong vertexLayoutHash,
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
            renderPass,
            colorAttachmentFormat,
            depthAttachmentFormat,
            programPipelineHash,
            vertexLayoutHash,
            rasterizationSamples,
            depthTestEnabled,
            blendEnabled,
            alphaToCoverageEnabled,
            colorWriteMask,
            profileName);

        bool knownAtStartup = _pipelinePrewarmDatabase?.Contains(entry.Key) == true;
        _pipelinePrewarmDatabase?.Record(entry);
        Engine.Rendering.Stats.RecordVulkanPipelineCacheMiss(entry.ToProfilerSummary(knownAtStartup));
    }

    internal void RecordVulkanComputePipelineCacheMiss(
        int passIndex,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        VkRenderProgram program,
        ulong programPipelineHash)
    {
        string passName = ResolveRenderPassName(passIndex, passMetadata);
        string pipelineName = Engine.Rendering.State.CurrentRenderingPipeline?.DebugName ?? "<no pipeline>";
        string programName = program.Data.Name ?? "UnnamedProgram";
        string profileName = VulkanFeatureProfile.ActiveProfile.ToString();

        VulkanPipelinePrewarmEntry entry = VulkanPipelinePrewarmDatabase.CreateComputeEntry(
            passIndex,
            passName,
            pipelineName,
            programName,
            programPipelineHash,
            profileName);

        bool knownAtStartup = _pipelinePrewarmDatabase?.Contains(entry.Key) == true;
        _pipelinePrewarmDatabase?.Record(entry);
        Engine.Rendering.Stats.RecordVulkanPipelineCacheMiss(entry.ToProfilerSummary(knownAtStartup));
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
