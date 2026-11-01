using XREngine.Data.Core;

namespace XREngine.Cutscenes;

/// <summary>
/// A span of time on a track. The base type is abstract so that YAML keeps the clip type of each list entry.
/// Derived types add the data that they evaluate.
/// </summary>
public abstract class CutsceneClip : XRBase
{
    private CutsceneTime _start;
    private CutsceneTime _duration;
    private CutsceneTime _clipOffset;
    private double _speed = 1.0;
    private CutsceneTime _blendIn;
    private CutsceneTime _blendOut;
    private ECutsceneClipExtrapolation _extrapolation;

    /// <summary>The cutscene time at which the clip starts.</summary>
    public CutsceneTime Start
    {
        get => _start;
        set => SetField(ref _start, value);
    }

    /// <summary>The length of the clip on the cutscene timeline.</summary>
    public CutsceneTime Duration
    {
        get => _duration;
        set => SetField(ref _duration, value);
    }

    /// <summary>The time inside the clip data at which the clip starts to play.</summary>
    public CutsceneTime ClipOffset
    {
        get => _clipOffset;
        set => SetField(ref _clipOffset, value);
    }

    /// <summary>The play speed of the clip data. One is normal speed.</summary>
    public double Speed
    {
        get => _speed;
        set => SetField(ref _speed, value);
    }

    /// <summary>The length of the fade in at the start of the clip.</summary>
    public CutsceneTime BlendIn
    {
        get => _blendIn;
        set => SetField(ref _blendIn, value);
    }

    /// <summary>The length of the fade out at the end of the clip.</summary>
    public CutsceneTime BlendOut
    {
        get => _blendOut;
        set => SetField(ref _blendOut, value);
    }

    /// <summary>How the clip behaves after its duration.</summary>
    public ECutsceneClipExtrapolation Extrapolation
    {
        get => _extrapolation;
        set => SetField(ref _extrapolation, value);
    }
}
