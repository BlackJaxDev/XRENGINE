namespace XREngine.Cutscenes;

/// <summary>How a video track gets the frame of the cutscene time.</summary>
internal enum ECutsceneVideoMode
{
    /// <summary>Forward playback: the decoder runs forward and the cutscene time releases each frame.</summary>
    Sequential = 0,

    /// <summary>A drag of the playhead: a keyframe seek shows the keyframe at or before the time.</summary>
    Keyframe = 1,

    /// <summary>A stopped playhead or a jump: an exact seek shows the frame of the time.</summary>
    Exact = 2,
}
