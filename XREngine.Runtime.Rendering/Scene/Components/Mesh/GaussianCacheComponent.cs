using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Gaussian.Cache;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.Components.Scene.Mesh;

/// <summary>
/// Plays an animated gaussian cache (<see cref="GaussianCache"/>) with the shared gaussian splat renderer.
/// </summary>
/// <remarks>
/// <para>
/// The component uploads the decoded data of the cache to the GPU: the splat records, the tree links, the driver
/// triangles, and the driver frames. When the cache accepts a chunk, the component uploads only the new ranges.
/// </para>
/// <para>
/// Each frame, in the PreRender pass, two compute passes run before the sorted draw of
/// <see cref="XREngine.Rendering.Gaussian.GaussianSplatRenderer"/>:
/// the driver pose pass blends the decoded driver frames to the current time, and the cut and splat pose pass chooses
/// the LOD nodes and writes the pose of each chosen splat into the splat buffer. The TransparentForward pass draws the
/// splats. <see cref="GaussianCachePoseReference"/> is the CPU mirror of both passes.
/// </para>
/// <para>
/// The renderer holds at most <see cref="MaxRenderedSplats"/> splats. The splats that do not fit are dropped, and the
/// component logs this one time.
/// </para>
/// <para>
/// When <see cref="MaxRenderedSplats"/> is at least the node count of the cache (the default), each node writes the
/// renderer slot at its global node index, and a node that is not chosen writes an empty slot. Splats with equal depth
/// then draw in node order, so the same cache, time, and camera give the same image in each run. With a smaller
/// <see cref="MaxRenderedSplats"/>, the slots come in atomic order, and the draw order of splats with equal depth can
/// change from run to run.
/// </para>
/// </remarks>
[Serializable]
public partial class GaussianCacheComponent : XRComponent, IRenderable
{
    private readonly RenderInfo3D _renderInfo;
    private readonly RenderInfo[] _renderedObjects;

    public GaussianCacheComponent()
    {
        _renderInfo = RenderInfo3D.New(
            this,
            new RenderCommandMethod3D((int)EDefaultRenderPass.PreRender, PrepareSplats),
            new RenderCommandMethod3D((int)EDefaultRenderPass.TransparentForward, DrawSplats));
        // Splats do not cast or receive shadows, so shadow passes do not collect them.
        _renderInfo.CastsShadows = false;
        _renderInfo.ReceivesShadows = false;
        _renderedObjects = [_renderInfo];
        _chunkAcceptedHandler = XRPropertyNotificationHandlers.FilterChanged(OnCacheChunkAccepted, nameof(GaussianCache.NextChunkIndex));
    }

    /// <inheritdoc />
    [Browsable(false)]
    public RenderInfo[] RenderedObjects => _renderedObjects;

    private GaussianCache? _cache;

    /// <summary>
    /// The cache to play. The component uploads the decoded data of the cache, and the new data of each chunk that the
    /// cache accepts later. <see cref="LoadAsync"/> sets this property.
    /// </summary>
    [YamlIgnore]
    public GaussianCache? Cache
    {
        get => _cache;
        set
        {
            GaussianCache? previous = _cache;
            if (!SetField(ref _cache, value))
                return;
            if (previous is not null)
                previous.PropertyChanged -= _chunkAcceptedHandler;
            // A change notification can assign another cache; that assignment queues its own upload.
            if (!ReferenceEquals(_cache, value))
                return;
            if (value is not null)
                value.PropertyChanged += _chunkAcceptedHandler;
            QueueCacheUpload(value);
        }
    }

    private bool _playing = true;

    /// <summary>When true, the playback time moves forward with the engine time, multiplied by <see cref="PlaybackSpeed"/>.</summary>
    [Category("Playback")]
    public bool Playing
    {
        get => _playing;
        set => SetField(ref _playing, value);
    }

    private float _playbackSpeed = 1.0f;

    /// <summary>The rate of the playback time, relative to the engine time. A negative value plays backward.</summary>
    [Category("Playback")]
    public float PlaybackSpeed
    {
        get => _playbackSpeed;
        set => SetField(ref _playbackSpeed, float.IsFinite(value) ? value : 0.0f);
    }

    private float _playbackTimeSeconds;

    /// <summary>
    /// The playback time in seconds. The loop mode of the cache maps it to a frame: a loop wraps, a ping-pong cache
    /// plays forward and then backward, and a cache that plays once stops at its last frame.
    /// </summary>
    [Category("Playback")]
    public float PlaybackTimeSeconds
    {
        get => _playbackTimeSeconds;
        set => SetField(ref _playbackTimeSeconds, float.IsFinite(value) ? value : 0.0f);
    }

    private float _lodThresholdPixels = 2.0f;

    /// <summary>
    /// A node is replaced by its children when its projected 3-sigma radius is at least this number of pixels and its
    /// children are loaded. Smaller values draw more splats.
    /// </summary>
    [Category("Rendering")]
    public float LodThresholdPixels
    {
        get => _lodThresholdPixels;
        set => SetField(ref _lodThresholdPixels, float.IsFinite(value) && value > 0.0f ? value : 0.0f);
    }

    private int _maxRenderedSplats = 2_000_000;

    /// <summary>
    /// The largest number of splats that the renderer draws. Splats over this number are dropped, and the component logs
    /// this one time.
    /// </summary>
    [Category("Rendering")]
    public int MaxRenderedSplats
    {
        get => _maxRenderedSplats;
        set => SetField(ref _maxRenderedSplats, Math.Clamp(value, 1, XREngine.Rendering.Gaussian.GaussianSplatRenderer.MaxCapacity));
    }

    /// <summary>Sets the playback time. It does not change <see cref="Playing"/>.</summary>
    public void Seek(float seconds)
        => PlaybackTimeSeconds = seconds;

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RegisterTick(ETickGroup.Normal, ETickOrder.Animation, AdvancePlayback);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Normal, ETickOrder.Animation, AdvancePlayback);
        base.OnComponentDeactivated();
    }

    protected override void OnDestroying()
    {
        if (_cache is { } cache)
            cache.PropertyChanged -= _chunkAcceptedHandler;
        ReleaseGpuResources();
        base.OnDestroying();
    }

    protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
    {
        base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);
        _renderInfo.CullingOffsetMatrix = renderMatrix;
    }

    private void AdvancePlayback()
    {
        if (!_playing || _playbackSpeed == 0.0f)
            return;

        float delta = RuntimeEngine.Time.Timer.Update.Delta * _playbackSpeed;
        if (!float.IsFinite(delta) || delta == 0.0f)
            return;

        // The time changes every frame, so it does not publish change notifications from here.
        SetField(ref _playbackTimeSeconds, _playbackTimeSeconds + delta, publishNotifications: false, nameof(PlaybackTimeSeconds));
    }
}
