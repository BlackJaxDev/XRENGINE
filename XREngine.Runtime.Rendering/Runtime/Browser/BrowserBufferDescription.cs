namespace XREngine.Rendering;

/// <summary>Bounded device-local buffer allocation. Uploads use queue writes rather than persistent mapping.</summary>
public sealed record BrowserBufferDescription(int Size, BrowserBufferUsage Usage, string Label = "");
