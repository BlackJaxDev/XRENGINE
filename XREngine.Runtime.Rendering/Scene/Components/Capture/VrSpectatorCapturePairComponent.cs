using System.Diagnostics;
using System.Threading;
using XREngine.Rendering;

namespace XREngine.Components.Lights;

/// <summary>
/// Schedules two independent spectator targets. One completed target remains readable
/// while the other writes; a pinned consumer defers reuse of its exact target.
/// </summary>
public sealed class VrSpectatorCapturePairComponent : XRComponent
{
    private readonly Func<bool> _attachCallback;
    private readonly Action _renderCallback;
    private readonly Action _postRenderCallback;
    private readonly Action _collectCallback;
    private readonly Action _swapCallback;
    private readonly Action _detachCallback;
    private readonly VrSpectatorCaptureComponent?[] _slots = new VrSpectatorCaptureComponent?[2];
    private readonly object _outputGate = new();
    private readonly long[] _submittedSerial = new long[2];
    private readonly long[] _observedVersion = new long[2];
    private XRWindow? _window;
    private IRuntimeRenderSchedulingServices? _schedulingHost;
    private int _stagedIndex = -1;
    private int _latestCompletedIndex = -1;
    private long _latestCompletedSerial;
    private long _minimumReadableSerial;
    private long _nextSerial;
    private long _nextCaptureTimestamp;
    private int _warmupRequired = 1;
    private uint _framesPerSecond = 30u;
    private bool _captureEnabled;
    private CameraComponent? _desktopCamera;
    private bool _desktopSpectatorActive;
    private readonly object _desktopWarmupSync = new();
    private int _desktopWarmupRemaining;

    public VrSpectatorCapturePairComponent()
    {
        _attachCallback = AttachToWindow;
        _renderCallback = BeforeRenderSpectator;
        _postRenderCallback = AfterRenderSpectator;
        _collectCallback = CollectSpectator;
        _swapCallback = SwapSpectator;
        _detachCallback = DetachFromWindow;
    }

    public VrSpectatorCaptureComponent? FirstSlot
    {
        get => _slots[0];
        set => SetField(ref _slots[0], value);
    }

    public VrSpectatorCaptureComponent? SecondSlot
    {
        get => _slots[1];
        set => SetField(ref _slots[1], value);
    }

    public uint Width
    {
        get => FirstSlot?.Width ?? 0u;
        set
        {
            if (FirstSlot is { } first) first.Width = value;
            if (SecondSlot is { } second) second.Width = value;
        }
    }

    public uint Height
    {
        get => FirstSlot?.Height ?? 0u;
        set
        {
            if (FirstSlot is { } first) first.Height = value;
            if (SecondSlot is { } second) second.Height = value;
        }
    }

    public uint FramesPerSecond
    {
        get => _framesPerSecond;
        set => SetField(ref _framesPerSecond, Math.Clamp(value, 1u, 120u));
    }

    public bool CaptureEnabled
    {
        get => Volatile.Read(ref _captureEnabled);
        set
        {
            if (!SetField(ref _captureEnabled, value) || value)
                return;
            CancelPreparedSpectator();
        }
    }

    public CameraComponent? DesktopCamera
    {
        get => _desktopCamera;
        set => SetField(ref _desktopCamera, value);
    }

    /// <summary>Warms imported skinned bounds before restoring desktop frustum culling.</summary>
    public bool DesktopSpectatorActive
    {
        get => _desktopSpectatorActive;
        set
        {
            if (!SetField(ref _desktopSpectatorActive, value))
                return;
            if (value)
                BeginDesktopWarmup();
            else
                EndDesktopWarmup();
        }
    }

    public long CompletedSerial => Interlocked.Read(ref _latestCompletedSerial);

    /// <summary>Acquires the latest completed generation for a consumer with its own GPU read fence.</summary>
    public bool TryAcquireCompletedOutput(out AdvancedOffscreenTextureCaptureLease? lease)
    {
        lock (_outputGate)
        {
            if (CompletedSerial < Interlocked.Read(ref _minimumReadableSerial))
            {
                lease = null;
                return false;
            }
            int index = Volatile.Read(ref _latestCompletedIndex);
            if (index < 0 || _slots[index] is not { } capture)
            {
                lease = null;
                return false;
            }
            return capture.TryAcquireCompletedOutput(out lease);
        }
    }

    /// <summary>Suppresses completed frames from before a camera cut until a new capture finishes.</summary>
    public void ResetOutputAfterCut()
    {
        int stagedIndex;
        lock (_outputGate)
        {
            Interlocked.Exchange(ref _minimumReadableSerial, Interlocked.Read(ref _nextSerial) + 1);
            stagedIndex = Interlocked.Exchange(ref _stagedIndex, -1);
        }
        if (stagedIndex >= 0)
            _slots[stagedIndex]?.CancelStagedCapture();
        Interlocked.Exchange(ref _nextCaptureTimestamp, 0);
        Interlocked.Exchange(ref _warmupRequired, 1);
        if (DesktopSpectatorActive)
            BeginDesktopWarmup();
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        ResetOutputAfterCut();
        RuntimeEngine.AddRenderThreadCoroutine(_attachCallback,
            "VrSpectatorCapturePairComponent.Attach", RenderThreadJobKind.RenderPipelineResource);
    }

    protected override void OnComponentDeactivated()
    {
        CaptureEnabled = false;
        DesktopSpectatorActive = false;
        RuntimeEngine.EnqueueMainThreadTask(_detachCallback,
            "VrSpectatorCapturePairComponent.Detach", RenderThreadJobKind.RenderPipelineResource);
        base.OnComponentDeactivated();
    }

    private bool AttachToWindow()
    {
        if (IsDestroyed || !IsActiveInHierarchy)
            return true;
        var renderWorld = World?.GetRenderWorld();
        if (renderWorld is null)
            return false;
        foreach (XRWindow window in RuntimeEngine.Windows)
        {
            if (!ReferenceEquals(window.TargetWorldInstance, renderWorld))
                continue;
            if (ReferenceEquals(_window, window))
                return true;
            DetachFromWindow();
            _window = window;
            _schedulingHost = RuntimeRenderingHostServices.Scheduling;
            _schedulingHost.SubscribeViewportCollectVisible(_collectCallback);
            _schedulingHost.SubscribeViewportSwapBuffers(_swapCallback);
            window.RenderViewportsCallback += _renderCallback;
            window.PostRenderViewportsCallback += _postRenderCallback;
            return true;
        }
        return false;
    }

    private void DetachFromWindow()
    {
        CancelPreparedSpectator();
        if (_schedulingHost is { } schedulingHost)
        {
            schedulingHost.UnsubscribeViewportCollectVisible(_collectCallback);
            schedulingHost.UnsubscribeViewportSwapBuffers(_swapCallback);
            _schedulingHost = null;
        }
        if (_window is not { } window)
            return;
        window.RenderViewportsCallback -= _renderCallback;
        window.PostRenderViewportsCallback -= _postRenderCallback;
        _window = null;
    }

    private void CollectSpectator()
    {
        for (int i = 0; i < _slots.Length; i++)
            _slots[i]?.CollectStagedCapture();
    }

    private void SwapSpectator()
    {
        for (int i = 0; i < _slots.Length; i++)
            _slots[i]?.SwapStagedCapture();
    }

    private void CancelPreparedSpectator()
    {
        int index = Interlocked.Exchange(ref _stagedIndex, -1);
        if (index >= 0)
            _slots[index]?.CancelStagedCapture();
    }

    private void BeforeRenderSpectator()
    {
        if (_window is null || !ReferenceEquals(_window.TargetWorldInstance, World?.GetRenderWorld()))
        {
            DetachFromWindow();
            if (IsActiveInHierarchy && !IsDestroyed)
                RuntimeEngine.AddRenderThreadCoroutine(_attachCallback,
                    "VrSpectatorCapturePairComponent.Reattach", RenderThreadJobKind.RenderPipelineResource);
            return;
        }

        int stagedIndex = Volatile.Read(ref _stagedIndex);
        if (stagedIndex >= 0 && _slots[stagedIndex] is { } staged)
        {
            if (!CaptureEnabled || !IsActiveInHierarchy || IsDestroyed)
                staged.CancelStagedCapture();
            else
                _ = staged.TryRenderStagedCapture();
            if (staged.HasStagedCapture)
                return;
            Interlocked.CompareExchange(ref _stagedIndex, -1, stagedIndex);
        }

        if (!CaptureEnabled || !IsActiveInHierarchy || IsDestroyed)
            return;
        long now = Stopwatch.GetTimestamp();
        if (now < Interlocked.Read(ref _nextCaptureTimestamp))
            return;

        if (Interlocked.Exchange(ref _warmupRequired, 0) != 0)
        {
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i] is { } slot)
                    slot.CaptureCullWithFrustum = false;
        }

        int target = Volatile.Read(ref _latestCompletedIndex) == 0 ? 1 : 0;
        if (_slots[target] is not { } writer)
            return;
        // Reserve identity before preparing the request. A concurrent camera
        // cut can then invalidate it even if preparation has not returned yet.
        long serial = Interlocked.Increment(ref _nextSerial);
        if (!writer.TryStageSynchronizedCapture())
            return;
        bool stale;
        lock (_outputGate)
        {
            stale = serial < _minimumReadableSerial || !CaptureEnabled;
            if (!stale)
            {
                _submittedSerial[target] = serial;
                Volatile.Write(ref _stagedIndex, target);
            }
        }
        if (stale)
        {
            writer.CancelStagedCapture();
            return;
        }
        Interlocked.Exchange(ref _nextCaptureTimestamp,
            now + Math.Max(1L, Stopwatch.Frequency / FramesPerSecond));
    }

    private void AfterRenderSpectator()
    {
        if (DesktopSpectatorActive && Volatile.Read(ref _desktopWarmupRemaining) > 0)
        {
            lock (_desktopWarmupSync)
                if (DesktopSpectatorActive && _desktopWarmupRemaining > 0 &&
                    --_desktopWarmupRemaining == 0 && DesktopCamera is { } desktopCamera)
                    desktopCamera.CullWithFrustum = true;
        }
        for (int i = 0; i < _slots.Length; i++)
        {
            if (_slots[i] is not { } capture)
                continue;
            _ = capture.TryCompleteCapture();
            long version = capture.CaptureVersion;
            if (version == _observedVersion[i])
                continue;
            _observedVersion[i] = version;
            if (_submittedSerial[i] <= _latestCompletedSerial)
                continue;
            if (_submittedSerial[i] >= Interlocked.Read(ref _minimumReadableSerial))
                capture.CaptureCullWithFrustum = true;
            lock (_outputGate)
            {
                Volatile.Write(ref _latestCompletedIndex, i);
                Interlocked.Exchange(ref _latestCompletedSerial, _submittedSerial[i]);
            }
        }

    }

    private void BeginDesktopWarmup()
    {
        lock (_desktopWarmupSync)
        {
            if (DesktopCamera is { } camera)
                camera.CullWithFrustum = false;
            // A cut can arrive during the current render. The second callback guarantees
            // at least one whole frame of the selected camera without frustum culling.
            Volatile.Write(ref _desktopWarmupRemaining, 2);
        }
    }

    private void EndDesktopWarmup()
    {
        lock (_desktopWarmupSync)
        {
            if (_desktopWarmupRemaining > 0 && DesktopCamera is { } camera)
                camera.CullWithFrustum = true;
            Volatile.Write(ref _desktopWarmupRemaining, 0);
        }
    }
}
