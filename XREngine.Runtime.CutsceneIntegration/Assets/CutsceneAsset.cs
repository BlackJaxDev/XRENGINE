using XREngine.Core.Files;

namespace XREngine.Cutscenes;

/// <summary>A timeline asset. It holds the tracks, the clips on them, and the bindings that the tracks act on.</summary>
public class CutsceneAsset : XRAsset
{
    private CutsceneTime _duration;
    private bool _forceRestoreAll;

    /// <summary>The length of the cutscene.</summary>
    public CutsceneTime Duration
    {
        get => _duration;
        set => SetField(ref _duration, value);
    }

    /// <summary>True to restore every target when the cutscene ends, whatever the track setting is.</summary>
    public bool ForceRestoreAll
    {
        get => _forceRestoreAll;
        set => SetField(ref _forceRestoreAll, value);
    }

    /// <summary>The tracks of the cutscene.</summary>
    public List<CutsceneTrack> Tracks { get; set; } = [];

    /// <summary>The targets that the tracks refer to by index.</summary>
    public List<CutsceneBindingReference> Bindings { get; set; } = [];
}
