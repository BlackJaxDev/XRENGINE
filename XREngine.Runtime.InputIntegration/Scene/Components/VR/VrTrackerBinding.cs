namespace XREngine.Components.VR;

/// <summary>Indices into the caller's slot and tracker spans.</summary>
public readonly record struct VrTrackerBinding(int SlotIndex, int TrackerIndex);
