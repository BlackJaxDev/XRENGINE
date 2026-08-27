using System.Text.Json;
using Silk.NET.Vulkan;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

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
            using (VulkanFrameLockScope.Enter(
                       _sync,
                       EVulkanFrameWaitReason.PipelineCompilerLock))
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

        using (VulkanFrameLockScope.Enter(
                   _sync,
                   EVulkanFrameWaitReason.PipelineCompilerLock))
            return _entriesByKey.ContainsKey(key);
    }

    public bool WasKnownAtStartup(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        using (VulkanFrameLockScope.Enter(
                   _sync,
                   EVulkanFrameWaitReason.PipelineCompilerLock))
            return _keysLoadedAtStartup.Contains(key);
    }

    public bool Record(VulkanPipelinePrewarmEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Key))
            return false;

        using (VulkanFrameLockScope.Enter(
                   _sync,
                   EVulkanFrameWaitReason.PipelineCompilerLock))
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
        using (VulkanFrameLockScope.Enter(
                   _sync,
                   EVulkanFrameWaitReason.PipelineCompilerLock))
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
