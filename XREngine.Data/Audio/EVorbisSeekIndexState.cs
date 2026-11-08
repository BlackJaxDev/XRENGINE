namespace XREngine.Data;

/// <summary>The state of the Ogg page index of a <see cref="VorbisStreamingDecoder"/>.</summary>
public enum EVorbisSeekIndexState
{
    /// <summary>
    /// The decoder has no index, because its stream cannot be opened a second time. A backward seek decodes from the start of the file.
    /// </summary>
    Disabled,

    /// <summary>The index builds on a job thread. A backward seek decodes from the start of the file until it is ready.</summary>
    Building,

    /// <summary>The index is ready. A backward seek starts the decode at the nearest indexed page before the target.</summary>
    Ready,

    /// <summary>
    /// The index or a restart from it failed. The decoder does not use the index again, and a backward seek decodes from
    /// the start of the file. <see cref="VorbisStreamingDecoder.SeekIndexFailure"/> gives the reason.
    /// </summary>
    Failed,
}
