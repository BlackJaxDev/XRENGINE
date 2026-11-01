namespace XREngine.Cutscenes;

/// <summary>Describes how a clip behaves for times after its own duration.</summary>
public enum ECutsceneClipExtrapolation
{
    /// <summary>The clip has no effect after its duration.</summary>
    None,
    /// <summary>The clip keeps the value of its last moment.</summary>
    Hold,
    /// <summary>The clip repeats from its start.</summary>
    Loop,
}
