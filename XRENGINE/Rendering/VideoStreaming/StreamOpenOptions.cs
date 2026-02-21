using System.Collections.Generic;

namespace XREngine.Rendering.VideoStreaming;

public sealed class StreamOpenOptions
{
    public string? UserAgent { get; set; }
    public string? Referrer { get; set; }
    public IReadOnlyDictionary<string, string>? Headers { get; set; }
    public bool EnableReconnect { get; set; } = true;
    public int OpenTimeoutMs { get; set; } = 15000;
    public int VideoQueueCapacity { get; set; } = 1;
    public int AudioQueueCapacity { get; set; } = 8;
}
