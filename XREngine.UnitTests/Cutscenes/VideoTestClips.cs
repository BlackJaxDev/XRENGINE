using FFmpeg.AutoGen;
using XREngine.Rendering.VideoStreaming;
using XREngine.Runtime.Media.FFmpeg;

namespace XREngine.UnitTests.Cutscenes;

/// <summary>
/// Writes small video clips for the seek tests with the FFmpeg libraries of the project. The FFmpeg build has decoders
/// and muxers but no encoders, so each clip stores raw RGB24 frames. Only every <see cref="KeyframeInterval"/>-th packet has
/// the key flag, so the index of the container has the keyframe layout of a normal compressed clip, and a demuxer seek
/// lands on those keyframes. Each frame is one gray level that gives its frame index, so a test can read the index back.
/// All timestamps are exact: 25 frames per second is 40 ms per frame in every container time base.
/// </summary>
internal static unsafe class VideoTestClips
{
    public const int Width = 16;
    public const int Height = 16;
    public const int FramesPerSecond = 25;
    public const int FrameCount = 50;
    public const int KeyframeInterval = 10;

    /// <summary>The time of one frame in 100 ns ticks.</summary>
    public const long FrameTicks = TimeSpan.TicksPerSecond / FramesPerSecond;

    /// <summary>
    /// The containers that the tests write: the file extension and the FFmpeg muxer name. The MP4 muxer rejects raw and
    /// qtrle video (EINVAL at the header, also in the experimental compliance mode), so MP4 has no clip; MP4 files use the
    /// QuickTime demuxer, which the <c>.mov</c> clips test.
    /// </summary>
    public static readonly (string Extension, string Muxer)[] Containers =
    [
        (".mkv", "matroska"),
        (".mov", "mov"),
        (".avi", "avi"),
        (".nut", "nut"),
    ];

    private static readonly object InitLock = new();
    private static bool? _ffmpegReady;

    /// <summary>The gray level of a frame.</summary>
    public static byte GrayOf(int frame)
        => (byte)(8 + frame * 4);

    /// <summary>The frame index of a decoded frame, from the gray level of its first pixel.</summary>
    public static int FrameOf(in DecodedVideoFrame frame)
        => (frame.PackedData.Span[0] - 8) / 4;

    /// <summary>The time of a frame in ticks.</summary>
    public static long TicksOf(int frame)
        => frame * FrameTicks;

    /// <summary>
    /// Installs the FFmpeg media backend and loads the FFmpeg libraries. Returns false if the libraries are not on this
    /// machine. The backend registration stays for the process, because the bindings cannot unload.
    /// </summary>
    public static bool EnsureFfmpeg()
    {
        lock (InitLock)
        {
            if (_ffmpegReady is { } ready)
                return ready;

            if (HlsReferenceRuntime.Backend is null)
            {
                // The registration also replaces the YouTube resolver. The tests need only the media backend.
                var previousResolver = YouTubeStreamUrlResolverRegistry.Current;
                FfmpegMediaBackend.Register();
                YouTubeStreamUrlResolverRegistry.Current = previousResolver;
            }

            _ffmpegReady = HlsReferenceRuntime.EnsureStarted();
            return _ffmpegReady.Value;
        }
    }

    /// <summary>Writes a clip with <see cref="FrameCount"/> frames in the given container and returns its path.</summary>
    /// <param name="directory">The folder of the file.</param>
    /// <param name="extension">The file extension.</param>
    /// <param name="muxer">The FFmpeg muxer name.</param>
    /// <param name="interCoded">
    /// True to store QuickTime Animation (qtrle) frames, a codec with inter frames: the demuxer then keeps the key flags of
    /// the packets, so a keyframe seek lands on every <see cref="KeyframeInterval"/>-th frame. False stores raw frames. Raw
    /// video is an intra-only codec, so FFmpeg marks every raw frame as a keyframe.
    /// </param>
    /// <param name="startFrame">The time of the first frame, in frames. A value above zero gives the file a non-zero start time.</param>
    public static unsafe string Write(string directory, string extension, string muxer, bool interCoded = false, int startFrame = 0)
    {
        string path = Path.Combine(directory, $"clip{extension}");
        AVFormatContext* format = null;
        Check(ffmpeg.avformat_alloc_output_context2(&format, null, muxer, path), "avformat_alloc_output_context2");
        AVPacket* packet = null;
        try
        {
            AVStream* stream = ffmpeg.avformat_new_stream(format, null);
            if (stream == null)
                throw new InvalidOperationException("avformat_new_stream failed.");

            AVRational frameBase = new() { num = 1, den = FramesPerSecond };
            stream->time_base = frameBase;
            stream->avg_frame_rate = new AVRational { num = FramesPerSecond, den = 1 };
            stream->r_frame_rate = stream->avg_frame_rate;
            AVCodecParameters* parameters = stream->codecpar;
            parameters->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            parameters->codec_id = interCoded ? AVCodecID.AV_CODEC_ID_QTRLE : AVCodecID.AV_CODEC_ID_RAWVIDEO;
            parameters->width = Width;
            parameters->height = Height;
            // AVI and the VFW mode of Matroska store bottom-up BGR24 (BI_RGB). QuickTime and NUT store RGB24. A gray frame
            // has the same bytes in both orders, so the decoder output is the same.
            bool bitmap = !interCoded && muxer is "avi" or "matroska";
            AVPixelFormat pixelFormat = bitmap ? AVPixelFormat.AV_PIX_FMT_BGR24 : AVPixelFormat.AV_PIX_FMT_RGB24;
            parameters->format = (int)pixelFormat;
            parameters->bits_per_coded_sample = 24;
            if (!interCoded && muxer is "nut")
                parameters->codec_tag = ffmpeg.avcodec_pix_fmt_to_codec_tag(pixelFormat);

            if ((format->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                Check(ffmpeg.avio_open(&format->pb, path, ffmpeg.AVIO_FLAG_WRITE), "avio_open");

            // Matroska stores raw RGB only in its VFW (BITMAPINFOHEADER) mode, which this option allows.
            AVDictionary* options = null;
            if (!interCoded && muxer == "matroska")
                ffmpeg.av_dict_set(&options, "allow_raw_vfw", "1", 0);
            int header = ffmpeg.avformat_write_header(format, &options);
            ffmpeg.av_dict_free(&options);
            Check(header, "avformat_write_header");

            int size = interCoded ? QtrlePacketSize : Width * Height * 3;
            packet = ffmpeg.av_packet_alloc();
            for (int i = 0; i < FrameCount; i++)
            {
                Check(ffmpeg.av_new_packet(packet, size), "av_new_packet");
                Span<byte> data = new(packet->data, size);
                if (interCoded)
                    WriteQtrleFrame(data, GrayOf(i));
                else
                    data.Fill(GrayOf(i));
                packet->pts = ffmpeg.av_rescale_q(i + startFrame, frameBase, stream->time_base);
                packet->dts = packet->pts;
                packet->duration = ffmpeg.av_rescale_q(1, frameBase, stream->time_base);
                packet->stream_index = stream->index;
                packet->flags = i % KeyframeInterval == 0 ? ffmpeg.AV_PKT_FLAG_KEY : 0;
                Check(ffmpeg.av_interleaved_write_frame(format, packet), "av_interleaved_write_frame");
            }
            Check(ffmpeg.av_write_trailer(format), "av_write_trailer");
        }
        finally
        {
            if (packet != null)
                ffmpeg.av_packet_free(&packet);
            if ((format->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && format->pb != null)
                ffmpeg.avio_closep(&format->pb);
            ffmpeg.avformat_free_context(format);
        }
        return path;
    }

    // A qtrle packet: the chunk size, a header with no line range, and for each line one skip byte, one run of Width
    // pixels, and the end code.
    private const int QtrleLineSize = 1 + 1 + 3 + 1;
    private const int QtrlePacketSize = 4 + 2 + Height * QtrleLineSize;

    /// <summary>
    /// Writes one 24-bit qtrle frame that sets every pixel to one gray level. Each frame updates every line, so a frame
    /// also decodes after a seek to an earlier keyframe.
    /// </summary>
    private static void WriteQtrleFrame(Span<byte> data, byte gray)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(data, data.Length);
        data[4] = 0;
        data[5] = 0;
        int offset = 6;
        for (int line = 0; line < Height; line++)
        {
            data[offset++] = 1;                    // Skip code: start at pixel 0.
            data[offset++] = unchecked((byte)-Width); // A run of Width pixels.
            data[offset++] = gray;
            data[offset++] = gray;
            data[offset++] = gray;
            data[offset++] = 0xFF;                 // End of the line.
        }
    }

    /// <summary>
    /// Writes a live HLS playlist (no end tag) with one segment, a Matroska clip in the same folder, and returns its path.
    /// </summary>
    /// <param name="directory">The folder of the files.</param>
    public static string WriteLivePlaylist(string directory)
    {
        string segment = Write(directory, ".mkv", "matroska");
        string path = Path.Combine(directory, "live.m3u8");
        File.WriteAllText(path,
            "#EXTM3U\n" +
            "#EXT-X-VERSION:3\n" +
            "#EXT-X-TARGETDURATION:2\n" +
            "#EXT-X-MEDIA-SEQUENCE:0\n" +
            "#EXTINF:2.0,\n" +
            Path.GetFileName(segment) + "\n");
        return path;
    }

    private static void Check(int result, string call)
    {
        if (result < 0)
            throw new InvalidOperationException($"{call} failed with FFmpeg error {result}.");
    }
}
