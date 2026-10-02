using System.Text.Json.Serialization;
using XREngine.Rendering.WebGPU;

namespace XREngine.Browser;

/// <summary>Statically roots cold engine-frame diagnostic serialization for browser builds.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Serialization)]
[JsonSerializable(typeof(WebGpuEngineFrameStatistics))]
internal sealed partial class BrowserEngineStatisticsJsonContext : JsonSerializerContext { }
