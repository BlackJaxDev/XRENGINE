namespace XREngine.Editor.Cutscenes;

/// <summary>The part of a clip that a mouse drag in the sequencer panel changes.</summary>
public enum ECutsceneClipDrag
{
    /// <summary>No drag is active.</summary>
    None,
    /// <summary>The drag moves the whole clip.</summary>
    Move,
    /// <summary>The drag moves the start of the clip.</summary>
    TrimStart,
    /// <summary>The drag moves the end of the clip.</summary>
    TrimEnd,
}
