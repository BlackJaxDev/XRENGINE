namespace XREngine.Cutscenes;

/// <summary>A clip that is active at a queried time.</summary>
/// <param name="TrackIndex">The index of the track in the cutscene asset.</param>
/// <param name="ClipIndex">The index of the clip in the track.</param>
/// <param name="LocalTime">
/// The time inside the clip data. The value is <c>(t - Start) * Speed + ClipOffset</c>
/// with the extrapolation of the clip applied.
/// </param>
/// <param name="Weight">The blend weight from 0 to 1.</param>
public readonly record struct CutsceneActiveClip(int TrackIndex, int ClipIndex, CutsceneTime LocalTime, float Weight);
