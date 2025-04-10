using FFmpeg.AutoGen;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using XREngine.Components.Scene;
using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.UI
{
    public unsafe class UIVideoComponent : UIMaterialComponent
    {
        //Optional AudioSourceComponent for audio streaming
        public AudioSourceComponent? AudioSource => GetSiblingComponent<AudioSourceComponent>();

        private readonly XRMaterialFrameBuffer _fbo;

        private string? _streamUrl = "http://pendelcam.kip.uni-heidelberg.de/mjpg/video.mjpg";
        public string? StreamUrl
        {
            get => _streamUrl;
            set => SetField(ref _streamUrl, value);
        }

        private AVFormatContext* _formatContext;
        private AVCodecContext* _videoCodecContext;
        private AVCodecContext* _audioCodecContext;
        // FFmpeg context with hardware acceleration
        private AVBufferRef* _hwDeviceContext;
        private AVPixelFormat _hwPixelFormat;

        private int _videoStreamIndex = -1;
        private int _audioStreamIndex = -1;
        //These bools are to prevent infinite pre-rendering recursion
        private bool _updating = false;
        private bool _swapping = false;
        private bool _rendering = false;

        private int _currentPboIndex = 0;
        private readonly XRDataBuffer[] _pboBuffers =
        [
            new("", EBufferTarget.PixelUnpackBuffer, 1, EComponentType.Byte, 3, false, false)
            {
                Resizable = false,
                Usage = EBufferUsage.StreamDraw,
                RangeFlags = EBufferMapRangeFlags.InvalidateBuffer | EBufferMapRangeFlags.Write,
                StorageFlags = EBufferMapStorageFlags.Write | EBufferMapStorageFlags.Coherent | EBufferMapStorageFlags.Persistent,
            },
            new("", EBufferTarget.PixelUnpackBuffer, 1, EComponentType.Byte, 3, false, false)
            {
                Resizable = false,
                Usage = EBufferUsage.StreamDraw,
                RangeFlags = EBufferMapRangeFlags.InvalidateBuffer | EBufferMapRangeFlags.Write,
                StorageFlags = EBufferMapStorageFlags.Write | EBufferMapStorageFlags.Coherent | EBufferMapStorageFlags.Persistent,
            }
        ];

        private const int MAX_QUEUE_SIZE = 3; // Limit frame queue to prevent memory buildup

        // Frame queue with size limit
        private readonly BlockingCollection<byte[]> _frameQueue = new(MAX_QUEUE_SIZE);
        private byte[]? _currentFrame;
        private readonly Lock _frameLock = new();

        private AVFrame* frame;
        private AVPacket* packet;
        private SwsContext* swsContext;

        private const AVPixelFormat videoTextureFormat = AVPixelFormat.AV_PIX_FMT_RGB24;

        public UIVideoComponent() : base(GetVideoMaterial())
            => _fbo = new XRMaterialFrameBuffer(Material);

        public XRTexture2D? VideoTexture => Material?.Textures[0] as XRTexture2D;

        private static XRMaterial GetVideoMaterial()
        {
            XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(1u, 1u,
                EPixelInternalFormat.Rgb8,
                EPixelFormat.Rgb,
                EPixelType.UnsignedByte,
                EFrameBufferAttachment.ColorAttachment0);
            //texture.SizedInternalFormat = ESizedInternalFormat.Rgb8;
            //texture.Resizable = false;
            return new XRMaterial([texture], XRShader.EngineShader(Path.Combine("Common", "UnlitTexturedForward.fs"), EShaderType.Fragment));
        }

        //protected override void OnResizeLayout(BoundingRectangle parentRegion)
        //{
        //    base.OnResizeLayout(parentRegion);

        //    int
        //        w = (int)ActualWidth.ClampMin(1.0f),
        //        h = (int)ActualHeight.ClampMin(1.0f);

        //    Viewport.Resize(w, h);
        //    _fbo.Resize(w, h);
        //}

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            AllocateDecode();
            RegisterTick(Components.ETickGroup.Normal, Components.ETickOrder.Logic, DecodeFrame);
            Engine.Time.Timer.CollectVisible += ConsumeFrameQueue;
        }
        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            UnregisterTick(Components.ETickGroup.Normal, Components.ETickOrder.Logic, DecodeFrame);
            Engine.Time.Timer.CollectVisible -= ConsumeFrameQueue;
            CleanupDecode();
            _frameQueue.CompleteAdding();
        }

        //private unsafe void DecodeThread()
        //{
        //    AllocateDecode();
        //    while (IsActiveInHierarchy)
        //        Decode();
        //    CleanupDecode();
        //}

        public void ConsumeFrameQueue()
        {
            // Try to get the latest frame from the queue
            if (_frameQueue.TryTake(out var frame))
            {
                lock (_frameLock)
                {
                    // Dispose previous frame if exists
                    _currentFrame = frame;
                }
            }

            // Update texture if we have a frame
            if (_currentFrame != null)
                UploadFrameToTexturePBO(_currentFrame);
        }
        private unsafe void UploadFrameToTexturePBO(byte[] frame)
        {
            var tex = VideoTexture;
            if (tex is null)
                return;

            _currentPboIndex = (_currentPboIndex + 1) % _pboBuffers.Length;
            XRDataBuffer pbo = _pboBuffers[_currentPboIndex];

            //Pre-allocate the texture's data buffer with the frame's data length
            pbo.Resize((uint)frame.Length, false);

            pbo.Bind();
            {
                //Setting streaming PBO will invalidate the texture, forcing it to push the streaming pbo's data to the texture on next bind (which is now)
                tex.Mipmaps[0].StreamingPBO = pbo;
                tex.Bind();

                //Push the frame data to the PBO
                PushFrameToPBO(frame, pbo);

                //Calling push data will do a sub-image update of the texture with the PBO's data
                tex.PushData();
            }
            pbo.Unbind();
        }

        private static void PushFrameToPBO(byte[] data, XRDataBuffer pbo)
        {
            pbo.MapBufferData();
            foreach (var apiBuffer in pbo.ActivelyMapping)
            {
                if (apiBuffer is null)
                    continue;

                // Copy data to api PBO
                var ptr = apiBuffer.GetMappedAddress();
                if (ptr is null)
                    continue;

                Marshal.Copy(data, 0, ptr.Value, data.Length);
            }
            pbo.UnmapBufferData();
        }

        private unsafe bool InitHardwareAcceleration(AVCodecContext* codecContext, AVHWDeviceType type)
        {
            fixed (AVBufferRef** p = &_hwDeviceContext)
            {
                if (ffmpeg.av_hwdevice_ctx_create(p, type, null, null, 0) < 0)
                    return false;
            }

            codecContext->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceContext);

            // Get the supported HW pixel format
            for (int i = 0; ; i++)
            {
                var config = ffmpeg.avcodec_get_hw_config(codecContext->codec, i);
                if (config == null)
                    break;

                if ((config->methods & 1) != 0 && //AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX
                    config->device_type == type)
                {
                    _hwPixelFormat = config->pix_fmt;
                    return true;
                }
            }

            return false;
        }

        private unsafe AVFrame* GetHwFrame(AVFrame* frame)
        {
            if (frame->format != (int)_hwPixelFormat)
                return frame;

            // Create a software frame
            var swFrame = ffmpeg.av_frame_alloc();
            if (ffmpeg.av_hwframe_transfer_data(swFrame, frame, 0) < 0)
            {
                ffmpeg.av_frame_free(&swFrame);
                return frame;
            }

            ffmpeg.av_frame_unref(frame);
            return swFrame;
        }

        private void DecodeFrame()
        {
            if (ffmpeg.av_read_frame(_formatContext, packet) < 0)
                return;
            
            if (packet->stream_index == _videoStreamIndex)
                DecodeVideo();
            else if (packet->stream_index == _audioStreamIndex)
                DecodeAudio();

            ffmpeg.av_packet_unref(packet);
        }

        private void DecodeAudio()
        {
            if (ffmpeg.avcodec_send_packet(_audioCodecContext, packet) < 0)
                return;
            
            while (ffmpeg.avcodec_receive_frame(_audioCodecContext, frame) >= 0)
                ProcessAudioFrame();
        }

        private void DecodeVideo()
        {
            if (ffmpeg.avcodec_send_packet(_videoCodecContext, packet) < 0)
                return;
            
            while (ffmpeg.avcodec_receive_frame(_videoCodecContext, frame) >= 0)
                ProcessVideoFrame();
        }

        private void AllocateDecode()
        {
            //ffmpeg.avdevice_register_all();
            //ffmpeg.avformat_network_init();

            // Allocate format context
            _formatContext = ffmpeg.avformat_alloc_context();

            // Set options for low latency streaming
            AVDictionary* options = null;
            string timeout = "5000000"; // 5 sec timeout
            ffmpeg.av_dict_set(&options, "rtsp_transport", "tcp", 0);
            ffmpeg.av_dict_set(&options, "stimeout", timeout, 0);
            ffmpeg.av_dict_set(&options, "fflags", "nobuffer", 0);
            ffmpeg.av_dict_set(&options, "flags", "low_delay", 0);
            ffmpeg.av_dict_set(&options, "timeout", timeout, 0);
            ffmpeg.av_dict_set(&options, "rw_timeout", timeout, 0);

            // Open input stream
            var pFormatContext = _formatContext;
            if (ffmpeg.avformat_open_input(&pFormatContext, _streamUrl, null, &options) != 0)
            {
                Debug.LogWarning("Failed to open input stream");
                return;
            }

            // Retrieve stream information
            if (ffmpeg.avformat_find_stream_info(_formatContext, null) < 0)
            {
                Debug.LogWarning("Failed to find stream info");
                return;
            }

            // Find video stream
            _videoStreamIndex = -1;
            for (int i = 0; i < _formatContext->nb_streams; i++)
            {
                var codecpar = _formatContext->streams[i]->codecpar;
                if (codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && _videoStreamIndex < 0)
                    _videoStreamIndex = i;
                else if (codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO && _audioStreamIndex < 0)
                    _audioStreamIndex = i;
            }

            if (_videoStreamIndex == -1 && _audioStreamIndex == -1)
            {
                Debug.LogWarning("No video or audio stream found");
                return;
            }

            OpenVideoContext();
            OpenAudioContext();
            
            frame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();
            swsContext = (SwsContext*)null;
        }

        private void CleanupDecode()
        {
            fixed (AVFrame** p = &frame)
                ffmpeg.av_frame_free(p);

            fixed (AVPacket** p = &packet)
                ffmpeg.av_packet_free(p);

            if (swsContext != null)
                ffmpeg.sws_freeContext(swsContext);

            if (_videoCodecContext != null)
            {
                fixed (AVCodecContext** p = &_videoCodecContext)
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
            if (_videoCodecContext != null)
            {
                fixed (AVCodecContext** p = &_videoCodecContext)
                {
                    ffmpeg.avcodec_free_context(p);
                }
            }
            if (_audioCodecContext != null)
            {
                fixed (AVCodecContext** p = &_audioCodecContext)
                {
                    ffmpeg.avcodec_free_context(p);
                }
            }
            if (_formatContext != null)
            {
                fixed (AVFormatContext** p = &_formatContext)
                {
                    ffmpeg.avformat_close_input(p);
                    ffmpeg.avformat_free_context(*p);
                }
            }

            lock (_frameLock)
            {
                _currentFrame = null;
            }

            while (_frameQueue.TryTake(out var f)) ;

            ffmpeg.avformat_network_deinit();
        }

        private void OpenAudioContext()
        {
            if (_audioStreamIndex < 0)
                return;

            // Get codec parameters and find decoder
            var codecParameters = _formatContext->streams[_audioStreamIndex]->codecpar;
            var codec = ffmpeg.avcodec_find_decoder(codecParameters->codec_id);
            if (codec == null)
                throw new Exception("Unsupported codec");

            // Allocate codec context
            _audioCodecContext = ffmpeg.avcodec_alloc_context3(codec);
            ffmpeg.avcodec_parameters_to_context(_audioCodecContext, codecParameters);

            // Open codec
            if (ffmpeg.avcodec_open2(_audioCodecContext, codec, null) < 0)
                throw new Exception("Could not open codec");
        }

        private void OpenVideoContext()
        {
            if (_videoStreamIndex < 0)
                return;
            
            // Get codec parameters and find decoder
            var codecParameters = _formatContext->streams[_videoStreamIndex]->codecpar;
            var codec = ffmpeg.avcodec_find_decoder(codecParameters->codec_id);
            if (codec == null)
                throw new Exception("Unsupported codec");

            // Allocate codec context
            _videoCodecContext = ffmpeg.avcodec_alloc_context3(codec);
            ffmpeg.avcodec_parameters_to_context(_videoCodecContext, codecParameters);

            // Try to initialize hardware acceleration (CUDA first, then others)
            AVHWDeviceType hwType = AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA;
            if (!InitHardwareAcceleration(_videoCodecContext, hwType))
            {
                hwType = AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA;
                if (!InitHardwareAcceleration(_videoCodecContext, hwType))
                    hwType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
            }

            // Open codec
            if (ffmpeg.avcodec_open2(_videoCodecContext, codec, null) < 0)
                throw new Exception("Could not open codec");
        }

        private void ProcessAudioFrame()
        {
            var audioSource = AudioSource;
            if (audioSource is null)
                return;

            short[] samples = new short[frame->nb_samples * _audioCodecContext->ch_layout.nb_channels];

            Marshal.Copy((IntPtr)frame->data[0], samples, 0, samples.Length);

            int freq = _audioCodecContext->sample_rate;
            bool stereo = _audioCodecContext->ch_layout.nb_channels == 2;

            audioSource.EnqueueStreamingBuffers(freq, stereo, samples);
        }

        private void ProcessVideoFrame()
        {
            // Handle hardware frame if needed
            var decodedFrame = GetHwFrame(frame);

            // Convert frame to RGB24 if needed
            if (decodedFrame->format != (int)videoTextureFormat)
            {
                if (swsContext == null)
                {
                    swsContext = ffmpeg.sws_getContext(
                        decodedFrame->width,
                        decodedFrame->height,
                        (AVPixelFormat)decodedFrame->format,
                        decodedFrame->width,
                        decodedFrame->height,
                        videoTextureFormat,
                        ffmpeg.SWS_BILINEAR,
                        null,
                        null,
                        null);
                }

                var convertedFrame = ffmpeg.av_frame_alloc();
                convertedFrame->format = (int)videoTextureFormat;
                convertedFrame->width = decodedFrame->width;
                convertedFrame->height = decodedFrame->height;
                ffmpeg.av_frame_get_buffer(convertedFrame, 0);

                ffmpeg.sws_scale(
                    swsContext,
                    decodedFrame->data,
                    decodedFrame->linesize,
                    0,
                    decodedFrame->height,
                    convertedFrame->data,
                    convertedFrame->linesize);

                if (decodedFrame != frame)
                    ffmpeg.av_frame_free(&decodedFrame);
                decodedFrame = convertedFrame;
            }

            // Copy frame data to managed memory
            var data = new byte[decodedFrame->width * decodedFrame->height * 3];
            fixed (byte* p = data)
            {
                var ptr = (byte*)decodedFrame->data[0];
                for (int i = 0; i < decodedFrame->height; i++)
                {
                    Buffer.MemoryCopy(ptr, p + i * decodedFrame->width * 3, decodedFrame->width * 3, decodedFrame->linesize[0]);
                    ptr += decodedFrame->linesize[0];
                }
            }

            // Add to frame queue (will block if queue is full)
            try
            {
                _frameQueue.Add(data);
            }
            catch (InvalidOperationException)
            {
                // Queue was completed (during shutdown)
            }

            if (decodedFrame != frame)
                ffmpeg.av_frame_free(&decodedFrame);
        }
    }
}
