using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine.Components.Lights;

/// <summary>
/// Double-buffered spectator refresh. Completed consumers retain exact slot leases;
/// a pinned slot defers refresh rather than waiting or overwriting in-flight readers.
/// </summary>
public sealed class VrSpectatorOutputComponent : XRComponent
{
    private SpectatorTextureCaptureComponent? _first, _second, _published, _writer;
    private XRWindow? _window;
    private Action? _refreshCallback;
    private readonly object _attachmentSync = new();
    private long _activationGeneration;
    private long _historyVersion, _writerHistoryVersion;
    private long _nextRefresh;
    private float _framesPerSecond = 30;
    private uint _width = 1280, _height = 720;
    private XRCamera? _sourceCamera;
    private int _historyReset;
    public XRCamera? SourceCamera { get => _sourceCamera; set => SetField(ref _sourceCamera, value); }
    public float FramesPerSecond
    {
        get => _framesPerSecond;
        set => SetField(ref _framesPerSecond, float.IsFinite(value) ? Math.Clamp(value, 1, 120) : throw new ArgumentOutOfRangeException(nameof(value)));
    }
    public uint Width { get => _width; set => SetField(ref _width, Math.Clamp(value, 16u, 8192u)); }
    public uint Height { get => _height; set => SetField(ref _height, Math.Clamp(value, 16u, 8192u)); }
    public long CompletedFrames { get; private set; }
    public string? LastFailure { get; private set; }

    public bool TryAcquireCompletedOutput([NotNullWhen(true)] out AdvancedOffscreenTextureCaptureLease? lease)
    {
        SpectatorTextureCaptureComponent? published = Volatile.Read(ref _published);
        if (published is not null)
            return published.TryAcquireCompletedOutput(out lease);
        lease = null;
        return false;
    }

    public void ResetHistory() => Interlocked.Exchange(ref _historyReset, 1);

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        if (_first is null)
        {
            SceneNode.NewChild(out SpectatorTextureCaptureComponent first, "Spectator Output A");
            SceneNode.NewChild(out SpectatorTextureCaptureComponent second, "Spectator Output B");
            _first = first;
            _second = second;
        }
        _first.IsActive = true;
        _second!.IsActive = true;
        long generation = Interlocked.Increment(ref _activationGeneration);
        RuntimeEngine.AddRenderThreadCoroutine(() => AttachWindow(generation), "VrSpectator.AttachOutput", RenderThreadJobKind.RenderPipelineResource);
    }

    private bool AttachWindow(long generation)
    {
        if (generation != Interlocked.Read(ref _activationGeneration) || !IsActiveInHierarchy || IsDestroyed)
            return true;
        if (_window is not null)
            return true;
        foreach (XRWindow window in RuntimeEngine.Windows)
        {
            if (!ReferenceEquals(window.TargetWorldInstance, World.GetRenderWorld()))
                continue;
            lock (_attachmentSync)
            {
                if (generation != Interlocked.Read(ref _activationGeneration) || !IsActiveInHierarchy || IsDestroyed)
                    return true;
                if (_window is not null)
                    return true;
                _window = window;
                Volatile.Write(ref _published, null);
                ResetHistory();
                _refreshCallback = () => Refresh(generation);
                window.RenderViewportsCallback += _refreshCallback;
            }
            return true;
        }
        LastFailure = "No rendering window hosts the spectator world.";
        return false;
    }

    private void Refresh(long generation)
    {
        // A lifecycle change may contend briefly; optional spectator work must never wait for it.
        if (!Monitor.TryEnter(_attachmentSync))
            return;
        try { RefreshCore(generation); }
        finally { Monitor.Exit(_attachmentSync); }
    }

    private void RefreshCore(long generation)
    {
        if (generation != Interlocked.Read(ref _activationGeneration) || !IsActiveInHierarchy || SourceCamera is null || _first is null || _second is null)
            return;
        try
        {
            if (Interlocked.Exchange(ref _historyReset, 0) != 0)
            {
                _first.InvalidateViewHistory();
                _second.InvalidateViewHistory();
                ++_historyVersion;
                Volatile.Write(ref _published, null);
                _nextRefresh = 0;
            }
            // Accepted native work can survive deactivation or an exception after authoring.
            // Drain both slot owners even when no orchestration receipt was published.
            if (!DrainUnownedWriter(_first) || !DrainUnownedWriter(_second))
                return;
            if (!TryAdvanceWriter())
                return;
            long now = Stopwatch.GetTimestamp();
            if (now < _nextRefresh)
                return;
            SpectatorTextureCaptureComponent next = ReferenceEquals(_published, _first) ? _second : _first;
            if (next.IsCaptureQuarantined)
            {
                LastFailure = "Spectator output lost trustworthy GPU completion; recreate the output owner.";
                _nextRefresh = long.MaxValue;
                return;
            }
            next.SourceCamera = SourceCamera;
            next.Width = Width;
            next.Height = Height;
            if (next.TryCapture())
            {
                _writer = next;
                _writerHistoryVersion = _historyVersion;
                _nextRefresh = now + (long)(Stopwatch.Frequency / FramesPerSecond);
                LastFailure = null;
            }
            else
                _nextRefresh = now + (long)(Stopwatch.Frequency / FramesPerSecond);
        }
        catch (Exception exception)
        {
            // A failed capture never aborts the headset/window submission callback.
            LastFailure = exception.Message;
            _nextRefresh = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        }
    }

    private bool TryAdvanceWriter()
    {
        if (_writer is null)
            return true;
        if (!_writer.TryCompleteCapture())
        {
            bool quarantined = _writer.IsCaptureQuarantined;
            if (!quarantined && _writer.HasPendingCapture)
                return false;
            _writer = null;
            LastFailure = quarantined
                ? "Spectator output lost trustworthy GPU completion; recreate the output owner."
                : "Spectator capture was rejected before completion; refresh will retry.";
            _nextRefresh = quarantined ? long.MaxValue : Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
            return false;
        }
        if (_writerHistoryVersion == _historyVersion)
            Volatile.Write(ref _published, _writer);
        _writer = null;
        CompletedFrames++;
        return true;
    }

    private bool DrainUnownedWriter(SpectatorTextureCaptureComponent slot)
    {
        if (!ReferenceEquals(slot, _writer) && slot.HasPendingCapture)
            slot.TryCompleteCapture();
        if (!slot.IsCaptureQuarantined)
            return true;
        LastFailure = "Spectator output lost trustworthy GPU completion; recreate the output owner.";
        _nextRefresh = long.MaxValue;
        return false;
    }

    protected override void OnComponentDeactivated()
    {
        DetachOutput();
        if (_first is not null) _first.IsActive = false;
        if (_second is not null) _second.IsActive = false;
        base.OnComponentDeactivated();
    }

    protected override void OnDestroying()
    {
        DetachOutput();
        _first?.SceneNode.Destroy();
        _second?.SceneNode.Destroy();
        base.OnDestroying();
    }

    private void DetachOutput()
    {
        XRWindow? window;
        Action? callback;
        lock (_attachmentSync)
        {
            Interlocked.Increment(ref _activationGeneration);
            Volatile.Write(ref _published, null);
            window = _window;
            callback = _refreshCallback;
            _window = null;
            _refreshCallback = null;
        }
        if (window is not null && callback is not null)
            RuntimeEngine.EnqueueMainThreadTask(() => window.RenderViewportsCallback -= callback,
                "VrSpectator.DetachOutput", RenderThreadJobKind.RenderPipelineResource);
    }

}
