namespace XREngine.Rendering.Commands;

/// <summary>One immutable owner-specific range captured at publication seal.</summary>
public readonly record struct AdvancedGpuDirtyOwnerRange(
    EAdvancedGpuRecordOwner Owner,
    AdvancedGpuDirtyRange Range,
    ulong ContentGeneration);
