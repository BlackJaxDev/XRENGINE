namespace XREngine.Data;

/// <summary>
/// One restart point of a <see cref="VorbisPageIndex"/>: an Ogg page on which an audio packet starts.
/// A decode that starts at the first packet that starts on this page gives its first frame at <see cref="RestartFrame"/>.
/// </summary>
/// <param name="ByteOffset">The byte offset of the page in the file.</param>
/// <param name="Sequence">The page sequence number. The restart checks it against the page in the file.</param>
/// <param name="Granule">The granule position of the page, or -1. The restart checks it against the page in the file.</param>
/// <param name="SkipSegments">
/// The number of lacing values at the start of the page that end a packet from an earlier page. The restart removes them.
/// </param>
/// <param name="RestartFrame">
/// The frame of the first sample that a decode from this page gives. The first packet of a decode gives no samples,
/// because it has no earlier packet to overlap with, so this is the end of that first packet.
/// </param>
internal readonly record struct VorbisPageIndexEntry(long ByteOffset, int Sequence, long Granule, int SkipSegments, long RestartFrame);
