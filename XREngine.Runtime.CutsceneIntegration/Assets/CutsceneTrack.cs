using XREngine.Data.Core;

namespace XREngine.Cutscenes;

/// <summary>A lane of clips that act on one binding of a cutscene.</summary>
public abstract class CutsceneTrack : XRBase
{
    private string _name = string.Empty;
    private int _bindingSlot;
    private ECutsceneFinishBehavior _whenFinished = ECutsceneFinishBehavior.RestoreState;

    /// <summary>The display name of the track.</summary>
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    /// <summary>The index of the binding in the cutscene that this track acts on.</summary>
    public int BindingSlot
    {
        get => _bindingSlot;
        set => SetField(ref _bindingSlot, value);
    }

    /// <summary>What the track does to its target when the cutscene ends.</summary>
    public ECutsceneFinishBehavior WhenFinished
    {
        get => _whenFinished;
        set => SetField(ref _whenFinished, value);
    }

    /// <summary>The clips of the track.</summary>
    public List<CutsceneClip> Clips { get; set; } = [];
}
