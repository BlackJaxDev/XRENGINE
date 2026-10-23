using System.Threading;
using XREngine.Data.Rendering;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

public partial class UIVideoComponent
{
    private const int MaximumVideoSurfaces = 4;
    private readonly List<VideoSurface> _videoSurfaces = [];
    private VideoSurface? _publishedVideoSurface;
    private ulong _nextVideoSurfaceGeneration;
    private string? _videoProducerFailure;
    private readonly Action _invalidateVideoVisual;
    private int _videoVisualInvalidationQueued;
    private int _videoProducerDestroyed;
    private readonly object _videoProducerGate = new();
    private int _videoProducerActive;
    private bool _videoReleasePending;
    private bool _videoSurfacesReleased;
    private bool _videoStreamStopPending;
    internal bool VideoSurfacesReleased => _videoSurfacesReleased;

    private sealed class VideoSurface(XRTexture2D texture) : IUIDrawProducerLease
    {
        private int _references = 1;
        private long _generation;
        public XRTexture2D Texture { get; } = texture;
        public ulong Generation => unchecked((ulong)Volatile.Read(ref _generation));
        public bool IsPoolOnly => Volatile.Read(ref _references) == 1;
        public void Publish(ulong generation) => Volatile.Write(ref _generation, checked((long)generation));
        public void Retain()
        {
            int current;
            do
            {
                current = Volatile.Read(ref _references);
                ObjectDisposedException.ThrowIf(current == 0, this);
            } while (Interlocked.CompareExchange(ref _references, checked(current + 1), current) != current);
        }
        public void Release()
        {
            int remaining = Interlocked.Decrement(ref _references);
            if (remaining < 0)
                throw new InvalidOperationException("Video surface was released twice.");
            if (remaining == 0)
            {
                if (RuntimeEngine.IsRenderThread || RuntimeEngine.RenderThreadId == 0)
                    DestroyResources();
                else
                    RuntimeEngine.EnqueueRenderThreadTask(DestroyResources, "UI.Video.DestroySurface",
                        RenderThreadJobKind.RenderPipelineResource);
            }
        }

        private void DestroyResources()
        {
            foreach (Mipmap2D mip in Texture.Mipmaps)
                mip.Data?.Dispose();
            Texture.Destroy();
        }
    }

    private void ProduceVideoFrame()
    {
        bool stopPending = false;
        lock (_videoProducerGate)
        {
            if (Volatile.Read(ref _videoProducerDestroyed) != 0 || !IsActive || _streamingSession is not { } session)
                return;
            _videoProducerActive++;
            try { DrainStreamingFramesOnMainThread(session); }
            finally
            {
                _videoProducerActive--;
                if (_videoProducerActive == 0 && _videoStreamStopPending)
                {
                    _videoStreamStopPending = false;
                    stopPending = true;
                }
                if (_videoProducerActive == 0 && _videoReleasePending)
                    ReleaseVideoSurfacesUnderGate();
            }
        }
        if (stopPending)
            StopStreamingPipeline();
    }

    private void QueueVideoVisualInvalidation()
    {
        if (Interlocked.Exchange(ref _videoVisualInvalidationQueued, 1) == 0)
            RuntimeEngine.EnqueueAppThreadTask(_invalidateVideoVisual, "UI.Video.FramePublished");
    }

    private void InvalidateVideoVisual()
    {
        Interlocked.Exchange(ref _videoVisualInvalidationQueued, 0);
        if (Volatile.Read(ref _videoProducerDestroyed) != 0)
            return;
        if (SceneNode is not null && !SceneNode.IsTransformNull && Transform is UITransform transform)
            transform.RecordUIChange(UIChangeDomain.Visual);
    }

    private VideoSurface? AcquireVideoUploadSurface(uint width, uint height)
    {
        VideoSurface? published = Volatile.Read(ref _publishedVideoSurface);
        foreach (VideoSurface surface in _videoSurfaces)
        {
            if (ReferenceEquals(surface, published) || !surface.IsPoolOnly)
                continue;
            if (surface.Texture.Width != width || surface.Texture.Height != height)
                surface.Texture.Resize(width, height);
            return surface;
        }
        if (_videoSurfaces.Count >= MaximumVideoSurfaces)
            return null;
        XRTexture2D texture = CreateVideoTexture(width, height);
        VideoSurface created = new(texture);
        _videoSurfaces.Add(created);
        return created;
    }

    private static XRTexture2D CreateVideoTexture(uint width, uint height, bool initializeBlack = false)
        => new(width, height, EPixelInternalFormat.Rgb8, EPixelFormat.Rgb, EPixelType.UnsignedByte, initializeBlack)
        {
            Resizable = true,
            MagFilter = ETexMagFilter.Linear,
            MinFilter = ETexMinFilter.Linear,
            UWrap = ETexWrapMode.ClampToEdge,
            VWrap = ETexWrapMode.ClampToEdge,
        };

    protected override bool TryCapturePublicationPayload(in UILayoutNodeOutput output,
        UIDrawPayloadBuilder builder, out UIDrawPayload? payload, out string? failure)
    {
        payload = null;
        if (Volatile.Read(ref _videoProducerDestroyed) != 0)
        {
            failure = "UI.Video.ProducerStopped: the video producer is closing.";
            return false;
        }
        VideoSurface? surface = Volatile.Read(ref _publishedVideoSurface);
        if (surface is null || surface.Generation == 0)
        {
            failure = _videoProducerFailure ?? "UI.Video.ProducerPending: no uploaded video frame is ready.";
            return false;
        }
        surface.Retain();
        try
        {
            if (!ReferenceEquals(surface, Volatile.Read(ref _publishedVideoSurface)))
            {
                failure = "UI.Video.ProducerChanging: the video frame changed during owner capture.";
                return false;
            }
            builder.ProducerLease = surface;
            builder.ProducerTextureSlot = 0;
            return builder.TryCapture(RenderCommand2D, false, out payload, out failure);
        }
        finally
        {
            surface.Release();
        }
    }

    protected override void OnDestroying()
    {
        Volatile.Write(ref _videoProducerDestroyed, 1);
        RuntimeEngine.Time.Timer.RenderFrame -= ProduceVideoFrame;
        bool stopNow;
        lock (_videoProducerGate)
        {
            if (_videoProducerActive != 0)
                _videoStreamStopPending = true;
            stopNow = _videoProducerActive == 0;
        }
        Exception? stopFailure = null;
        if (stopNow)
        {
            try { StopStreamingPipeline(); }
            catch (Exception ex) { stopFailure = ex; }
        }
        Volatile.Write(ref _publishedVideoSurface, null);
        try { base.OnDestroying(); }
        finally
        {
            lock (_videoProducerGate)
            {
                if (_videoProducerActive != 0)
                    _videoReleasePending = true;
                else if (RuntimeEngine.IsRenderThread || RuntimeEngine.RenderThreadId == 0)
                    ReleaseVideoSurfacesUnderGate();
                else
                    RuntimeEngine.EnqueueRenderThreadTask(ReleaseVideoSurfaces, "UI.Video.ReleaseSurfaces",
                        RenderThreadJobKind.RenderPipelineResource);
            }
        }
        if (stopFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stopFailure).Throw();
    }

    private void ReleaseVideoSurfaces()
    {
        lock (_videoProducerGate)
            ReleaseVideoSurfacesUnderGate();
    }

    private void ReleaseVideoSurfacesUnderGate()
    {
        if (_videoSurfacesReleased)
            return;
        if (_videoProducerActive != 0)
        {
            _videoReleasePending = true;
            return;
        }
        _videoSurfacesReleased = true;
        _videoReleasePending = false;
        Volatile.Write(ref _publishedVideoSurface, null);
        foreach (VideoSurface surface in _videoSurfaces)
            surface.Release();
        _videoSurfaces.Clear();
    }
}
