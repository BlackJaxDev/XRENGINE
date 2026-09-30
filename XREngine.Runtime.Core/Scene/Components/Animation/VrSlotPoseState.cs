namespace XREngine.Components.Animation;

/// <summary>Published slot ownership and blend weight. A retained display pose is never a calibration sample.</summary>
public readonly record struct VrSlotPoseState(EVrSlotPoseSource Source, float Weight, bool CurrentlyTracked, bool RequiresRecalibration);
