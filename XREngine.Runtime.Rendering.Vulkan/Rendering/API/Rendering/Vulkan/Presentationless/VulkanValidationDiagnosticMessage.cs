namespace XREngine.Rendering.Vulkan;

/// <summary>A bounded aggregate of one native validation message identity.</summary>
public sealed record VulkanValidationDiagnosticMessage
{
    public string Identity { get; init; } = string.Empty;
    public int Count { get; init; }
    public int ErrorCount { get; init; }
    public int WarningCount { get; init; }
    public ulong FirstFrameId { get; init; }
    public ulong LastFrameId { get; init; }
    public string FirstSample { get; init; } = string.Empty;
    public string LastSample { get; init; } = string.Empty;
}
