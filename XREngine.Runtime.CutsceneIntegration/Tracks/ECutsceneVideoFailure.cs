namespace XREngine.Cutscenes;

/// <summary>A failure of a video clip that stops the clip. The evaluator reports each failure of a clip once.</summary>
internal enum ECutsceneVideoFailure
{
    /// <summary>The media session did not open the file.</summary>
    OpenFailed = 0,

    /// <summary>The source has no seek, for example a live stream.</summary>
    CannotSeek = 1,

    /// <summary>A seek of the decoder failed.</summary>
    SeekFailed = 2,
}
