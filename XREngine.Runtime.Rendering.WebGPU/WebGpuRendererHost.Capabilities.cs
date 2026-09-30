using System.Text.Json;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private const string MeshletUnsupportedReason =
        "WebGPU has no task or mesh shader stages.";

    public BrowserDeviceCapabilities? DeviceCapabilities { get; private set; }

    // WebGPU exposes neither a GPU-written draw count nor task/mesh shader stages, so every
    // count-driven and meshlet submission path reports unavailable instead of being emulated.
    public bool SupportsIndirectCountDraw() => false;

    public EMeshShaderDialect MeshShaderDialect => EMeshShaderDialect.None;

    public bool SupportsDirectMeshTaskDispatch() => false;

    public bool SupportsIndirectCountMeshTaskDispatch() => false;

    public bool SupportsProductionMeshletShaders() => false;

    public bool SupportsMeshletDispatch() => false;

    public string MeshletDispatchUnsupportedReason => MeshletUnsupportedReason;

    public bool TryDrawMeshTasksIndirectCount(
        XRRenderProgram program,
        XRDataBuffer indirectBuffer,
        XRDataBuffer countBuffer,
        uint maxDrawCount,
        uint stride,
        out string failureReason,
        nuint byteOffset = 0,
        nuint countByteOffset = 0)
    {
        failureReason = MeshletUnsupportedReason;
        return false;
    }

    private static BrowserDeviceCapabilities ReadCapabilities(int session)
    {
        string json = WebGpuImports.GetCapabilities(session);
        if (json.Length > 32768)
            throw new InvalidOperationException("WebGPU capability snapshot exceeds its size limit.");
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.GetProperty("profile").GetString() is not "BrowserWebGPUCore")
            throw new InvalidOperationException("WebGPU returned an incompatible device profile.");
        JsonElement features = root.GetProperty("features");
        if (features.GetArrayLength() > 128)
            throw new InvalidOperationException("WebGPU returned too many feature names.");
        string[] names = new string[features.GetArrayLength()];
        for (int i = 0; i < names.Length; i++)
            names[i] = features[i].GetString() ?? throw new InvalidOperationException("A WebGPU feature name is missing.");
        Dictionary<string, long> limits = new(StringComparer.Ordinal);
        foreach (JsonProperty limit in root.GetProperty("limits").EnumerateObject())
        {
            if (limits.Count >= 128 || !limit.Value.TryGetInt64(out long value) || value < 0)
                throw new InvalidOperationException("WebGPU returned an invalid device limit.");
            limits.Add(limit.Name, value);
        }
        return new BrowserDeviceCapabilities("BrowserWebGPUCore", names, limits);
    }
}
