namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Independent ownership proofs still needed before retired XR images can be destroyed.</summary>
[Flags]
public enum EOpenXrSwapchainRetirementBlockers : uint
{
    None = 0,
    RuntimeImageAcquired = 1 << 0,
    GenerationBudget = 1 << 1,
    SubmissionCompletion = 1 << 2,
    ResourceLifetime = 1 << 3,
    ExternalImageLifetime = 1 << 4,
    DetachedResourceSlots = 1 << 5,
    ChildResources = 1 << 6,
    RuntimeDestroyFailure = 1 << 7,
    DeviceLost = 1 << 8,
}
