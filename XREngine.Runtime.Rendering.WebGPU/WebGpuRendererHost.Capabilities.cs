using System.Text.Json;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    public BrowserDeviceCapabilities? DeviceCapabilities { get; private set; }

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
