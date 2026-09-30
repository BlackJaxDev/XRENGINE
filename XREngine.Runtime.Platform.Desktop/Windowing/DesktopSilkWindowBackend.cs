using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using XREngine.Data.Vectors;
using XREngine.Input.Devices;
using XREngine.Rendering;

namespace XREngine.Runtime.Platform.Desktop.Windowing;

/// <summary>
/// Owns one Silk desktop window and its native input subscriptions. Callers may read published
/// snapshots from other threads; native operations stay on the thread that created this object.
/// </summary>
internal sealed class DesktopSilkWindowBackend : IRuntimeWindowBackend
{
    private static readonly ConcurrentBag<DesktopSilkWindowBackend> AbandonedResources = new();
    private readonly IWindow _window;
    private readonly RuntimeWindowCreateOptions _options;
    private readonly WindowInputSnapshotAccumulator _inputAccumulator = new();
    private readonly WindowInputSnapshotAccumulator _uiInputAccumulator = new();
    private readonly object _snapshotSync = new();
    private readonly HashSet<IKeyboard> _keyboards = [];
    private readonly HashSet<IMouse> _mice = [];
    private IRuntimeWindowEventSink? _sink;
    private IInputContext? _input;
    private WindowSurfaceSnapshot _surface;
    private WindowEventSnapshot _events;
    private ulong _surfaceSequence;
    private ulong _eventSequence;
    private bool _initialized;
    private bool _disposed;
    private bool _closeRequested;
    private bool _focused;
    private int _closeCallbackDepth;
    private string _title;
    private nint _platformWindowHandle;
    private nint _operatingSystemWindowHandle;
    private IVector2 _clientScreenPosition;
    private RuntimeDesktopMonitor[] _monitors = [];
    private long _lastMonitorCaptureTicks;
    private IRuntimeWindowGlContext? _glContext;
    private IRuntimeWindowVulkanSurface? _vulkanSurface;
    private IDesktopInteractiveResizeHook? _resizeHook;
    private EInteractiveWindowResizeStrategy _resizeStrategy;
    private bool _interactiveResize;

    public DesktopSilkWindowBackend(IWindow window, RuntimeWindowCreateOptions options)
    {
        _window = window;
        _options = options;
        _title = options.Startup.Title ?? string.Empty;
        _resizeStrategy = options.ResizeStrategy;
        _clientScreenPosition = options.Position;
        OwnerThreadId = Environment.CurrentManagedThreadId;
        LifetimeGeneration = Stopwatch.GetTimestamp();
    }

    public RuntimeWindowBackendKind Kind { get; private set; } = RuntimeWindowBackendKind.Unknown;
    public RuntimeWindowBackendOwnershipInfo Ownership => RuntimeWindowBackendOwnershipInfo.ForBackend(Kind);
    public RuntimeGraphicsApiKind GraphicsApi => _options.GraphicsApi;
    public int OwnerThreadId { get; }
    public long LifetimeGeneration { get; }
    internal long SharedParentGeneration => _options.SharedContext?.OwnerGeneration ?? 0;
    public WindowSurfaceSnapshot Surface { get { lock (_snapshotSync) return _surface; } }
    public WindowEventSnapshot Events { get { lock (_snapshotSync) return _events; } }
    public WindowInputSnapshot Input => _inputAccumulator.Latest;
    public WindowInputSnapshot ConsumeInput() => _inputAccumulator.ConsumeLatest();
    public WindowInputSnapshot ConsumeUiInput() => _uiInputAccumulator.ConsumeLatest();
    public WindowInputSnapshot ConsumeUiInput(List<WindowInputEvent> orderedDestination)
        => _uiInputAccumulator.ConsumeLatest(orderedDestination);
    public IRuntimeWindowGlContext? GlContext => _glContext;
    public IRuntimeWindowVulkanSurface? VulkanSurface => _vulkanSurface;
    public nint PlatformWindowHandle => _platformWindowHandle;
    public nint OperatingSystemWindowHandle => _operatingSystemWindowHandle;
    public IVector2 ClientScreenPosition { get { lock (_snapshotSync) return _clientScreenPosition; } }
    public ReadOnlyMemory<RuntimeDesktopMonitor> Monitors { get { lock (_snapshotSync) return _monitors; } }
    public string Title => Volatile.Read(ref _title);
    public bool IsClosing => _closeRequested;
    internal IWindow NativeWindow => _window;

    public void Initialize(IRuntimeWindowEventSink sink)
    {
        AssertOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
            throw new InvalidOperationException("The desktop window is already initialized.");

        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _window.Load += OnLoad;
        _window.FramebufferResize += OnFramebufferResize;
        _window.FocusChanged += OnFocusChanged;
        _window.FileDrop += OnFileDropped;
        _window.Closing += OnClosing;
        _window.Render += OnRender;
        try
        {
            _window.Initialize();
            _initialized = true;
            if (_options.Purpose != RuntimeWindowPurpose.Presentation)
                DesktopSilkWindowBackendFactory.RegisterAuxiliaryWindow(this);
            Kind = ResolveBackendKind();
            _platformWindowHandle = _window.Handle;
            _operatingSystemWindowHandle = _window.Native?.Win32?.Hwnd ?? 0;
            if (_window.GLContext is not null)
                _glContext = new DesktopSilkGlContext(_window, OwnerThreadId, LifetimeGeneration);
            if (_window.VkSurface is not null)
                _vulkanSurface = new DesktopSilkVulkanSurface(_window, LifetimeGeneration);
            PublishSurface();
            PublishEvents(false);
            PublishInput();
            RefreshMonitors();
            InstallResizeHook();
            Vector2D<int> requestedPosition = new(_options.Position.X, _options.Position.Y);
            if (_window.Position != requestedPosition)
                _window.Position = requestedPosition;
        }
        catch
        {
            DesktopSilkWindowBackendFactory.UnregisterAuxiliaryWindow(this);
            _resizeHook?.Dispose();
            _resizeHook = null;
            UnlinkWindow();
            throw;
        }
    }

    public void PumpEvents()
    {
        AssertOwnerThread();
        _window.DoEvents();
        PublishSurface();
        PublishEvents(false);
        PublishInput();
        if (Stopwatch.GetElapsedTime(_lastMonitorCaptureTicks) >= TimeSpan.FromSeconds(1))
            RefreshMonitors();
        if (_options.Purpose == RuntimeWindowPurpose.Presentation)
            DesktopSilkWindowBackendFactory.PumpAuxiliaryWindows(OwnerThreadId, LifetimeGeneration);
    }

    public void DispatchRender()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _glContext?.AssertOwnerThread();
        _window.DoRender();
    }

    public void RequestSize(IVector2 size)
    {
        AssertOwnerThread();
        _window.Size = new Vector2D<int>(size.X, size.Y);
    }

    public void RequestPosition(IVector2 position)
    {
        AssertOwnerThread();
        _window.Position = new Vector2D<int>(position.X, position.Y);
        PublishClientScreenPosition();
    }

    public void RequestClientScreenPosition(IVector2 position)
    {
        AssertOwnerThread();
        PublishClientScreenPosition();
        Vector2D<int> windowPosition = _window.Position;
        IVector2 clientScreenPosition = ClientScreenPosition;
        _window.Position = new Vector2D<int>(
            position.X - (clientScreenPosition.X - windowPosition.X),
            position.Y - (clientScreenPosition.Y - windowPosition.Y));
        PublishClientScreenPosition();
    }

    public void RequestTitle(string title)
    {
        AssertOwnerThread();
        _window.Title = title;
        Volatile.Write(ref _title, title);
    }

    public void RequestState(EWindowState state)
    {
        AssertOwnerThread();
        _window.WindowState = state == EWindowState.Fullscreen ? WindowState.Fullscreen : WindowState.Normal;
        _window.WindowBorder = state == EWindowState.Borderless || !_options.Startup.UseNativeTitleBar
            ? WindowBorder.Hidden
            : WindowBorder.Resizable;
    }

    public void RequestFocus()
    {
        AssertOwnerThread();
        _window.Focus();
    }

    public void RequestVisibility(bool visible)
    {
        AssertOwnerThread();
        _window.IsVisible = visible;
    }

    public void RequestCursorCapture(bool captured)
    {
        AssertOwnerThread();
        if (_input is { Mice.Count: > 0 })
            _input.Mice[0].Cursor.CursorMode = captured ? CursorMode.Disabled : CursorMode.Normal;
    }

    public void SetVSync(bool enabled)
    {
        AssertOwnerThread();
        _window.VSync = enabled;
    }

    public void RequestClose()
    {
        AssertOwnerThread();
        _window.Close();
    }

    public bool TryCancelClose()
    {
        AssertOwnerThread();
        try
        {
            _window.IsClosing = false;
            _closeRequested = false;
            PublishEvents(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void SetInteractiveResizeStrategy(EInteractiveWindowResizeStrategy strategy)
    {
        AssertOwnerThread();
        if (strategy == _resizeStrategy)
            return;
        EInteractiveWindowResizeStrategy previous = _resizeStrategy;
        _resizeHook?.Dispose();
        _resizeHook = null;
        try
        {
            _resizeStrategy = strategy;
            InstallResizeHook();
        }
        catch
        {
            _resizeHook?.Dispose();
            _resizeHook = null;
            _resizeStrategy = previous;
            InstallResizeHook();
            throw;
        }
    }

    private void InstallResizeHook()
    {
        _resizeHook = DesktopInteractiveResizeHookFactory.Create(_resizeStrategy);
        _resizeHook?.Install(this);
        if (_input is not null)
            _resizeHook?.OnInputCreated(_input);
    }

    internal void BeginNativeResize()
    {
        AssertOwnerThread();
        _interactiveResize = true;
        PublishSurface();
        _sink?.InteractiveResizeStarted();
    }

    internal void UpdateNativeResize()
    {
        AssertOwnerThread();
        PublishSurface();
        WindowSurfaceSnapshot surface = Surface;
        _sink?.SurfaceChanged(surface);
        _sink?.InteractiveResizeUpdated(new IVector2(surface.FramebufferWidth, surface.FramebufferHeight));
        _sink?.RepaintRequested();
    }

    internal void EndNativeResize()
    {
        AssertOwnerThread();
        _interactiveResize = false;
        PublishSurface();
        _sink?.InteractiveResizeEnded();
        _sink?.SurfaceChanged(Surface);
        _sink?.RepaintRequested();
    }

    public void RetainAbandonedResources()
    {
        AssertOwnerThread();
        DesktopSilkWindowBackendFactory.UnregisterAuxiliaryWindow(this);
        _resizeHook?.Dispose();
        _resizeHook = null;
        UnlinkWindow();
        _sink = null;
        AbandonedResources.Add(this);
    }

    public void Dispose()
    {
        AssertOwnerThread();
        if (_disposed)
            return;
        if (_closeCallbackDepth != 0)
            throw new InvalidOperationException("The native window cannot be destroyed from its close callback; retire renderer resources first and dispose after the callback returns.");
        _disposed = true;
        DesktopSilkWindowBackendFactory.UnregisterAuxiliaryWindow(this);
        _resizeHook?.Dispose();
        _resizeHook = null;
        UnlinkWindow();
        _input?.Dispose();
        _input = null;
        (_glContext as DesktopSilkGlContext)?.Retire();
        _glContext = null;
        (_vulkanSurface as DesktopSilkVulkanSurface)?.Retire();
        _vulkanSurface = null;
        _window.Dispose();
        PublishEvents(false);
    }

    private void OnLoad()
    {
        _input = _window.CreateInput();
        _resizeHook?.OnInputCreated(_input);
        _input.ConnectionChanged += OnConnectionChanged;
        for (int i = 0; i < _input.Keyboards.Count; i++)
            Subscribe(_input.Keyboards[i]);
        for (int i = 0; i < _input.Mice.Count; i++)
            Subscribe(_input.Mice[i]);
        PublishInput();
    }

    private void OnFramebufferResize(Vector2D<int> _)
    {
        PublishSurface();
        _sink?.SurfaceChanged(Surface);
    }

    private void OnFocusChanged(bool focused)
    {
        _focused = focused;
        PublishEvents(false);
        _sink?.FocusChanged(focused);
    }

    private void OnFileDropped(string[] paths) => _sink?.FileDropped(paths);
    private void OnRender(double deltaSeconds) => _sink?.RenderRequested(deltaSeconds);

    private void OnClosing()
    {
        _closeCallbackDepth++;
        bool approved = false;
        try
        {
            _closeRequested = true;
            PublishEvents(false);
            approved = _sink?.CloseRequested() ?? false;
        }
        finally
        {
            // The renderer retires GPU resources before the owner destroys this native window.
            // Cancel Silk's immediate destruction while the callback is still on its stack.
            _window.IsClosing = false;
            _closeRequested = approved;
            PublishEvents(approved);
            _closeCallbackDepth--;
        }
    }

    private void OnConnectionChanged(IInputDevice device, bool connected)
    {
        switch (device)
        {
            case IKeyboard keyboard:
                if (connected) Subscribe(keyboard); else Unsubscribe(keyboard);
                break;
            case IMouse mouse:
                if (connected) Subscribe(mouse); else Unsubscribe(mouse);
                break;
        }
        PublishInput();
    }

    private void Subscribe(IKeyboard keyboard)
    {
        if (!_keyboards.Add(keyboard)) return;
        keyboard.KeyDown += OnKeyDown;
        keyboard.KeyUp += OnKeyUp;
        keyboard.KeyChar += OnKeyChar;
    }

    private void Unsubscribe(IKeyboard keyboard)
    {
        if (!_keyboards.Remove(keyboard)) return;
        keyboard.KeyDown -= OnKeyDown;
        keyboard.KeyUp -= OnKeyUp;
        keyboard.KeyChar -= OnKeyChar;
    }

    private void Subscribe(IMouse mouse)
    {
        if (!_mice.Add(mouse)) return;
        mouse.MouseDown += OnMouseDown;
        mouse.MouseUp += OnMouseUp;
        mouse.MouseMove += OnMouseMove;
        mouse.Scroll += OnScroll;
        _inputAccumulator.PrimePointerPosition(mouse.Position.X, mouse.Position.Y);
        _uiInputAccumulator.PrimePointerPosition(mouse.Position.X, mouse.Position.Y);
    }

    private void Unsubscribe(IMouse mouse)
    {
        if (!_mice.Remove(mouse)) return;
        mouse.MouseDown -= OnMouseDown;
        mouse.MouseUp -= OnMouseUp;
        mouse.MouseMove -= OnMouseMove;
        mouse.Scroll -= OnScroll;
    }

    private void OnKeyDown(IKeyboard _, Key key, int __)
    {
        EKey mapped = DesktopWindowInputKeyMap.ToEngineKey(key);
        if (_inputAccumulator.RecordKeyDown(mapped))
            _sink?.KeyDown(mapped);
        _uiInputAccumulator.RecordKeyDown(mapped);
    }

    private void OnKeyUp(IKeyboard _, Key key, int __)
    {
        EKey mapped = DesktopWindowInputKeyMap.ToEngineKey(key);
        _inputAccumulator.RecordKeyUp(mapped);
        _uiInputAccumulator.RecordKeyUp(mapped);
    }

    private void OnKeyChar(IKeyboard _, char value)
    {
        _inputAccumulator.RecordTextInput(value);
        _uiInputAccumulator.RecordTextInput(value);
    }

    private void OnMouseMove(IMouse _, System.Numerics.Vector2 position)
    {
        _inputAccumulator.RecordPointerPosition(position.X, position.Y);
        _uiInputAccumulator.RecordPointerPosition(position.X, position.Y);
    }

    private void OnScroll(IMouse _, ScrollWheel wheel)
    {
        _inputAccumulator.RecordScroll(wheel.X, wheel.Y);
        _uiInputAccumulator.RecordScroll(wheel.X, wheel.Y);
    }

    private void OnMouseDown(IMouse _, MouseButton button)
    {
        if (TryMapMouseButton(button, out EMouseButton mapped))
        {
            _inputAccumulator.RecordMouseDown(mapped);
            _uiInputAccumulator.RecordMouseDown(mapped);
        }
    }

    private void OnMouseUp(IMouse _, MouseButton button)
    {
        if (TryMapMouseButton(button, out EMouseButton mapped))
        {
            _inputAccumulator.RecordMouseUp(mapped);
            _uiInputAccumulator.RecordMouseUp(mapped);
        }
    }

    private static bool TryMapMouseButton(MouseButton native, out EMouseButton mapped)
    {
        mapped = native switch
        {
            MouseButton.Left => EMouseButton.LeftClick,
            MouseButton.Right => EMouseButton.RightClick,
            MouseButton.Middle => EMouseButton.MiddleClick,
            _ => default,
        };
        return native is MouseButton.Left or MouseButton.Right or MouseButton.Middle;
    }

    private void PublishSurface()
    {
        PublishClientScreenPosition();
        Vector2D<int> client = _window.Size;
        Vector2D<int> framebuffer = _window.FramebufferSize;
        WindowSurfaceSnapshot snapshot = new(
            ++_surfaceSequence,
            client.X,
            client.Y,
            framebuffer.X,
            framebuffer.Y,
            client.X > 0 ? (float)framebuffer.X / client.X : 1.0f,
            client.Y > 0 ? (float)framebuffer.Y / client.Y : 1.0f,
            _window.WindowState == WindowState.Minimized,
            _interactiveResize,
            Stopwatch.GetTimestamp());
        lock (_snapshotSync) _surface = snapshot;
    }

    private void PublishClientScreenPosition()
    {
        Vector2D<int> windowPosition = _window.Position;
        if (OperatingSystem.IsWindows() && _operatingSystemWindowHandle != 0)
        {
            NativePoint point = default;
            if (ClientToScreen(_operatingSystemWindowHandle, ref point))
            {
                lock (_snapshotSync)
                    _clientScreenPosition = new IVector2(point.X, point.Y);
                return;
            }
        }

        lock (_snapshotSync)
            _clientScreenPosition = new IVector2(windowPosition.X, windowPosition.Y);
    }

    private void RefreshMonitors()
    {
        RuntimeDesktopMonitor[] monitors = DesktopMonitorSnapshotCapture.Capture();
        lock (_snapshotSync)
            _monitors = monitors;
        _lastMonitorCaptureTicks = Stopwatch.GetTimestamp();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(nint handle, ref NativePoint point);

    private void PublishEvents(bool closeApproved)
    {
        WindowEventSnapshot snapshot = new(
            ++_eventSequence,
            _focused,
            Surface.IsMinimized,
            _closeRequested,
            closeApproved,
            _disposed,
            _disposed,
            Stopwatch.GetTimestamp(),
            Environment.CurrentManagedThreadId);
        lock (_snapshotSync) _events = snapshot;
    }

    private void PublishInput()
    {
        int keyboardCount = _input?.Keyboards.Count ?? 0;
        int mouseCount = _input?.Mice.Count ?? 0;
        int gamepadCount = _input?.Gamepads.Count ?? 0;
        bool captured = _input is { Mice.Count: > 0 } &&
            _input.Mice[0].Cursor.CursorMode is CursorMode.Disabled or CursorMode.Raw;
        WindowGamepadSnapshot gamepad = CapturePrimaryGamepad();
        _inputAccumulator.Publish(keyboardCount, mouseCount, gamepadCount, _focused, captured, gamepad);
        _uiInputAccumulator.Publish(keyboardCount, mouseCount, gamepadCount, _focused, captured, gamepad);
    }

    private WindowGamepadSnapshot CapturePrimaryGamepad()
    {
        if (_input is not { Gamepads.Count: > 0 })
            return default;

        IGamepad gamepad = _input.Gamepads[0];
        if (!gamepad.IsConnected)
            return default;

        ushort pressedButtonMask = 0;
        for (int i = 0; i < gamepad.Buttons.Count; i++)
        {
            Button button = gamepad.Buttons[i];
            if (button.Pressed && TryMapGamepadButton(button.Name, out EGamePadButton mapped))
                pressedButtonMask |= (ushort)(1u << (int)mapped);
        }

        return new WindowGamepadSnapshot(
            true,
            pressedButtonMask,
            gamepad.Triggers.Count > 0 ? gamepad.Triggers[0].Position : 0.0f,
            gamepad.Triggers.Count > 1 ? gamepad.Triggers[1].Position : 0.0f,
            gamepad.Thumbsticks.Count > 0 ? gamepad.Thumbsticks[0].X : 0.0f,
            gamepad.Thumbsticks.Count > 0 ? -gamepad.Thumbsticks[0].Y : 0.0f,
            gamepad.Thumbsticks.Count > 1 ? gamepad.Thumbsticks[1].X : 0.0f,
            gamepad.Thumbsticks.Count > 1 ? -gamepad.Thumbsticks[1].Y : 0.0f);
    }

    private static bool TryMapGamepadButton(ButtonName name, out EGamePadButton button)
    {
        button = name switch
        {
            ButtonName.DPadUp => EGamePadButton.DPadUp,
            ButtonName.DPadDown => EGamePadButton.DPadDown,
            ButtonName.DPadLeft => EGamePadButton.DPadLeft,
            ButtonName.DPadRight => EGamePadButton.DPadRight,
            ButtonName.Y => EGamePadButton.FaceUp,
            ButtonName.A => EGamePadButton.FaceDown,
            ButtonName.X => EGamePadButton.FaceLeft,
            ButtonName.B => EGamePadButton.FaceRight,
            ButtonName.LeftStick => EGamePadButton.LeftStick,
            ButtonName.RightStick => EGamePadButton.RightStick,
            ButtonName.Home => EGamePadButton.SpecialLeft,
            ButtonName.Start => EGamePadButton.SpecialRight,
            ButtonName.LeftBumper => EGamePadButton.LeftBumper,
            ButtonName.RightBumper => EGamePadButton.RightBumper,
            _ => (EGamePadButton)(-1),
        };
        return (int)button >= 0;
    }

    private void UnlinkWindow()
    {
        _window.Load -= OnLoad;
        _window.FramebufferResize -= OnFramebufferResize;
        _window.FocusChanged -= OnFocusChanged;
        _window.FileDrop -= OnFileDropped;
        _window.Closing -= OnClosing;
        _window.Render -= OnRender;
        if (_input is not null)
            _input.ConnectionChanged -= OnConnectionChanged;
        foreach (IKeyboard keyboard in _keyboards.ToArray()) Unsubscribe(keyboard);
        foreach (IMouse mouse in _mice.ToArray()) Unsubscribe(mouse);
    }

    private RuntimeWindowBackendKind ResolveBackendKind()
    {
        if (_window is not INativeWindowSource nativeSource || nativeSource.Native is null)
            return RuntimeWindowBackendKind.Unknown;
        string kind = nativeSource.Native.Kind.ToString();
        if (kind.Contains("Glfw", StringComparison.OrdinalIgnoreCase))
            return RuntimeWindowBackendKind.Glfw;
        if (kind.Contains("Sdl", StringComparison.OrdinalIgnoreCase))
            return RuntimeWindowBackendKind.Sdl;
        if (kind.Contains("Win32", StringComparison.OrdinalIgnoreCase))
            return RuntimeWindowBackendKind.Win32;
        return RuntimeWindowBackendKind.Unknown;
    }

    private void AssertOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != OwnerThreadId)
            throw new InvalidOperationException("Native desktop window access must execute on its creating thread.");
    }
}
