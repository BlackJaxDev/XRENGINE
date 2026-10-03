namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Identifies one reserved swapchain retirement under a specific runtime instance generation.</summary>
public readonly record struct OpenXrRetirementToken(long InstanceGeneration, int Slot, long ReservationGeneration);
