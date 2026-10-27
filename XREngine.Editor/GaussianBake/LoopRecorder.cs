using System.Threading.Channels;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Gaussian.Baking;
using XREngine.Rendering.Models.Gaussian.Baking.Imaging;
using XREngine.Rendering.Models.Gaussian.Baking.Video;
using XREngine.Rendering.Models.Gaussian.Baking.Views;

namespace XREngine.Editor.GaussianBake;

/// <summary>
/// Records the preview loop of a <see cref="GaussianCacheComponent"/>. A render-thread capture renders one pose for each
/// video frame, with the component time at <c>i / fps</c>. This class converts each frame to 8-bit sRGB with
/// <see cref="BakeTonemapper"/> and writes it to one ffmpeg session for each output of <see cref="LoopRendition.All"/>.
/// </summary>
/// <remarks>
/// The capture gives the frames through a queue of <see cref="QueueCapacity"/> frames and renders a new frame only when
/// the queue has space, so at most a few float frames are in memory. Each float frame is released after the conversion.
/// The videos have no alpha channel, so the frames are made opaque. The posters thus show the same image as frame 0 of
/// the videos.
/// </remarks>
internal sealed class LoopRecorder : IBakeViewSink
{
    /// <summary>The width of the rendered frames, in pixels.</summary>
    public const int FrameWidth = 1920;

    /// <summary>The height of the rendered frames, in pixels.</summary>
    public const int FrameHeight = 1080;

    /// <summary>The number of rendered frames that can wait for conversion.</summary>
    private const int QueueCapacity = 2;

    /// <summary>The longest time to wait for the render thread to release the capture after the recording stops.</summary>
    private static readonly TimeSpan ReleaseWait = TimeSpan.FromSeconds(30);

    private readonly GaussianCacheComponent _component;
    private readonly IReadOnlyList<BakeCameraPose> _poses;
    private readonly float _fps;
    private readonly Channel<float[]> _frames = Channel.CreateBounded<float[]>(new BoundedChannelOptions(QueueCapacity)
    {
        SingleReader = true,
        SingleWriter = true,
    });

    /// <summary>Creates a recorder.</summary>
    /// <param name="component">The component that plays the cache. Its <c>Playing</c> value must be false.</param>
    /// <param name="poses">One camera pose for each video frame.</param>
    /// <param name="fps">The video frame rate.</param>
    public LoopRecorder(GaussianCacheComponent component, IReadOnlyList<BakeCameraPose> poses, float fps)
    {
        _component = component;
        _poses = poses;
        _fps = fps;
    }

    /// <summary>Gets the number of frames that went to the video sessions.</summary>
    public int FramesWritten { get; private set; }

    bool IBakeViewSink.IsContentReady => _component.IsGpuDataReady;

    bool IBakeViewSink.CanAccept => _frames.Reader.Count < QueueCapacity;

    void IBakeViewSink.PrepareView(int viewIndex)
        => _component.PlaybackTimeSeconds = (float)(viewIndex / (double)_fps);

    void IBakeViewSink.Accept(int viewIndex, float[] rgba)
    {
        if (!_frames.Writer.TryWrite(rgba))
            throw new InvalidOperationException($"The loop frame queue has no space for frame {viewIndex}.");
    }

    /// <summary>
    /// Renders every frame, writes the four videos and the two posters into <paramref name="outputDirectory"/>, and
    /// waits until ffmpeg has written all files.
    /// </summary>
    /// <param name="world">The render world that holds the component.</param>
    /// <param name="bounds">The world bounds of the splats. The clip planes fit them.</param>
    /// <param name="encoder">The ffmpeg encoder.</param>
    /// <param name="outputDirectory">The output folder. It must exist.</param>
    /// <param name="cancellationToken">Stops the capture and the ffmpeg processes.</param>
    public async Task RecordAsync(
        IRuntimeRenderWorld world,
        AABB bounds,
        FfmpegEncoder encoder,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        RenderInfo3D[] targets = CollectTargets(_component);
        List<FfmpegSession> videos = new(4);
        List<FfmpegSession> posters = new(2);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        BakeViewCapture? capture = null;
        try
        {
            foreach (LoopRendition rendition in LoopRendition.All)
            {
                string path = Path.Combine(outputDirectory, rendition.FileName);
                FfmpegSession session = await encoder.StartAsync(rendition, FrameWidth, FrameHeight, _fps, path, stop.Token).ConfigureAwait(false);
                (rendition.IsPoster ? posters : videos).Add(session);
            }

            capture = new BakeViewCapture(world, targets, bounds, _poses, FrameWidth, FrameHeight, stop.Token, this);

            // The queue closes when the capture ends, so a reader that waits for a frame sees a failed capture.
            _ = capture.Completion.ContinueWith(
                static (_, writer) => ((ChannelWriter<float[]>)writer!).TryComplete(),
                _frames.Writer,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            RuntimeEngine.AddRenderThreadCoroutine(capture.Step, "GaussianBake.RecordLoop", RenderThreadJobKind.RequiresGraphicsContext);

            byte[] rgba8 = new byte[FrameWidth * FrameHeight * 4];
            for (int i = 0; i < _poses.Count; i++)
            {
                // The float frame is released at the end of this iteration.
                float[] linear = await NextFrameAsync(capture, i, stop.Token).ConfigureAwait(false);
                BakeTonemapper.ToSrgb8(linear, rgba8);
                MakeOpaque(rgba8);

                foreach (FfmpegSession video in videos)
                    await video.WriteFrameAsync(rgba8).ConfigureAwait(false);
                FramesWritten = i + 1;

                if (i == 0)
                {
                    foreach (FfmpegSession poster in posters)
                    {
                        await poster.WriteFrameAsync(rgba8).ConfigureAwait(false);
                        await poster.CompleteAsync().ConfigureAwait(false);
                    }
                }
            }

            await capture.Completion.ConfigureAwait(false);
            foreach (FfmpegSession video in videos)
                await video.CompleteAsync().ConfigureAwait(false);
        }
        finally
        {
            // This stops a capture that is still running and every ffmpeg process that is not complete.
            await stop.CancelAsync().ConfigureAwait(false);
            if (capture is not null)
                await WaitForReleaseAsync(capture).ConfigureAwait(false);
            foreach (FfmpegSession session in videos)
                await session.DisposeAsync().ConfigureAwait(false);
            foreach (FfmpegSession session in posters)
                await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<float[]> NextFrameAsync(BakeViewCapture capture, int frameIndex, CancellationToken cancellationToken)
    {
        ChannelReader<float[]> reader = _frames.Reader;
        while (true)
        {
            if (reader.TryRead(out float[]? frame))
                return frame;

            if (!await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // The capture ended before this frame. Its task holds the failure.
                await capture.Completion.ConfigureAwait(false);
                throw new GaussianBakeException(GaussianBakeFailureCodes.CaptureFailed, $"The loop capture ended before frame {frameIndex}.");
            }
        }
    }

    /// <summary>Returns the render infos of the component. The loop draws only the cache.</summary>
    private static RenderInfo3D[] CollectTargets(GaussianCacheComponent component)
    {
        List<RenderInfo3D> targets = [];
        foreach (RenderInfo info in component.RenderedObjects)
            if (info is RenderInfo3D info3D)
                targets.Add(info3D);
        return [.. targets];
    }

    private static void MakeOpaque(byte[] rgba8)
    {
        for (int i = 3; i < rgba8.Length; i += 4)
            rgba8[i] = byte.MaxValue;
    }

    /// <summary>
    /// Waits until the capture ends, so that the render thread released its viewport and textures before the caller
    /// removes the component. The result of the capture is not needed here: the recording already used or reported it.
    /// </summary>
    private static async Task WaitForReleaseAsync(BakeViewCapture capture)
    {
        try
        {
            await capture.Completion.WaitAsync(ReleaseWait).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A canceled, failed, or late capture changes nothing here.
        }
    }
}
