namespace XREngine.Rendering.Vulkan;

/// <summary>Cold diagnostic copy of a destruction class's current-frame accounting.</summary>
public readonly record struct VulkanRetirementClassDiagnostic(
    string Class,
    int OrdinaryCap,
    int HighWaterMark,
    int Admitted,
    int Completed,
    int Deferred,
    int Backlog,
    double OldestPendingAgeMilliseconds,
    bool UncappedSafetyDrain,
    int UncappedSafetyDrainActivations);
