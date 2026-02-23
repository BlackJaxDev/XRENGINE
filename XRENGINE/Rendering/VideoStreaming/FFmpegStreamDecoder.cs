using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine.Rendering.VideoStreaming;

/// <summary>
/// Opens an HLS (or other FFmpeg-supported) URL and decodes video/audio frames
/// on a background thread, delivering them via callbacks.
/// <para>
/// Architecture: A single background thread runs <see cref="DecodeLoop"/>, which
/// reads packets from FFmpeg and decodes them into raw video (RGB24) and audio
/// (interleaved PCM) frames. Decoded frames are delivered to <see cref="OnVideoFrame"/>
/// and <see cref="OnAudioFrame"/> callbacks, which are typically wired to
/// <see cref="HlsMediaStreamSession"/>'s backpressure-enqueue methods.
/// </para>
/// <para>
/// Decoder-side frame pacing is intentionally disabled. Playback timing is
/// controlled by backpressure from the session queue: the decode thread blocks
/// in the callback when the queue is full.
/// </para>
/// </summary>
internal sealed class FFmpegStreamDecoder : IDisposable
{
    // ═══════════════════════════════════════════════════════════════
    // Constants
    // ═══════════════════════════════════════════════════════════════

    /// <summary>All decoded video frames are converted to this pixel format before delivery.</summary>
    private const AVPixelFormat TargetPixelFormat = AVPixelFormat.AV_PIX_FMT_RGB24;

    /// <summary>Maximum consecutive packet-read failures before the decode loop aborts.</summary>
    private const int MaxReadRetries = 12;

    // ═══════════════════════════════════════════════════════════════
    // Fields — Thread Safety & Lifecycle
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Lock guarding init/cleanup of FFmpeg contexts (decode thread uses them lock-free once started).</summary>
    private readonly Lock _lock = new();

    /// <summary>Set once <see cref="Dispose"/> is called to prevent double-cleanup.</summary>
    private volatile bool _disposed;

    /// <summary>Controls the decode loop — cleared to request a graceful stop.</summary>
    private volatile bool _running;

    /// <summary>The background decode thread (started by <see cref="OpenAsync"/>).</summary>
    private Thread? _decodeThread;

    // ═══════════════════════════════════════════════════════════════
    // Fields — FFmpeg Native Contexts
    // All access guarded by _lock during init/cleanup, then only
    // touched on the decode thread.
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Top-level FFmpeg demuxer context (owns the opened stream).</summary>
    private unsafe AVFormatContext* _formatContext;

    /// <summary>Video decoder context (H.264, etc.).</summary>
    private unsafe AVCodecContext* _videoCodecContext;

    /// <summary>Audio decoder context (AAC, etc.).</summary>
    private unsafe AVCodecContext* _audioCodecContext;

    /// <summary>Hardware device context (unused — HW accel is currently disabled).</summary>
    private unsafe AVBufferRef* _hwDeviceContext;

    /// <summary>Reusable frame buffer for the current decoded frame.</summary>
    private unsafe AVFrame* _frame;

    /// <summary>Reusable packet buffer for the current demuxed packet.</summary>
    private unsafe AVPacket* _packet;

    /// <summary>Software scaler context for pixel-format conversion to <see cref="TargetPixelFormat"/>.</summary>
    private unsafe SwsContext* _swsContext;

    /// <summary>Pixel format reported by hardware acceleration (used for HW→SW transfer detection).</summary>
    private AVPixelFormat _hwPixelFormat;

    // ── Cached SwsContext parameters (to detect when re-creation is needed) ──
    private int _swsWidth;
    private int _swsHeight;
    private AVPixelFormat _swsSrcFormat;

    // ═══════════════════════════════════════════════════════════════
    // Fields — Stream Indices & State
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Index of the selected video stream within the format context, or -1 if none.</summary>
    private int _videoStreamIndex = -1;

    /// <summary>Index of the selected audio stream within the format context, or -1 if none.</summary>
    private int _audioStreamIndex = -1;

    /// <summary>Running count of consecutive read failures (reset on success).</summary>
    private int _readRetryCount;

    /// <summary>Last reported video dimensions (used to detect size changes).</summary>
    private int _lastReportedWidth;
    private int _lastReportedHeight;

    /// <summary>
    /// Estimated video frame duration in .NET ticks (100 ns). Seeded at 60 fps
    /// and refined from stream metadata when available.
    /// </summary>
    private long _videoFrameDurationTicks = TimeSpan.TicksPerSecond / 60;

    /// <summary>Last emitted video PTS in .NET ticks (monotonic timeline).</summary>
    private long _lastVideoPtsTicks = long.MinValue;

    /// <summary>
    /// Current synthesized video PTS used when FFmpeg provides missing or
    /// unusable timestamps.
    /// </summary>
    private long _syntheticVideoPtsTicks = long.MinValue;

    /// <summary>Consecutive count of video frames with missing/unusable source PTS.</summary>
    private int _videoMissingPtsStreak;

    // ═══════════════════════════════════════════════════════════════
    // Callbacks — Wired by HlsMediaStreamSession
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Invoked on the decode thread for each decoded video frame.</summary>
    public Action<DecodedVideoFrame>? OnVideoFrame { get; set; }

    /// <summary>Invoked on the decode thread for each decoded audio frame.</summary>
    public Action<DecodedAudioFrame>? OnAudioFrame { get; set; }

    /// <summary>Invoked when the decoded video resolution changes (e.g. HLS quality switch).</summary>
    public Action<int, int>? OnVideoSizeChanged { get; set; }

    /// <summary>Invoked once when the video frame rate is detected from stream metadata.</summary>
    public Action<double>? OnVideoFrameRateDetected { get; set; }

    /// <summary>Invoked for non-fatal errors and warnings.</summary>
    public Action<string>? OnError { get; set; }

    // ═══════════════════════════════════════════════════════════════
    // Public API — Open / Stop / Dispose
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Opens the stream at <paramref name="url"/> with optional HTTP/HLS settings,
    /// then starts a background decode thread that reads and decodes packets.
    /// </summary>
    /// <param name="url">Stream URL (HLS playlist, RTMP, file, etc.).</param>
    /// <param name="options">Optional HTTP headers, reconnect policy, queue sizes.</param>
    /// <param name="cancellationToken">Token to cancel the open operation.</param>
    /// <exception cref="InvalidOperationException">Thrown if FFmpeg fails to open the URL.</exception>
    public unsafe Task OpenAsync(string url, StreamOpenOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            bool opened = false;
            try
            {
                opened = AllocateAndOpen(url, options);
            }
            catch (Exception ex)
            {
                // avformat_open_input (or other native calls) can throw a
                // native access-violation / SEH exception rather than
                // returning a negative error code. Ensure we always clean
                // up native resources and surface a clear error.
                Cleanup();
                throw new InvalidOperationException(
                    $"FFmpeg native exception while opening '{url}': {ex.Message}", ex);
            }

            if (!opened)
            {
                Cleanup();
                Debug.LogWarning($"FFmpeg failed to open any streams for URL: {url}");
                return;
            }

            _running = true;
            Debug.Out($"FFmpeg stream decoder opened: url='{url}', video={_videoStreamIndex}, audio={_audioStreamIndex}");

            _decodeThread = new Thread(DecodeLoop)
            {
                Name = "FFmpegStreamDecoder",
                IsBackground = true
            };
            _decodeThread.Start();
        }, cancellationToken);
    }

    /// <summary>
    /// Signals the decode loop to stop, waits up to 3 seconds for the thread
    /// to exit, and then frees all native FFmpeg resources.
    /// </summary>
    public void Stop()
    {
        _running = false;
        _decodeThread?.Join(TimeSpan.FromSeconds(3));
        Cleanup();
    }

    /// <summary>
    /// Stops and disposes all resources. Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    // ═══════════════════════════════════════════════════════════════
    // Decode Loop
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Main entry point for the background decode thread.
    /// Continuously reads and decodes packets until stopped or an
    /// unrecoverable error occurs.
    /// </summary>
    private void DecodeLoop()
    {
        try
        {
            while (_running && !_disposed)
                ReadAndDecodeOnePacket();
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, "FFmpeg decode loop");
            if (_running && !_disposed)
                OnError?.Invoke($"Decode loop exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads a single packet from the format context, routes it to the
    /// appropriate decoder (video or audio), and handles read errors with
    /// exponential-backoff retries.
    /// </summary>
    private unsafe void ReadAndDecodeOnePacket()
    {
        int result = ffmpeg.av_read_frame(_formatContext, _packet);
        if (result < 0)
        {
            // End of file — stop cleanly.
            if (result == ffmpeg.AVERROR_EOF)
            {
                _running = false;
                return;
            }

            // Transient read failure — retry with exponential backoff.
            _readRetryCount++;
            if (_readRetryCount > MaxReadRetries)
            {
                OnError?.Invoke($"FFmpeg read failed after {MaxReadRetries} retries.");
                _running = false;
                return;
            }

            int delayMs = Math.Min(2000, 100 * (1 << Math.Min(_readRetryCount - 1, 4)));
            Thread.Sleep(delayMs);
            return;
        }

        _readRetryCount = 0; // Reset on successful read.

        try
        {
            // Route the packet to the correct decoder based on stream index.
            if (_packet->stream_index == _videoStreamIndex)
                DecodeVideoPacket();
            else if (_packet->stream_index == _audioStreamIndex)
                DecodeAudioPacket();
        }
        finally
        {
            ffmpeg.av_packet_unref(_packet);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Video Decoding
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Sends the current video packet to the decoder and processes all
    /// output frames (a single packet may produce zero or more frames).
    /// </summary>
    private unsafe void DecodeVideoPacket()
    {
        if (_videoCodecContext == null)
            return;

        if (ffmpeg.avcodec_send_packet(_videoCodecContext, _packet) < 0)
            return;

        while (ffmpeg.avcodec_receive_frame(_videoCodecContext, _frame) >= 0)
            ProcessVideoFrame();
    }

    /// <summary>
    /// Converts a decoded video frame to RGB24, copies the pixel data to a
    /// managed byte array, computes the presentation timestamp, and delivers
    /// the result via <see cref="OnVideoFrame"/>.
    /// </summary>
    private unsafe void ProcessVideoFrame()
    {
        // If the frame came from HW accel, transfer it to a software frame.
        AVFrame* decodedFrame = GetHwFrame(_frame);
        try
        {
            int width = decodedFrame->width;
            int height = decodedFrame->height;

            if (width <= 0 || height <= 0)
                return;

            // ── Notify resolution change (e.g. HLS quality switch) ──
            if (width != _lastReportedWidth || height != _lastReportedHeight)
            {
                _lastReportedWidth = width;
                _lastReportedHeight = height;
                OnVideoSizeChanged?.Invoke(width, height);
            }

            // ── Convert to RGB24 if the decoded format differs ──
            AVFrame* rgbFrame = decodedFrame;
            bool freeRgb = false;

            if (decodedFrame->format != (int)TargetPixelFormat)
            {
                var srcFmt = (AVPixelFormat)decodedFrame->format;

                // Re-create the sws scaler when dimensions or format change.
                if (_swsContext == null || width != _swsWidth || height != _swsHeight || srcFmt != _swsSrcFormat)
                {
                    if (_swsContext != null)
                        ffmpeg.sws_freeContext(_swsContext);

                    // SWS_BILINEAR = 2 (not exposed by FFmpeg.AutoGen as a named constant).
                    _swsContext = ffmpeg.sws_getContext(
                        width, height, srcFmt,
                        width, height, TargetPixelFormat,
                        2, null, null, null);
                    _swsWidth = width;
                    _swsHeight = height;
                    _swsSrcFormat = srcFmt;

                    if (_swsContext == null)
                    {
                        OnError?.Invoke($"sws_getContext failed for {srcFmt} -> {TargetPixelFormat} at {width}x{height}");
                        return;
                    }
                }

                // Allocate a temporary RGB frame for the scaled output.
                rgbFrame = ffmpeg.av_frame_alloc();
                rgbFrame->format = (int)TargetPixelFormat;
                rgbFrame->width = width;
                rgbFrame->height = height;
                ffmpeg.av_frame_get_buffer(rgbFrame, 0);
                freeRgb = true;

                ffmpeg.sws_scale(
                    _swsContext,
                    decodedFrame->data, decodedFrame->linesize,
                    0, height,
                    rgbFrame->data, rgbFrame->linesize);
            }

            // ── Copy pixel data to a managed byte array (row by row for stride safety) ──
            int dataSize = width * height * 3; // RGB24 = 3 bytes per pixel
            byte[] data = new byte[dataSize];
            fixed (byte* dest = data)
            {
                byte* src = (byte*)rgbFrame->data[0];
                int srcStride = rgbFrame->linesize[0];
                int dstStride = width * 3;
                for (int row = 0; row < height; row++)
                    Buffer.MemoryCopy(src + row * srcStride, dest + row * dstStride, dstStride, dstStride);
            }

            // ── Compute presentation timestamp (PTS) in 100ns ticks ──
            long pts = ResolveVideoPtsTicks(decodedFrame);

            // ── Deliver the decoded frame ──
            var frame = new DecodedVideoFrame(width, height, pts, VideoPixelFormat.Rgb24, data);
            OnVideoFrame?.Invoke(frame);

            if (freeRgb)
                ffmpeg.av_frame_free(&rgbFrame);
        }
        finally
        {
            // Clean up: free the HW-transferred frame, or unref the reusable frame.
            if (decodedFrame != _frame)
            {
                ffmpeg.av_frame_free(&decodedFrame);
            }
            else
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }
    }

    /// <summary>
    /// If the decoded frame is in a hardware pixel format, transfers it to
    /// a software frame. Otherwise returns the original frame unchanged.
    /// </summary>
    private unsafe AVFrame* GetHwFrame(AVFrame* frame)
    {
        if (frame->format != (int)_hwPixelFormat)
            return frame;

        var swFrame = ffmpeg.av_frame_alloc();
        if (ffmpeg.av_hwframe_transfer_data(swFrame, frame, 0) < 0)
        {
            ffmpeg.av_frame_free(&swFrame);
            return frame; // Fallback: return original on transfer failure.
        }

        ffmpeg.av_frame_unref(frame);
        return swFrame;
    }

    // ═══════════════════════════════════════════════════════════════
    // Audio Decoding
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Sends the current audio packet to the decoder and processes all
    /// output frames.
    /// </summary>
    private unsafe void DecodeAudioPacket()
    {
        if (_audioCodecContext == null)
            return;

        if (ffmpeg.avcodec_send_packet(_audioCodecContext, _packet) < 0)
            return;

        while (ffmpeg.avcodec_receive_frame(_audioCodecContext, _frame) >= 0)
            ProcessAudioFrame();
    }

    /// <summary>
    /// Converts a decoded audio frame from its native sample format
    /// (possibly planar) to interleaved PCM, computes the PTS, and
    /// delivers it via <see cref="OnAudioFrame"/>.
    /// </summary>
    private unsafe void ProcessAudioFrame()
    {
        int channels = _audioCodecContext->ch_layout.nb_channels;
        int sampleRate = _audioCodecContext->sample_rate;
        int nbSamples = _frame->nb_samples;

        if (channels <= 0 || sampleRate <= 0 || nbSamples <= 0)
            return;

        // ── Map FFmpeg sample format to our AudioSampleFormat enum ──
        AVSampleFormat fmt = (AVSampleFormat)_frame->format;
        AudioSampleFormat sampleFormat;
        int bytesPerSample;

        switch (fmt)
        {
            case AVSampleFormat.AV_SAMPLE_FMT_S16:
            case AVSampleFormat.AV_SAMPLE_FMT_S16P:
                sampleFormat = AudioSampleFormat.S16;
                bytesPerSample = 2;
                break;
            case AVSampleFormat.AV_SAMPLE_FMT_S32:
            case AVSampleFormat.AV_SAMPLE_FMT_S32P:
                sampleFormat = AudioSampleFormat.S32;
                bytesPerSample = 4;
                break;
            case AVSampleFormat.AV_SAMPLE_FMT_FLT:
            case AVSampleFormat.AV_SAMPLE_FMT_FLTP:
                sampleFormat = AudioSampleFormat.F32;
                bytesPerSample = 4;
                break;
            case AVSampleFormat.AV_SAMPLE_FMT_DBL:
            case AVSampleFormat.AV_SAMPLE_FMT_DBLP:
                sampleFormat = AudioSampleFormat.F64;
                bytesPerSample = 8;
                break;
            default:
                // Unsupported sample format — skip this frame.
                ffmpeg.av_frame_unref(_frame);
                return;
        }

        int totalSamples = nbSamples * channels;
        int totalBytes = totalSamples * bytesPerSample;
        byte[] data = new byte[totalBytes];

        bool isPlanar = ffmpeg.av_sample_fmt_is_planar(fmt) != 0;

        // ── Copy audio data, interleaving planar formats ──
        fixed (byte* dest = data)
        {
            if (!isPlanar)
            {
                // Interleaved layout — straight memory copy.
                Buffer.MemoryCopy((void*)_frame->data[0], dest, totalBytes, totalBytes);
            }
            else
            {
                // Planar layout — each channel is in a separate data[] plane.
                // Interleave sample-by-sample: [L0 R0 L1 R1 ...].
                for (int s = 0; s < nbSamples; s++)
                {
                    for (int ch = 0; ch < channels; ch++)
                    {
                        byte* src = (byte*)_frame->data[(uint)ch] + s * bytesPerSample;
                        byte* dst = dest + (s * channels + ch) * bytesPerSample;
                        Buffer.MemoryCopy(src, dst, bytesPerSample, bytesPerSample);
                    }
                }
            }
        }

        // ── Compute presentation timestamp in .NET ticks ──
        long pts = _frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
            ? _frame->best_effort_timestamp
            : _frame->pts;

        if (_audioStreamIndex >= 0 && pts != ffmpeg.AV_NOPTS_VALUE)
        {
            unsafe
            {
                AVRational tb = _formatContext->streams[_audioStreamIndex]->time_base;
                pts = pts * TimeSpan.TicksPerSecond * tb.num / tb.den;
            }
        }

        // ── Deliver the decoded audio frame ──
        var frame = new DecodedAudioFrame(sampleRate, channels, sampleFormat, pts, data);
        OnAudioFrame?.Invoke(frame);

        ffmpeg.av_frame_unref(_frame);
    }

    // ═══════════════════════════════════════════════════════════════
    // Initialization — Stream Opening & Codec Setup
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Allocates and opens the FFmpeg format context for the given URL,
    /// discovers video/audio streams, and opens their respective codecs.
    /// </summary>
    /// <returns><c>true</c> if at least one stream was successfully opened.</returns>
    private unsafe bool AllocateAndOpen(string url, StreamOpenOptions? options)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            OnError?.Invoke("Cannot open stream: URL is null or empty.");
            return false;
        }

        _formatContext = ffmpeg.avformat_alloc_context();
        if (_formatContext == null)
        {
            OnError?.Invoke("Failed to allocate AVFormatContext.");
            return false;
        }

        AVDictionary* dict = null;
        try
        {
            // ── Probe / analysis budget (generous sizing matching Flyleaf defaults) ──
            ffmpeg.av_dict_set(&dict, "probesize", (50L * 1024 * 1024).ToString(), 0);       // 50 MB
            ffmpeg.av_dict_set(&dict, "analyzeduration", (10L * 1_000_000).ToString(), 0);    // 10 seconds

            // ── HTTP options from caller ──
            if (!string.IsNullOrWhiteSpace(options?.UserAgent))
                ffmpeg.av_dict_set(&dict, "user_agent", options!.UserAgent, 0);

            if (!string.IsNullOrWhiteSpace(options?.Referrer))
                ffmpeg.av_dict_set(&dict, "referer", options!.Referrer, 0);

            if (options?.Headers is { Count: > 0 })
            {
                string headerBlock = string.Join("\r\n", options.Headers.Select(static h => $"{h.Key}: {h.Value}"));
                if (!string.IsNullOrWhiteSpace(headerBlock))
                    ffmpeg.av_dict_set(&dict, "headers", headerBlock + "\r\n", 0);
            }

            // ── HLS-specific options ──
            bool hls = url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                     || url.Contains("hls", StringComparison.OrdinalIgnoreCase);

            // NOTE: Do NOT set protocol_whitelist. Flyleaf's FFmpeg build includes
            // the HLS demuxer which manages its own protocol whitelist internally
            // (file,http,https,tcp,tls,crypto). Overriding it blocks crypto and
            // data protocols needed for encrypted or embedded HLS segments.

            if (hls)
            {
                // Disable HTTP keep-alive for HLS — prevents stale connections
                // when the demuxer opens many short-lived segment requests.
                ffmpeg.av_dict_set(&dict, "http_persistent", "0", 0);
            }

            // ── Reconnect policy ──
            if (options?.EnableReconnect == true)
            {
                ffmpeg.av_dict_set(&dict, "reconnect", "1", 0);
                ffmpeg.av_dict_set(&dict, "reconnect_streamed", "1", 0);
                ffmpeg.av_dict_set(&dict, "reconnect_delay_max", "5", 0);
            }

            // Discard corrupt packets rather than passing them to the decoder.
            ffmpeg.av_dict_set(&dict, "fflags", "discardcorrupt", 0);

            if (options?.OpenTimeoutMs > 0)
                ffmpeg.av_dict_set(&dict, "timeout", (options.OpenTimeoutMs * 1000).ToString(), 0);

            // ── Open the format context ──
            // Let FFmpeg auto-detect format (including HLS) — do NOT force
            // av_find_input_format("hls"). Auto-detection works correctly
            // with this FFmpeg build.
            AVFormatContext* pFmt = _formatContext;
            Debug.Out($"Streaming FFmpeg: avformat_open_input url='{url}' hls={hls}");

            int result;
            try
            {
                result = ffmpeg.avformat_open_input(&pFmt, url, null, &dict);
            }
            catch (Exception ex)
            {
                // avformat_open_input can throw a native SEH / access-violation
                // exception instead of returning < 0. Treat it as an open failure
                // and let the caller handle cleanup.
                _formatContext = pFmt; // preserve whatever FFmpeg set
                OnError?.Invoke($"Native exception in avformat_open_input: {ex.Message}");
                return false;
            }
            _formatContext = pFmt;

            if (result < 0)
            {
                string err = GetFFmpegError(result);
                OnError?.Invoke($"Failed to open stream: {err} (code {result})");
                return false;
            }

            Debug.Out("Streaming FFmpeg: avformat_open_input succeeded.");
        }
        finally
        {
            ffmpeg.av_dict_free(&dict);
        }

        // ── Analyze stream info ──
        Debug.Out("Streaming FFmpeg: avformat_find_stream_info...");
        if (ffmpeg.avformat_find_stream_info(_formatContext, null) < 0)
        {
            OnError?.Invoke("Failed to find stream info.");
            return false;
        }

        string? fmtName = _formatContext->iformat != null
            ? System.Runtime.InteropServices.Marshal.PtrToStringAnsi((nint)_formatContext->iformat->name)
            : null;
        long durationUs = _formatContext->duration;
        Debug.Out($"Streaming FFmpeg: format='{fmtName}', nb_streams={_formatContext->nb_streams}, duration={durationUs}us");

        // ── Select best video + audio streams ──
        // Try FFmpeg's heuristic first, then fall back to first-discovered.
        _videoStreamIndex = ffmpeg.av_find_best_stream(_formatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        _audioStreamIndex = ffmpeg.av_find_best_stream(_formatContext, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);

        if (_videoStreamIndex < 0 || _audioStreamIndex < 0)
        {
            for (int i = 0; i < _formatContext->nb_streams; i++)
            {
                var codecpar = _formatContext->streams[i]->codecpar;
                if (codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && _videoStreamIndex < 0)
                    _videoStreamIndex = i;
                else if (codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO && _audioStreamIndex < 0)
                    _audioStreamIndex = i;
            }
        }

        Debug.Out($"Streaming FFmpeg: videoStream={_videoStreamIndex}, audioStream={_audioStreamIndex}");

        if (_videoStreamIndex < 0 && _audioStreamIndex < 0)
        {
            OnError?.Invoke("No video or audio stream found.");
            return false;
        }

        // ── Open codecs for the selected streams ──
        if (_videoStreamIndex >= 0)
            OpenVideoCodec();

        if (_audioStreamIndex >= 0)
            OpenAudioCodec();

        // Allocate reusable frame and packet buffers for the decode loop.
        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();

        return true;
    }

    /// <summary>
    /// Opens and configures the video codec for the selected video stream.
    /// Detects frame rate from stream metadata and reports it via
    /// <see cref="OnVideoFrameRateDetected"/>.
    /// </summary>
    private unsafe void OpenVideoCodec()
    {
        AVStream* stream = _formatContext->streams[_videoStreamIndex];
        var codecpar = _formatContext->streams[_videoStreamIndex]->codecpar;
        var codec = ffmpeg.avcodec_find_decoder(codecpar->codec_id);
        if (codec == null)
        {
            OnError?.Invoke($"Unsupported video codec: {codecpar->codec_id}");
            _videoStreamIndex = -1;
            return;
        }

        _videoCodecContext = ffmpeg.avcodec_alloc_context3(codec);
        ffmpeg.avcodec_parameters_to_context(_videoCodecContext, codecpar);

        // Use 4-thread frame-level parallelism for software decode.
        _videoCodecContext->thread_count = 4;
        _videoCodecContext->thread_type = ffmpeg.FF_THREAD_FRAME;

        // Hardware acceleration disabled: Flyleaf's D3D11 HW frames are incompatible
        // with our OpenGL pipeline, and CUDA/D3D11VA caused ucrtbase.dll native crashes.
        // Pure software decode is sufficient for streaming video.

        int result = ffmpeg.avcodec_open2(_videoCodecContext, codec, null);
        if (result < 0)
        {
            OnError?.Invoke($"Could not open video codec: {GetFFmpegError(result)}");
            fixed (AVCodecContext** p = &_videoCodecContext)
                ffmpeg.avcodec_free_context(p);
            _videoCodecContext = null;
            _videoStreamIndex = -1;
            return;
        }

        // Detect frame rate from multiple sources (avg > real > codec).
        double? detectedFps = TryGetFrameRate(stream->avg_frame_rate)
            ?? TryGetFrameRate(stream->r_frame_rate)
            ?? TryGetFrameRate(_videoCodecContext->framerate);

        if (detectedFps is > 0)
        {
            OnVideoFrameRateDetected?.Invoke(detectedFps.Value);
            Debug.Out($"Streaming FFmpeg: detected video fps={detectedFps.Value:F3}");

            long frameTicks = (long)Math.Round(TimeSpan.TicksPerSecond / detectedFps.Value);
            _videoFrameDurationTicks = Math.Clamp(frameTicks,
                TimeSpan.TicksPerMillisecond,
                TimeSpan.TicksPerSecond);
        }
    }

    /// <summary>
    /// Opens and configures the audio codec for the selected audio stream.
    /// </summary>
    private unsafe void OpenAudioCodec()
    {
        var codecpar = _formatContext->streams[_audioStreamIndex]->codecpar;
        var codec = ffmpeg.avcodec_find_decoder(codecpar->codec_id);
        if (codec == null)
        {
            OnError?.Invoke($"Unsupported audio codec: {codecpar->codec_id}");
            _audioStreamIndex = -1;
            return;
        }

        _audioCodecContext = ffmpeg.avcodec_alloc_context3(codec);
        ffmpeg.avcodec_parameters_to_context(_audioCodecContext, codecpar);

        int result = ffmpeg.avcodec_open2(_audioCodecContext, codec, null);
        if (result < 0)
        {
            OnError?.Invoke($"Could not open audio codec: {GetFFmpegError(result)}");
            fixed (AVCodecContext** p = &_audioCodecContext)
                ffmpeg.avcodec_free_context(p);
            _audioCodecContext = null;
            _audioStreamIndex = -1;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Hardware Acceleration (currently disabled — retained for future use)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempts to initialize hardware-accelerated decoding for the given
    /// device type. Currently unused because HW accel is incompatible with
    /// the OpenGL rendering pipeline.
    /// </summary>
    /// <returns><c>true</c> if HW accel was successfully initialized.</returns>
    private unsafe bool TryInitHwAccel(AVCodecContext* ctx, AVHWDeviceType type)
    {
        fixed (AVBufferRef** p = &_hwDeviceContext)
        {
            if (ffmpeg.av_hwdevice_ctx_create(p, type, null, null, 0) < 0)
                return false;
        }

        ctx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceContext);

        // Search for a matching HW config that supports the requested device type.
        for (int i = 0; ; i++)
        {
            var config = ffmpeg.avcodec_get_hw_config(ctx->codec, i);
            if (config == null)
                break;

            if ((config->methods & 1) != 0 && config->device_type == type)
            {
                _hwPixelFormat = config->pix_fmt;
                return true;
            }
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════════
    // Cleanup — Native Resource Deallocation
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Frees all allocated FFmpeg native resources in the correct order:
    /// scaler → frame/packet → codecs → HW device → format context.
    /// </summary>
    private unsafe void Cleanup()
    {
        _running = false;

        _lastVideoPtsTicks = long.MinValue;
        _syntheticVideoPtsTicks = long.MinValue;
        _videoMissingPtsStreak = 0;

        // Free the software scaler context.
        if (_swsContext != null)
        {
            ffmpeg.sws_freeContext(_swsContext);
            _swsContext = null;
        }

        // Free the reusable frame buffer.
        if (_frame != null)
        {
            fixed (AVFrame** p = &_frame)
                ffmpeg.av_frame_free(p);
        }

        // Free the reusable packet buffer.
        if (_packet != null)
        {
            fixed (AVPacket** p = &_packet)
                ffmpeg.av_packet_free(p);
        }

        // Free codec contexts.
        if (_videoCodecContext != null)
        {
            fixed (AVCodecContext** p = &_videoCodecContext)
                ffmpeg.avcodec_free_context(p);
        }

        if (_audioCodecContext != null)
        {
            fixed (AVCodecContext** p = &_audioCodecContext)
                ffmpeg.avcodec_free_context(p);
        }

        // Free the HW device context (if any).
        if (_hwDeviceContext != null)
        {
            fixed (AVBufferRef** p = &_hwDeviceContext)
                ffmpeg.av_buffer_unref(p);
        }

        // Close the format context (also frees stream info).
        if (_formatContext != null)
        {
            fixed (AVFormatContext** p = &_formatContext)
                ffmpeg.avformat_close_input(p);
        }
    }

    /// <summary>
    /// Converts FFmpeg video timestamps to a monotonic .NET-tick timeline.
    /// When FFmpeg timestamps are missing or non-monotonic, synthesizes PTS
    /// using the estimated frame duration so downstream A/V sync logic can
    /// stay on audio-clock pacing.
    /// </summary>
    private unsafe long ResolveVideoPtsTicks(AVFrame* decodedFrame)
    {
        long sourcePts = decodedFrame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
            ? decodedFrame->best_effort_timestamp
            : decodedFrame->pts;

        long resolvedPts = long.MinValue;
        bool hasResolvedPts = false;

        if (_videoStreamIndex >= 0 && sourcePts != ffmpeg.AV_NOPTS_VALUE)
        {
            AVRational tb = _formatContext->streams[_videoStreamIndex]->time_base;
            AVRational dstTb = new() { num = 1, den = (int)TimeSpan.TicksPerSecond };
            resolvedPts = ffmpeg.av_rescale_q(sourcePts, tb, dstTb);
            hasResolvedPts = resolvedPts >= 0;
        }

        // Reject non-monotonic timestamps; they break hasValidPts checks in
        // the session and force wall-clock fallback.
        if (hasResolvedPts && _lastVideoPtsTicks != long.MinValue && resolvedPts <= _lastVideoPtsTicks)
            hasResolvedPts = false;

        if (hasResolvedPts)
        {
            _videoMissingPtsStreak = 0;
            _syntheticVideoPtsTicks = resolvedPts;
            _lastVideoPtsTicks = resolvedPts;
            return resolvedPts;
        }

        // Missing/invalid timestamp: synthesize a monotonic PTS.
        _videoMissingPtsStreak++;

        long step = Math.Max(TimeSpan.TicksPerMillisecond, _videoFrameDurationTicks);
        if (_syntheticVideoPtsTicks == long.MinValue)
        {
            _syntheticVideoPtsTicks = _lastVideoPtsTicks != long.MinValue
                ? _lastVideoPtsTicks + step
                : step;
        }
        else
        {
            _syntheticVideoPtsTicks += step;
        }

        if (_videoMissingPtsStreak == 1 || _videoMissingPtsStreak % 300 == 0)
        {
            Debug.Out($"[AV Video] Synthesizing PTS for frame(s): streak={_videoMissingPtsStreak}, " +
                      $"stepMs={step / (double)TimeSpan.TicksPerMillisecond:F2}");
        }

        _lastVideoPtsTicks = _syntheticVideoPtsTicks;
        return _syntheticVideoPtsTicks;
    }

    // ═══════════════════════════════════════════════════════════════
    // Utilities
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates an <see cref="AVRational"/> frame rate and returns it as
    /// a <c>double</c>, or <c>null</c> if the value is out of the sane
    /// range (1–240 fps).
    /// </summary>
    private static double? TryGetFrameRate(AVRational rate)
    {
        if (rate.num <= 0 || rate.den <= 0)
            return null;

        double fps = (double)rate.num / rate.den;
        return fps is >= 1.0 and <= 240.0 ? fps : null;
    }

    /// <summary>
    /// Converts an FFmpeg error code into a human-readable error string.
    /// </summary>
    private static unsafe string GetFFmpegError(int errorCode)
    {
        byte[] buf = new byte[1024];
        fixed (byte* ptr = buf)
        {
            ffmpeg.av_strerror(errorCode, ptr, (ulong)buf.Length);
            return new string((sbyte*)ptr).Trim('\0');
        }
    }
}
