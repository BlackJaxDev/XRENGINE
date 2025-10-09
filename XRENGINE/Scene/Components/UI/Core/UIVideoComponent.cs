using FFmpeg.AutoGen;
using FlyleafLib;
using FlyleafLib.Controls;
using FlyleafLib.MediaPlayer;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Text.Json;
using XREngine.Components;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;

namespace XREngine.Rendering.UI
{
    //public class UIVideoComponent2 : UIMaterialComponent, IHostPlayer
    //{
    //    /* TODO
    // *
    // * Attached: (UserControl) Host = Surface
    // * Detached: (Form) Surface
    // * (Form) Overlay
    // *
    // */

    //    #region Properties / Variables
    //    PlayerGL? _Player;
    //    public PlayerGL? Player
    //    {
    //        get => _Player;
    //        set
    //        {
    //            if (_Player == value)
    //                return;

    //            var oldPlayer = _Player;
    //            _Player = value;
    //            SetPlayer(oldPlayer);
    //            //Raise(nameof(Player));
    //        }
    //    }

    //    bool _IsFullScreen;
    //    public bool IsFullScreen
    //    {
    //        get => _IsFullScreen;
    //        set
    //        {
    //            if (_IsFullScreen == value)
    //                return;

    //            //if (value)
    //            //    FullScreen();
    //            //else
    //            //    NormalScreen();
    //        }
    //    }

    //    bool _ToggleFullScreenOnDoubleClick = true;
    //    public bool ToggleFullScreenOnDoubleClick
    //    { get => _ToggleFullScreenOnDoubleClick; set => SetField(ref _ToggleFullScreenOnDoubleClick, value); }

    //    public int UniqueId { get; private set; } = -1;

    //    bool _KeyBindings = true;
    //    public bool KeyBindings { get => _KeyBindings; set => SetField(ref _KeyBindings, value); }

    //    bool _PanMoveOnCtrl = true;
    //    public bool PanMoveOnCtrl { get => _PanMoveOnCtrl; set => SetField(ref _PanMoveOnCtrl, value); }

    //    bool _PanZoomOnCtrlWheel = true;
    //    public bool PanZoomOnCtrlWheel { get => _PanZoomOnCtrlWheel; set => SetField(ref _PanZoomOnCtrlWheel, value); }

    //    bool _PanRotateOnShiftWheel = true;
    //    public bool PanRotateOnShiftWheel { get => _PanRotateOnShiftWheel; set => SetField(ref _PanRotateOnShiftWheel, value); }

    //    bool _DragMove = true;
    //    public bool DragMove { get => _DragMove; set => SetField(ref _DragMove, value); }

    //    bool _SwapDragEnterOnShift = true;
    //    public bool SwapDragEnterOnShift { get => _SwapDragEnterOnShift; set => SetField(ref _SwapDragEnterOnShift, value); }


    //    int panPrevX, panPrevY;
    //    LogHandler Log;
    //    bool designMode = LicenseManager.UsageMode == LicenseUsageMode.Designtime;
    //    static int idGenerator;

    //    private class FlyleafHostDropWrap { public UIVideoComponent2? FlyleafHost; } // To allow non FlyleafHosts to drag & drop
    //    #endregion

    //    public UIVideoComponent2()
    //    {
    //        UniqueId = idGenerator++;

    //        if (designMode)
    //            return;

    //        Log = new LogHandler(("[#" + UniqueId + "]").PadRight(8, ' ') + $" [FlyleafHost NP] ");

    //        //KeyUp += Host_KeyUp;
    //        //KeyDown += Host_KeyDown;
    //        //DoubleClick += Host_DoubleClick;
    //        //MouseDown += Host_MouseDown;
    //        //MouseMove += Host_MouseMove;
    //        //MouseWheel += Host_MouseWheel;
    //        //DragEnter += Host_DragEnter;
    //        //DragDrop += Host_DragDrop;
    //    }

    //    //private void Host_DragDrop(object sender, DragEventArgs e)
    //    //{
    //    //    if (Player == null)
    //    //        return;

    //    //    FlyleafHostDropWrap hostWrap = (FlyleafHostDropWrap)e.Data.GetData(typeof(FlyleafHostDropWrap));

    //    //    if (hostWrap != null)
    //    //    {
    //    //        (hostWrap.FlyleafHost.Player, Player) = (Player, hostWrap.FlyleafHost.Player);
    //    //        return;
    //    //    }

    //    //    if (e.Data.GetDataPresent(DataFormats.FileDrop))
    //    //    {
    //    //        string filename = ((string[])e.Data.GetData(DataFormats.FileDrop, false))[0];
    //    //        Player.OpenAsync(filename);
    //    //    }
    //    //    else if (e.Data.GetDataPresent(DataFormats.Text))
    //    //    {
    //    //        string text = e.Data.GetData(DataFormats.Text, false).ToString();
    //    //        if (text.Length > 0)
    //    //            Player.OpenAsync(text);
    //    //    }
    //    //}
    //    //private void Host_DragEnter(object sender, DragEventArgs e) { if (Player != null) e.Effect = DragDropEffects.All; }
    //    //private void Host_MouseWheel(object sender, MouseEventArgs e)
    //    //{
    //    //    if (Player == null || e.Delta == 0)
    //    //        return;

    //    //    if (PanZoomOnCtrlWheel && ModifierKeys.HasFlag(Keys.Control))
    //    //    {
    //    //        System.Windows.Point curDpi = new(e.Location.X, e.Location.Y);
    //    //        if (e.Delta > 0)
    //    //            Player.ZoomIn(curDpi);
    //    //        else
    //    //            Player.ZoomOut(curDpi);
    //    //    }
    //    //    else if (PanRotateOnShiftWheel && ModifierKeys.HasFlag(Keys.Shift))
    //    //    {
    //    //        if (e.Delta > 0)
    //    //            Player.RotateRight();
    //    //        else
    //    //            Player.RotateLeft();
    //    //    }
    //    //}
    //    //private void Host_MouseMove(object sender, MouseEventArgs e)
    //    //{
    //    //    if (Player == null)
    //    //        return;

    //    //    if (e.Location != mouseMoveLastPoint)
    //    //    {
    //    //        Player.Activity.RefreshFullActive();
    //    //        mouseMoveLastPoint = e.Location;
    //    //    }

    //    //    if (e.Button != MouseButtons.Left)
    //    //        return;

    //    //    if (PanMoveOnCtrl && ModifierKeys.HasFlag(Keys.Control))
    //    //    {
    //    //        Player.PanXOffset = panPrevX + e.X - mouseLeftDownPoint.X;
    //    //        Player.PanYOffset = panPrevY + e.Y - mouseLeftDownPoint.Y;
    //    //    }
    //    //    else if (DragMove && Capture && ParentForm != null && !IsFullScreen)
    //    //    {
    //    //        ParentForm.Location = new Point(ParentForm.Location.X + e.X - mouseLeftDownPoint.X, ParentForm.Location.Y + e.Y - mouseLeftDownPoint.Y);
    //    //    }
    //    //}
    //    //private void Host_MouseDown(object sender, MouseEventArgs e)
    //    //{
    //    //    if (e.Button != MouseButtons.Left)
    //    //        return;

    //    //    mouseLeftDownPoint = new Point(e.X, e.Y);

    //    //    if (Player != null)
    //    //    {
    //    //        Player.Activity.RefreshFullActive();

    //    //        panPrevX = Player.PanXOffset;
    //    //        panPrevY = Player.PanYOffset;

    //    //        if (ModifierKeys.HasFlag(Keys.Shift))
    //    //        {
    //    //            DoDragDrop(new FlyleafHostDropWrap() { FlyleafHost = this }, DragDropEffects.Move);
    //    //        }
    //    //    }
    //    //}
    //    //private void Host_DoubleClick(object sender, EventArgs e) { if (!ToggleFullScreenOnDoubleClick) return; IsFullScreen = !IsFullScreen; }
    //    //private void Host_KeyDown(object sender, KeyEventArgs e) { if (KeyBindings) Player.KeyDown(Player, e); }
    //    //private void Host_KeyUp(object sender, KeyEventArgs e) { if (KeyBindings) Player.KeyUp(Player, e); }

    //    public void SetPlayer(Player? oldPlayer)
    //    {
    //        // De-assign old Player's Handle/FlyleafHost
    //        if (oldPlayer != null)
    //        {
    //            Log.Debug($"De-assign Player #{oldPlayer.PlayerId}");

    //            oldPlayer.VideoDecoder.DestroySwapChain();
    //            oldPlayer.Host = null;
    //        }

    //        if (Player == null)
    //            return;

    //        Log.Prefix = ("[#" + UniqueId + "]").PadRight(8, ' ') + $" [FlyleafHost #{Player.PlayerId}] ";

    //        // De-assign new Player's Handle/FlyleafHost
    //        Player.Host?.Player_Disposed();


    //        // Assign new Player's (Handle/FlyleafHost)
    //        Log.Debug($"Assign Player #{Player.PlayerId}");

    //        Player.Host = this;
    //        Player.VideoDecoder.CreateSwapChain(Handle);

    //        //BackColor = Utils.WPFToWinFormsColor(Player.Config.Video.BackgroundColor);
    //    }

    //    protected override void OnMaterialSettingUniforms(XRMaterialBase material, XRRenderProgram program)
    //    {
    //        base.OnMaterialSettingUniforms(material, program);
    //        Player?.WFPresent();
    //    }

    //    protected internal override void OnComponentActivated()
    //    {
    //        base.OnComponentActivated();
    //    }

    //    //public void FullScreen()
    //    //{
    //    //    if (ParentForm == null)
    //    //        return;

    //    //    oldStyle = ParentForm.FormBorderStyle;
    //    //    oldLocation = Location;
    //    //    oldSize = Size;
    //    //    oldParent = Parent;

    //    //    ParentForm.FormBorderStyle = FormBorderStyle.None;
    //    //    ParentForm.WindowState = FormWindowState.Maximized;
    //    //    Parent = ParentForm;
    //    //    Location = new Point(0, 0);
    //    //    Size = ParentForm.ClientSize;

    //    //    BringToFront();
    //    //    Focus();

    //    //    _IsFullScreen = true;
    //    //    Raise(nameof(IsFullScreen));
    //    //}
    //    //public void NormalScreen()
    //    //{
    //    //    if (ParentForm == null)
    //    //        return;

    //    //    ParentForm.FormBorderStyle = oldStyle;
    //    //    ParentForm.WindowState = FormWindowState.Normal;
    //    //    Parent = oldParent;

    //    //    Location = oldLocation;
    //    //    Size = oldSize;

    //    //    Focus();

    //    //    _IsFullScreen = false;
    //    //    Raise(nameof(IsFullScreen));
    //    //}

    //    public bool Player_CanHideCursor() => false;
    //    public bool Player_GetFullScreen() => IsFullScreen;
    //    public void Player_SetFullScreen(bool value) => IsFullScreen = value;
    //    public void Player_Disposed() => Player = null;

    //    //protected override bool IsInputKey(Keys keyData) => Player != null && Player.Host != null;  // Required to allow keybindings such as arrows etc.

    //    // TBR: Related to Renderer's WndProc
    //    //protected override void OnPaintBackground(PaintEventArgs pe)
    //    //{
    //    //    if (Player == null || (Player != null && !Player.WFPresent()))
    //    //        base.OnPaintBackground(pe);
    //    //}
    //    //protected override void OnPaint(PaintEventArgs pe) { }
    //}
    public class UIVideoComponent : UIMaterialComponent
    {
        static UIVideoComponent()
        {
            var current = AppContext.BaseDirectory;
            var probe = Path.Combine(current, "runtimes", "win-x64", "native");
            Console.WriteLine($"[FFmpeg] Probing native libs in: {probe}");
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;
            
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!path.Split(Path.PathSeparator).Contains(probe, StringComparer.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable("PATH", $"{probe}{Path.PathSeparator}{path}");

            ffmpeg.RootPath = probe;
        }

        //Optional AudioSourceComponent for audio streaming
        public AudioSourceComponent? AudioSource => GetSiblingComponent<AudioSourceComponent>();

        private readonly XRMaterialFrameBuffer _fbo;

        ///// <summary>
        ///// Loads a Twitch stream by username.
        ///// </summary>
        ///// <param name="username">The Twitch username</param>
        ///// <returns>Task representing the async operation</returns>
        //public async Task OpenTwitchStream(string username)
        //{
        //    try
        //    {
        //        string streamUrl = await GetTwitchStreamUrl(username);
        //        StreamUrl = streamUrl;

        //        // Restart streaming if already active
        //        if (IsActiveInHierarchy)
        //        {
        //            OnComponentDeactivated();
        //            OnComponentActivated();
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.LogWarning($"Failed to load Twitch stream for {username}: {ex.Message}");
        //    }
        //}

        /// <summary>
        /// Gets the stream URL for a Twitch channel by username.
        /// </summary>
        /// <param name="username">The Twitch username</param>
        /// <returns>The HLS stream URL</returns>
        private static async Task<string?> GetTwitchStreamUrl(string username)
        {
            // Method 1: Use GQL query to fetch access token
            using HttpClient client = new();

            // First, check if the channel is live
            client.DefaultRequestHeaders.Add("Client-ID", "kimne78kx3ncx6brgo4mv6wki5h1ko");

            var gqlPayload = new
            {
                operationName = "PlaybackAccessToken",
                variables = new
                {
                    isLive = true,
                    login = username,
                    isVod = false,
                    vodID = "",
                    playerType = "embed"
                },
                extensions = new
                {
                    persistedQuery = new
                    {
                        version = 1,
                        sha256Hash = "0828119ded1c13477966434e15800ff57ddacf13ba1911c129dc2200705b0712"
                    }
                }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(gqlPayload),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync("https://gql.twitch.tv/gql", content);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();

            //Parse response to extract token and signature
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            var tokenData = root
                .GetProperty("data")
                .GetProperty("streamPlaybackAccessToken");

            var token = tokenData.GetProperty("value").GetString();
            var sig = tokenData.GetProperty("signature").GetString();

            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(sig))
                throw new Exception("Failed to obtain stream token");

            //Construct the M3U8 URL
            string m3u8Url = $"https://usher.ttvnw.net/api/channel/hls/{username}.m3u8" +
                "?player=twitchweb" +
                $"&token={Uri.EscapeDataString(token)}" +
                $"&sig={sig}" +
                "&allow_source=true" +
                "&allow_audio_only=true" +
                "&fast_bread=true";

            m3u8Url = "https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8";

            return m3u8Url;

            //Get the M3U8 playlist
            string m3u8Response;
            try
            {
                m3u8Response = await client.GetStringAsync(m3u8Url);
            }
            catch (HttpRequestException ex)
            {
                Debug.LogWarning($"Failed to fetch M3U8 playlist: {ex.Message}");
                return null;
            }

            //Parse the M3U8 playlist
            var urls = ParseM3u8ForAllOptions(m3u8Response);
            if (urls.Length == 0)
            {
                Debug.LogWarning("No valid stream URLs found in the playlist");
                return null;
            }

            //Sort by bandwidth and select the highest quality stream
            var (title, w, h, fps, bandwidth, url) = urls.OrderByDescending(x => x.bandwidth).FirstOrDefault();
            string streamUrl = url;
            if (string.IsNullOrEmpty(streamUrl))
            {
                Debug.LogWarning("No valid stream URL found in the playlist");
                return null;
            }

            return streamUrl;
        }

        private static (string name, int w, int h, float fps, int bandwidth, string url)[] ParseM3u8ForAllOptions(string m3u8Content)
        {
            const string streamInfTag = "#EXT-X-STREAM-INF:";
            const string mediaTag = "#EXT-X-MEDIA:";

            //#EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="720p60",NAME="720p60",AUTOSELECT=YES,DEFAULT=YES
            //#EXT-X-STREAM-INF:BANDWIDTH=3422999,RESOLUTION=1280x720,CODECS="avc1.4D401F,mp4a.40.2",VIDEO="720p60",FRAME-RATE=60.000

            string[] lines = m3u8Content.Split('\n');
            List<(string name, int w, int h, float fps, int bandwidth, string url)> options = [];

            string name = string.Empty;
            int w = 0, h = 0, bandwidth = 0;
            float fps = 0.0f;

            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith(mediaTag))
                {
                    string[] attributes = lines[i].Split([',', ':'], StringSplitOptions.RemoveEmptyEntries);
                    foreach (var attr in attributes)
                    {
                        if (attr.StartsWith("NAME="))
                            name = attr[(attr.IndexOf('=') + 1)..];
                    }
                }
                if (lines[i].StartsWith(streamInfTag))
                {
                    string[] attributes = lines[i].Split([',', ':'], StringSplitOptions.RemoveEmptyEntries);
                    foreach (var attr in attributes)
                    {
                        if (attr.StartsWith("RESOLUTION="))
                        {
                            string[] res = attr[(attr.IndexOf('=') + 1)..].Split('x');
                            w = int.Parse(res[0]);
                            h = int.Parse(res[1]);
                        }
                        else if (attr.StartsWith("FRAME-RATE="))
                            fps = float.Parse(attr[(attr.IndexOf('=') + 1)..]);
                        else if (attr.StartsWith("BANDWIDTH="))
                            bandwidth = int.Parse(attr[(attr.IndexOf('=') + 1)..]);
                    }
                    if (i + 1 < lines.Length && !lines[i + 1].StartsWith('#'))
                    {
                        string url = lines[i + 1].Trim();
                        options.Add((name, w, h, fps, bandwidth, url));

                        name = string.Empty;
                        w = 0;
                        h = 0;
                        fps = 0.0f;
                        bandwidth = 0;
                    }
                }
            }
            return [.. options];
        }

        private string? _streamUrl = "http://pendelcam.kip.uni-heidelberg.de/mjpg/video.mjpg";
        public string? StreamUrl
        {
            get => _streamUrl;
            set => SetField(ref _streamUrl, value);
        }

        private unsafe AVFormatContext* _formatContext;
        private unsafe AVCodecContext* _videoCodecContext;
        private unsafe AVCodecContext* _audioCodecContext;
        // FFmpeg context with hardware acceleration
        private unsafe AVBufferRef* _hwDeviceContext;
        private AVPixelFormat _hwPixelFormat;

        private int _videoStreamIndex = -1;
        private int _audioStreamIndex = -1;

        private int _currentPboIndex = 0;
        private readonly XRDataBuffer?[] _pboBuffers = [null, null];

        private const int MAX_QUEUE_SIZE = 3; // Limit frame queue to prevent memory buildup

        // Frame queue with size limit
        private readonly BlockingCollection<byte[]> _frameQueue = new(MAX_QUEUE_SIZE);
        private byte[]? _currentFrame;
        private readonly Lock _frameLock = new();

        private unsafe AVFrame* _frame;
        private unsafe AVPacket* _packet;
        private unsafe SwsContext* _swsContext;

        private const AVPixelFormat _videoTextureFormat = AVPixelFormat.AV_PIX_FMT_RGB24;

        public UIVideoComponent() : base(GetVideoMaterial(), true)
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

        //protected internal override void OnComponentActivated()
        //{
        //    base.OnComponentActivated();
        //    OpenTwitchStream("sodapoppin").ContinueWith(t =>
        //    {
        //        if (t.Exception != null)
        //            Debug.LogWarning($"Failed to load Twitch stream for sodapoppin: {t.Exception.Message}");

        //        AllocateDecode();
        //        RegisterTick(Components.ETickGroup.Normal, Components.ETickOrder.Logic, DecodeFrame);
        //        Engine.Time.Timer.RenderFrame += ConsumeFrameQueue;
        //    });
        //}
        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            StopDecoding();
        }

        private void StopDecoding()
        {
            UnregisterTick(Components.ETickGroup.Normal, Components.ETickOrder.Logic, DecodeFrame);
            Engine.Time.Timer.RenderFrame -= ConsumeFrameQueue;
            CleanupDecode();
            _frameQueue.CompleteAdding();
        }

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

            if (Engine.InvokeOnMainThread(() => UploadFrameToTexturePBO(frame)))
                return;

            _currentPboIndex = (_currentPboIndex + 1) % _pboBuffers.Length;
            XRDataBuffer? pbo = _pboBuffers[_currentPboIndex];

            //Pre-allocate the texture's data buffer with the frame's data length
            if (pbo is null || pbo.Length != (uint)frame.Length)
            {
                //pbo.Resize((uint)frame.Length, false);
                pbo?.Destroy();

                _pboBuffers[_currentPboIndex] = pbo = new XRDataBuffer("", EBufferTarget.PixelUnpackBuffer, (uint)frame.Length / 3, EComponentType.Byte, 3, false, false)
                {
                    Resizable = false,
                    Usage = EBufferUsage.StreamDraw,
                    // Avoid ClientStorage due to driver instability; keep persistent/coherent mapping if required
                    RangeFlags = EBufferMapRangeFlags.Write | EBufferMapRangeFlags.Persistent | EBufferMapRangeFlags.Coherent,
                    StorageFlags = EBufferMapStorageFlags.Write | EBufferMapStorageFlags.Coherent | EBufferMapStorageFlags.Persistent,
                };
            }

            pbo.Generate();
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
                if (ptr is null || !ptr.Value.IsValid)
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

        private unsafe void DecodeFrame()
        {
            int result = ffmpeg.av_read_frame(_formatContext, _packet);
            if (result < 0)
            {
                // Check for specific errors
                if (result == ffmpeg.AVERROR_EOF)
                {
                    Debug.Out("End of stream reached");
                    return;
                }

                // Get detailed error message
                byte[] errorBuffer = new byte[1024];
                fixed (byte* ptr = errorBuffer)
                {
                    ffmpeg.av_strerror(result, ptr, (ulong)errorBuffer.Length);
                    string errorMsg = new string((sbyte*)ptr).Trim('\0');
                    Debug.LogWarning($"av_read_frame error: {errorMsg} (code: {result})");
                }

                // For HLS streams, we might need to reopen the stream if we get certain errors
                if (StreamUrl?.EndsWith(".m3u8") == true)
                {
                    // Wait a moment before retrying
                    Thread.Sleep(100);
                }

                return;
            }

            if (_packet->stream_index == _videoStreamIndex)
                DecodeVideo();
            else if (_packet->stream_index == _audioStreamIndex)
                DecodeAudio();

            ffmpeg.av_packet_unref(_packet);
        }

        private unsafe void DecodeAudio()
        {
            if (ffmpeg.avcodec_send_packet(_audioCodecContext, _packet) < 0)
                return;
            
            while (ffmpeg.avcodec_receive_frame(_audioCodecContext, _frame) >= 0)
                ProcessAudioFrame();
        }

        private unsafe void DecodeVideo()
        {
            if (ffmpeg.avcodec_send_packet(_videoCodecContext, _packet) < 0)
                return;
            
            while (ffmpeg.avcodec_receive_frame(_videoCodecContext, _frame) >= 0)
                ProcessVideoFrame();
        }

        private unsafe bool AllocateDecode()
        {
            string? streamUrl = _streamUrl;
            if (string.IsNullOrEmpty(streamUrl))
            {
                //Debug.LogWarning("Stream URL is null or empty");
                return false;
            }

            //using (var httpClient = new HttpClient())
            //{
            //    try
            //    {
            //        var response = await httpClient.GetAsync(streamUrl);
            //        if (!response.IsSuccessStatusCode)
            //        {
            //            Debug.LogWarning($"Stream URL returned HTTP {response.StatusCode}");
            //            return false;
            //        }
            //        var content = await response.Content.ReadAsStringAsync();
            //        if (!content.Contains("#EXTM3U"))
            //        {
            //            Debug.LogWarning("Response doesn't appear to be a valid M3U8 file");
            //            return false;
            //        }
            //    }
            //    catch (Exception ex)
            //    {
            //        Debug.LogWarning($"Error checking stream URL: {ex.Message}");
            //        return false;
            //    }
            //}

            // Initialize networking for FFmpeg
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_DEBUG);
            ffmpeg.avdevice_register_all();
            ffmpeg.avformat_network_init();

            // Allocate format context
            _formatContext = ffmpeg.avformat_alloc_context();

            // Set interrupt callback for timeout handling
            //AVIOInterruptCB fp = new()
            //{
            //    callback = new AVIOInterruptCB_callback_func
            //    {
            //        Pointer = (nint)(delegate* unmanaged[Cdecl]<void*, int>)&ReadTimeoutCallback
            //    },
            //    opaque = null
            //};
            //_formatContext->interrupt_callback = fp;

            // Set options for low latency streaming
            AVDictionary* options = stackalloc AVDictionary[1];
            string timeout = "5000000"; // 5 sec timeout

            bool hls = streamUrl.EndsWith(".m3u8");

            if (streamUrl.Contains("ttvnw.net") || streamUrl.Contains("twitch.tv"))
            {
                ffmpeg.av_dict_set(&options, "user_agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)", 0);
                ffmpeg.av_dict_set(&options, "referer", "https://player.twitch.tv", 0);
                ffmpeg.av_dict_set(&options, "headers",
                    "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64)\r\n" +
                    "Accept: */*\r\n" +
                    "Origin: https://player.twitch.tv\r\n" +
                    "Referer: https://player.twitch.tv\r\n"
                    , 0);
            }

            // For HLS/M3U8 streams
            if (hls)
            {
                //ffmpeg.av_dict_set(&options, "hls_init_time", "0", 0);
                //ffmpeg.av_dict_set(&options, "hls_seek_time", "0", 0);
                //ffmpeg.av_dict_set(&options, "live_start_index", "0", 0);
                //ffmpeg.av_dict_set(&options, "hls_flags", "single_file+append_list+omit_endlist", 0);
                //ffmpeg.av_dict_set(&options, "hls_playlist_reload_attempts", "10", 0);
                //ffmpeg.av_dict_set(&options, "hls_segment_type", "mpegts", 0);

                //ffmpeg.av_dict_set(&options, "allowed_extensions", "m3u8,ts", 0);
                ffmpeg.av_dict_set(&options, "protocol_whitelist", "file,http,https,tcp,tls", 0);
                //ffmpeg.av_dict_set(&options, "seg_max_retry", "3", 0);

                //ffmpeg.av_dict_set(&options, "reconnect", "1", 0);
                //ffmpeg.av_dict_set(&options, "reconnect_streamed", "1", 0);
                //ffmpeg.av_dict_set(&options, "reconnect_delay_max", "5", 0);
            }

            //ffmpeg.av_dict_set(&options, "rtsp_transport", "tcp", 0);
            //ffmpeg.av_dict_set(&options, "stimeout", timeout, 0);
            //ffmpeg.av_dict_set(&options, "flags", "low_delay", 0);
            //ffmpeg.av_dict_set(&options, "timeout", timeout, 0);
            //ffmpeg.av_dict_set(&options, "rw_timeout", timeout, 0);
            ffmpeg.av_dict_set(&options, "fflags", "discardcorrupt", 0);

            // Open input stream
            AVFormatContext* pFormatContext = _formatContext;
            int result;
            if (hls)
            {
                var inputFormat = ffmpeg.av_find_input_format("hls");
                if (inputFormat != null)
                    result = ffmpeg.avformat_open_input(&pFormatContext, streamUrl, inputFormat, &options);
                else
                    result = ffmpeg.avformat_open_input(&pFormatContext, streamUrl, null, &options);
            }
            else
                result = ffmpeg.avformat_open_input(&pFormatContext, streamUrl, null, &options);
            if (result != 0)
            {
                byte[] errorBuffer = new byte[1024];
                string errorMsg;
                fixed (byte* ptr = errorBuffer)
                {
                    ffmpeg.av_strerror(result, ptr, (ulong)errorBuffer.Length);
                    errorMsg = new string((sbyte*)ptr).Trim('\0');
                }
                Debug.LogWarning($"Failed to open input stream: {errorMsg} (Code: {result})");
                return false;
            }

            // Retrieve stream information
            if (ffmpeg.avformat_find_stream_info(_formatContext, null) < 0)
            {
                Debug.LogWarning("Failed to find stream info");
                return false;
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
                return false;
            }

            OpenVideoContext();
            OpenAudioContext();

            ffmpeg.av_dict_free(&options);

            _frame = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();
            _swsContext = (SwsContext*)null;

            return true;
        }

        // Timeout callback function for FFmpeg
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static unsafe int ReadTimeoutCallback(void* opaque)
        {
            // Return 1 to abort I/O operation, 0 to continue
            return 0; // Default: continue execution
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();

            //Task.Run(async () =>
            //{
            //try
            //{
            GetTwitchStreamURL("saintsakura").ContinueWith(t => StartDecode());
                    //StartDecode();
                //}
                //catch (Exception ex)
                //{
                //    Debug.LogWarning($"Failed to initialize video stream: {ex.Message}");
                //}
            //});
        }

        private void StartDecode()
        {
            if (!AllocateDecode())
                return;
            
            RegisterTick(Components.ETickGroup.Normal, Components.ETickOrder.Logic, DecodeFrame);
            Engine.Time.Timer.RenderFrame += ConsumeFrameQueue;
        }

        public async Task GetTwitchStreamURL(string username)
        {
            //try
            //{
                //Debug.Out($"Opening Twitch stream for {username}...");
                string? streamUrl = await GetTwitchStreamUrl(username);
                //Debug.Out($"Got Twitch stream URL: {streamUrl}");
                StreamUrl = streamUrl;
            //}
            //catch (Exception ex)
            //{
            //    Debug.LogWarning($"Failed to load Twitch stream for {username}: {ex.Message}");
            //    throw; // Re-throw to handle in the caller
            //}
        }

        private unsafe void CleanupDecode()
        {
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

            if (_swsContext != null)
                ffmpeg.sws_freeContext(_swsContext);

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

            lock (_frameLock)
            {
                _currentFrame = null;
            }

            while (_frameQueue.TryTake(out var _)) { }

            ffmpeg.avformat_network_deinit();
        }

        private unsafe void OpenAudioContext()
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

        private unsafe void OpenVideoContext()
        {
            if (_videoStreamIndex < 0)
                return;

            // Get codec parameters and find decoder
            var codecParameters = _formatContext->streams[_videoStreamIndex]->codecpar;
            var codec = ffmpeg.avcodec_find_decoder(codecParameters->codec_id);
            if (codec == null)
            {
                Debug.LogWarning($"Unsupported codec ID: {codecParameters->codec_id}");
                return;
            }

            // Allocate codec context
            _videoCodecContext = ffmpeg.avcodec_alloc_context3(codec);
            ffmpeg.avcodec_parameters_to_context(_videoCodecContext, codecParameters);

            // Set threading options to improve performance
            _videoCodecContext->thread_count = 4;
            _videoCodecContext->thread_type = ffmpeg.FF_THREAD_FRAME;

            // Try to initialize hardware acceleration (CUDA first, then others)
            bool hwAccelInitialized = false;

            // Try various hardware acceleration methods
            AVHWDeviceType[] hwTypes =
            [
                AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA,
                AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA,
                AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2,
                AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI,
                AVHWDeviceType.AV_HWDEVICE_TYPE_VDPAU
            ];

            foreach (var hwType in hwTypes)
            {
                if (InitHardwareAcceleration(_videoCodecContext, hwType))
                {
                    hwAccelInitialized = true;
                    break;
                }
            }

            if (!hwAccelInitialized)
                Debug.Out("Hardware acceleration not available, using software decoding");

            // Open codec with error handling
            int result = ffmpeg.avcodec_open2(_videoCodecContext, codec, null);
            if (result < 0)
            {
                byte[] errorBuffer = new byte[1024];
                string errorMsg = string.Empty;
                fixed (byte* ptr = errorBuffer)
                {
                    ffmpeg.av_strerror(result, ptr, (ulong)errorBuffer.Length);
                    errorMsg = new string((char*)ptr).Trim('\0');
                }
                Debug.LogWarning($"Could not open codec: {errorMsg}");
                return;
            }
        }

        private unsafe void ProcessAudioFrame()
        {
            var audioSource = AudioSource;
            if (audioSource is null)
                return;

            short[] samples = new short[_frame->nb_samples * _audioCodecContext->ch_layout.nb_channels];

            Marshal.Copy((IntPtr)_frame->data[0], samples, 0, samples.Length);

            int freq = _audioCodecContext->sample_rate;
            bool stereo = _audioCodecContext->ch_layout.nb_channels == 2;

            audioSource.EnqueueStreamingBuffers(freq, stereo, samples);
        }

        private IVector2? _widthHeight;
        public IVector2? WidthHeight
        {
            get => _widthHeight;
            set
            {
                if (_widthHeight == value)
                    return;

                _widthHeight = value;
                if (value is null)
                    return;

                VideoTexture?.Resize((uint)value.Value.X, (uint)value.Value.Y);
                _fbo.Resize((uint)value.Value.X, (uint)value.Value.Y);
            }
        }

        private unsafe void ProcessVideoFrame()
        {
            // Handle hardware frame if needed
            var decodedFrame = GetHwFrame(_frame);

            WidthHeight = new IVector2(decodedFrame->width, decodedFrame->height);

            // Convert frame to RGB24 if needed
            if (decodedFrame->format != (int)_videoTextureFormat)
            {
                if (_swsContext == null)
                {
                    _swsContext = ffmpeg.sws_getContext(
                        decodedFrame->width,
                        decodedFrame->height,
                        (AVPixelFormat)decodedFrame->format,
                        decodedFrame->width,
                        decodedFrame->height,
                        _videoTextureFormat,
                        ffmpeg.SWS_BILINEAR,
                        null,
                        null,
                        null);
                }

                var convertedFrame = ffmpeg.av_frame_alloc();
                convertedFrame->format = (int)_videoTextureFormat;
                convertedFrame->width = decodedFrame->width;
                convertedFrame->height = decodedFrame->height;
                ffmpeg.av_frame_get_buffer(convertedFrame, 0);

                ffmpeg.sws_scale(
                    _swsContext,
                    decodedFrame->data,
                    decodedFrame->linesize,
                    0,
                    decodedFrame->height,
                    convertedFrame->data,
                    convertedFrame->linesize);

                if (decodedFrame != _frame)
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

            if (decodedFrame != _frame)
                ffmpeg.av_frame_free(&decodedFrame);
        }
    }
}
