namespace XREngine.Cutscenes;

/// <summary>A problem that the compiler found in a cutscene asset.</summary>
/// <param name="TrackIndex">The index of the track with the problem.</param>
/// <param name="ClipIndex">The index of the clip with the problem.</param>
/// <param name="Message">A description of the problem.</param>
public readonly record struct CutsceneCompileDiagnostic(int TrackIndex, int ClipIndex, string Message);
