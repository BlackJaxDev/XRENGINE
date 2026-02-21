using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine.Rendering.VideoStreaming;

/// <summary>
/// Opens an HLS (or other FFmpeg-supported) URL and decodes video/audio frames
/// on a background thread, delivering them via callbacks.
/// Replaces the previous Flyleaf-based HlsPlayerAdapter.
/// </summary>
internal sealed class FFmpegStreamDecoder : IDisposable
{
    private const AVPixelFormat TargetPixelFormat = AVPixelFormat.AV_PIX_FMT_RGB24;
    private const int MaxReadRetries = 12;

    private readonly Lock _lock = new();
    private volatile bool _disposed;
    private volatile bool _running;
    private Thread? _decodeThread;

    // FFmpeg contexts (all access guarded by _lock during init/cleanup, then only on decode thread)
    private unsafe AVFormatContext* _formatContext;
    private unsafe AVCodecContext* _videoCodecContext;
    private unsafe AVCodecContext* _audioCodecContext;
    private unsafe AVBufferRef* _hwDeviceContext;
    private unsafe AVFrame* _frame;
    private unsafe AVPacket* _packet;
    private unsafe SwsContext* _swsContext;
    private AVPixelFormat _hwPixelFormat;
    private int _swsWidth;
    private int _swsHeight;
    private AVPixelFormat _swsSrcFormat;

    private int _videoStreamIndex = -1;
    private int _audioStreamIndex = -1;
    private int _readRetryCount;

    // Callbacks
    public Action<DecodedVideoFrame>? OnVideoFrame { get; set; }
    public Action<DecodedAudioFrame>? OnAudioFrame { get; set; }
    public Action<int, int>? OnVideoSizeChanged { get; set; }
    public Action<string>? OnError { get; set; }

    private int _lastReportedWidth;
    private int _lastReportedHeight;

    // Frame pacing: prevent decoding faster than real-time
    private long _firstPtsTicks = long.MinValue;
    private long _wallClockStartTicks;
    private const long MaxAheadTicks = TimeSpan.TicksPerSecond; // allow 1s decode-ahead

    /// <summary>
    /// Open the stream and start decoding on a background thread.
    /// </summary>
    public unsafe Task OpenAsync(string url, StreamOpenOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            if (!AllocateAndOpen(url, options))
                throw new InvalidOperationException($"FFmpegStreamDecoder failed to open '{url}'.");

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

    public void Stop()
    {
        _running = false;
        _decodeThread?.Join(TimeSpan.FromSeconds(3));
        Cleanup();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    // ─── Decode loop ───────────────────────────────────────────────

    private void DecodeLoop()
    {
        _wallClockStartTicks = Environment.TickCount64 * TimeSpan.TicksPerMillisecond;
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
    /// Sleeps the decode thread if we are decoding faster than real-time.
    /// This prevents audio buffer overflow and wasted CPU on frames we'd drop.
    /// </summary>
    private void PaceFrame(long ptsTicks)
    {
        if (ptsTicks <= 0)
            return;

        if (_firstPtsTicks == long.MinValue)
        {
            _firstPtsTicks = ptsTicks;
            _wallClockStartTicks = Environment.TickCount64 * TimeSpan.TicksPerMillisecond;
            return;
        }

        long streamElapsed = ptsTicks - _firstPtsTicks;
        long wallElapsed = Environment.TickCount64 * TimeSpan.TicksPerMillisecond - _wallClockStartTicks;
        long ahead = streamElapsed - wallElapsed;

        if (ahead > MaxAheadTicks)
        {
            int sleepMs = (int)((ahead - MaxAheadTicks / 2) / TimeSpan.TicksPerMillisecond);
            if (sleepMs > 0 && sleepMs < 5000)
                Thread.Sleep(sleepMs);
        }
    }

    private unsafe void ReadAndDecodeOnePacket()
    {
        int result = ffmpeg.av_read_frame(_formatContext, _packet);
        if (result < 0)
        {
            if (result == ffmpeg.AVERROR_EOF)
            {
                _running = false;
                return;
            }

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

        _readRetryCount = 0;

        try
        {
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

    // ─── Video decoding ────────────────────────────────────────────

    private unsafe void DecodeVideoPacket()
    {
        if (_videoCodecContext == null)
            return;

        if (ffmpeg.avcodec_send_packet(_videoCodecContext, _packet) < 0)
            return;

        while (ffmpeg.avcodec_receive_frame(_videoCodecContext, _frame) >= 0)
            ProcessVideoFrame();
    }

    private unsafe void ProcessVideoFrame()
    {
        AVFrame* decodedFrame = GetHwFrame(_frame);
        try
        {
            int width = decodedFrame->width;
            int height = decodedFrame->height;

            if (width <= 0 || height <= 0)
                return;

            // Notify size change
            if (width != _lastReportedWidth || height != _lastReportedHeight)
            {
                _lastReportedWidth = width;
                _lastReportedHeight = height;
                OnVideoSizeChanged?.Invoke(width, height);
            }

            // Convert to RGB24 if needed
            AVFrame* rgbFrame = decodedFrame;
            bool freeRgb = false;

            if (decodedFrame->format != (int)TargetPixelFormat)
            {
                var srcFmt = (AVPixelFormat)decodedFrame->format;
                if (_swsContext == null || width != _swsWidth || height != _swsHeight || srcFmt != _swsSrcFormat)
                {
                    if (_swsContext != null)
                        ffmpeg.sws_freeContext(_swsContext);

                    // SWS_BILINEAR = 2 (not exposed by FFmpeg.AutoGen)
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

            // Copy to managed array
            int dataSize = width * height * 3;
            byte[] data = new byte[dataSize];
            fixed (byte* dest = data)
            {
                byte* src = (byte*)rgbFrame->data[0];
                int srcStride = rgbFrame->linesize[0];
                int dstStride = width * 3;
                for (int row = 0; row < height; row++)
                    Buffer.MemoryCopy(src + row * srcStride, dest + row * dstStride, dstStride, dstStride);
            }

            long pts = decodedFrame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
                ? decodedFrame->best_effort_timestamp
                : decodedFrame->pts;

            // Convert pts from stream timebase to ticks (100ns units)
            if (_videoStreamIndex >= 0 && pts != ffmpeg.AV_NOPTS_VALUE)
            {
                AVRational tb = _formatContext->streams[_videoStreamIndex]->time_base;
                pts = pts * TimeSpan.TicksPerSecond * tb.num / tb.den;
            }

            var frame = new DecodedVideoFrame(width, height, pts, VideoPixelFormat.Rgb24, data);
            PaceFrame(pts);
            OnVideoFrame?.Invoke(frame);

            if (freeRgb)
                ffmpeg.av_frame_free(&rgbFrame);
        }
        finally
        {
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

    private unsafe AVFrame* GetHwFrame(AVFrame* frame)
    {
        if (frame->format != (int)_hwPixelFormat)
            return frame;

        var swFrame = ffmpeg.av_frame_alloc();
        if (ffmpeg.av_hwframe_transfer_data(swFrame, frame, 0) < 0)
        {
            ffmpeg.av_frame_free(&swFrame);
            return frame;
        }

        ffmpeg.av_frame_unref(frame);
        return swFrame;
    }

    // ─── Audio decoding ────────────────────────────────────────────

    private unsafe void DecodeAudioPacket()
    {
        if (_audioCodecContext == null)
            return;

        if (ffmpeg.avcodec_send_packet(_audioCodecContext, _packet) < 0)
            return;

        while (ffmpeg.avcodec_receive_frame(_audioCodecContext, _frame) >= 0)
            ProcessAudioFrame();
    }

    private unsafe void ProcessAudioFrame()
    {
        int channels = _audioCodecContext->ch_layout.nb_channels;
        int sampleRate = _audioCodecContext->sample_rate;
        int nbSamples = _frame->nb_samples;

        if (channels <= 0 || sampleRate <= 0 || nbSamples <= 0)
            return;

        // Determine sample format and data size
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
                // Unsupported format - skip
                ffmpeg.av_frame_unref(_frame);
                return;
        }

        int totalSamples = nbSamples * channels;
        int totalBytes = totalSamples * bytesPerSample;
        byte[] data = new byte[totalBytes];

        bool isPlanar = ffmpeg.av_sample_fmt_is_planar(fmt) != 0;

        fixed (byte* dest = data)
        {
            if (!isPlanar)
            {
                // Interleaved - straight copy
                Buffer.MemoryCopy((void*)_frame->data[0], dest, totalBytes, totalBytes);
            }
            else
            {
                // Planar - interleave manually
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

        var frame = new DecodedAudioFrame(sampleRate, channels, sampleFormat, pts, data);
        PaceFrame(pts);
        OnAudioFrame?.Invoke(frame);

        ffmpeg.av_frame_unref(_frame);
    }

    // ─── Init / Cleanup ────────────────────────────────────────────

    private unsafe bool AllocateAndOpen(string url, StreamOpenOptions? options)
    {
        _formatContext = ffmpeg.avformat_alloc_context();

        AVDictionary* dict = null;
        try
        {
            // Apply open options
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

            bool hls = url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
            if (hls)
                ffmpeg.av_dict_set(&dict, "protocol_whitelist", "file,http,https,tcp,tls", 0);

            if (options?.EnableReconnect == true)
            {
                ffmpeg.av_dict_set(&dict, "reconnect", "1", 0);
                ffmpeg.av_dict_set(&dict, "reconnect_streamed", "1", 0);
                ffmpeg.av_dict_set(&dict, "reconnect_delay_max", "5", 0);
            }

            ffmpeg.av_dict_set(&dict, "fflags", "discardcorrupt", 0);

            if (options?.OpenTimeoutMs > 0)
                ffmpeg.av_dict_set(&dict, "timeout", (options.OpenTimeoutMs * 1000).ToString(), 0);

            AVFormatContext* pFmt = _formatContext;
            int result;
            if (hls)
            {
                var inputFormat = ffmpeg.av_find_input_format("hls");
                result = inputFormat != null
                    ? ffmpeg.avformat_open_input(&pFmt, url, inputFormat, &dict)
                    : ffmpeg.avformat_open_input(&pFmt, url, null, &dict);
            }
            else
            {
                result = ffmpeg.avformat_open_input(&pFmt, url, null, &dict);
            }
            _formatContext = pFmt;

            if (result < 0)
            {
                string err = GetFFmpegError(result);
                OnError?.Invoke($"Failed to open stream: {err} (code {result})");
                return false;
            }
        }
        finally
        {
            ffmpeg.av_dict_free(&dict);
        }

        if (ffmpeg.avformat_find_stream_info(_formatContext, null) < 0)
        {
            OnError?.Invoke("Failed to find stream info.");
            return false;
        }

        // Find streams
        for (int i = 0; i < _formatContext->nb_streams; i++)
        {
            var codecpar = _formatContext->streams[i]->codecpar;
            if (codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && _videoStreamIndex < 0)
                _videoStreamIndex = i;
            else if (codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO && _audioStreamIndex < 0)
                _audioStreamIndex = i;
        }

        if (_videoStreamIndex < 0 && _audioStreamIndex < 0)
        {
            OnError?.Invoke("No video or audio stream found.");
            return false;
        }

        if (_videoStreamIndex >= 0)
            OpenVideoCodec();

        if (_audioStreamIndex >= 0)
            OpenAudioCodec();

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();

        return true;
    }

    private unsafe void OpenVideoCodec()
    {
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
        }
    }

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

    private unsafe bool TryInitHwAccel(AVCodecContext* ctx, AVHWDeviceType type)
    {
        fixed (AVBufferRef** p = &_hwDeviceContext)
        {
            if (ffmpeg.av_hwdevice_ctx_create(p, type, null, null, 0) < 0)
                return false;
        }

        ctx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceContext);

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

    private unsafe void Cleanup()
    {
        _running = false;

        if (_swsContext != null)
        {
            ffmpeg.sws_freeContext(_swsContext);
            _swsContext = null;
        }

        if (_frame != null)
        {
            fixed (AVFrame** p = &_frame)
                ffmpeg.av_frame_free(p);
        }

        if (_packet != null)
        {
            fixed (AVPacket** p = &_packet)
                ffmpeg.av_packet_free(p);
        }

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

        if (_hwDeviceContext != null)
        {
            fixed (AVBufferRef** p = &_hwDeviceContext)
                ffmpeg.av_buffer_unref(p);
        }

        if (_formatContext != null)
        {
            fixed (AVFormatContext** p = &_formatContext)
                ffmpeg.avformat_close_input(p);
        }
    }

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
