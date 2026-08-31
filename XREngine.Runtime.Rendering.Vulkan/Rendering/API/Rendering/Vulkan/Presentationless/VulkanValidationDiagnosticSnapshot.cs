namespace XREngine.Rendering.Vulkan;

/// <summary>Cold, device-owned validation evidence accumulated since instance creation.</summary>
public sealed record VulkanValidationDiagnosticSnapshot
{
    public bool StandardValidationEnabled { get; init; }
    public bool SynchronizationValidationEnabled { get; init; }
    public bool DebugMessengerActive { get; init; }
    public long ErrorCount { get; init; }
    public long WarningCount { get; init; }
    /// <summary>Warnings counted but omitted from logging by the existing unused-attachment filter.</summary>
    public long SuppressedWarningCount { get; init; }
    public int OverflowCount { get; init; }
    public VulkanValidationDiagnosticMessage[] Messages { get; init; } = [];
}
