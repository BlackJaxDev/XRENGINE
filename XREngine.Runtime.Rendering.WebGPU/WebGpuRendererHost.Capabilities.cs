using System.Text.Json;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private const string MeshletUnsupportedReason =
        "The engine WebGPU meshlet route requires an installed compute-to-indexed-indirect lowering and cooked meshlet shader family. Hardware task/mesh shader stages are unavailable.";

    private BrowserDeviceCapabilities? _deviceCapabilities;
    public BrowserDeviceCapabilities? DeviceCapabilities
    {
        get => _deviceCapabilities;
        private set => SetField(ref _deviceCapabilities, value);
    }

    /// <summary>Count lowering is GPU-only; canonical draw identity requires enabled indirect first-instance.</summary>
    public override bool SupportsIndirectCountDraw()
    {
        if (State != BrowserRendererState.Ready || DeviceCapabilities is not { } capabilities)
            return false;
        for (int index = 0; index < capabilities.Features.Count; index++)
            if (capabilities.Features[index] == "indirect-first-instance") return true;
        return false;
    }

    public override EMeshShaderDialect MeshShaderDialect => EMeshShaderDialect.None;

    public override bool SupportsDirectMeshTaskDispatch() => false;

    public override bool SupportsIndirectCountMeshTaskDispatch() => false;

    public override bool SupportsProductionMeshletShaders() => false;

    public override bool SupportsMeshletDispatch() => false;

    public override string MeshletDispatchUnsupportedReason => MeshletUnsupportedReason;

    public override bool TryDrawMeshTasksIndirectCount(
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
