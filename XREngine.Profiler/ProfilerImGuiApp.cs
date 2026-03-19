using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using XREngine.Profiler.UI;

namespace XREngine.Profiler;

/// <summary>
/// Manages the ImGui lifecycle and renders the profiler UI.
/// Created once on window Load, disposed on window Close.
/// </summary>
internal sealed class ProfilerImGuiApp : IDisposable
{
    private readonly IWindow _window;
    private readonly GL _gl;
    private readonly IInputContext _input;
    private readonly ImGuiController _imgui;
    private readonly UdpProfilerReceiver _receiver;
    private readonly ProfilerPanelRenderer _renderer;

    // Panel visibility toggles (View menu)
    private bool _showProfilerTree = true;
    private bool _showFpsDropSpikes = true;
    private bool _showRenderStats = true;
    private bool _showGpuPipeline = true;
    private bool _showThreadAllocations = true;
    private bool _showComponentTimings = true;
    private bool _showBvhMetrics = true;
    private bool _showJobSystem = true;
    private bool _showMainThreadInvokes = true;
    private bool _showConnectionInfo = true;
    private bool _showSettings = true;

    private bool _dockLayoutInitialized;

    public ProfilerImGuiApp(IWindow window, int port)
    {
        _window = window;
        _gl = GL.GetApi(window);
        _input = window.CreateInput();
        _imgui = new ImGuiController(_gl, window, _input);

        // Enable docking
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;

        ApplyDarkTheme();

        _receiver = new UdpProfilerReceiver(port);
        _renderer = new ProfilerPanelRenderer(_receiver);
        _receiver.Start();

        _window.FramebufferResize += OnFramebufferResize;
        OnFramebufferResize(_window.FramebufferSize);

        Console.WriteLine($"[Profiler] Listening on UDP port {port}");
    }

    public void Update(float deltaSeconds)
    {
        _imgui.Update(deltaSeconds);
    }

    public void Render(float deltaSeconds)
    {
        _gl.ClearColor(0.1f, 0.1f, 0.1f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        // Process latest UDP data before drawing
        _renderer.ProcessLatestData();

        DrawUI();

        _imgui.Render();
    }

    public void Dispose()
    {
        _window.FramebufferResize -= OnFramebufferResize;
        _receiver.Dispose();
        _imgui.Dispose();
        _input.Dispose();
        _gl.Dispose();
    }

    private void OnFramebufferResize(Vector2D<int> size)
    {
        int width = Math.Max(size.X, 1);
        int height = Math.Max(size.Y, 1);
        _gl.Viewport(0, 0, (uint)width, (uint)height);

        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(width, height);

        var windowSize = _window.Size;
        float scaleX = windowSize.X > 0 ? (float)width / windowSize.X : 1.0f;
        float scaleY = windowSize.Y > 0 ? (float)height / windowSize.Y : 1.0f;
        io.DisplayFramebufferScale = new Vector2(scaleX, scaleY);
    }

    // ─────────────────── ImGui UI ───────────────────

    private void DrawUI()
    {
        DrawMainMenuBar();
        DrawDockSpace();

        _renderer.DrawCorePanelsWithConnectionInfo(
            ref _showSettings,
            ref _showProfilerTree,
            ref _showFpsDropSpikes,
            ref _showRenderStats,
            ref _showGpuPipeline,
            ref _showThreadAllocations,
            ref _showComponentTimings,
            ref _showBvhMetrics,
            ref _showJobSystem,
            ref _showMainThreadInvokes,
            ref _showConnectionInfo);

        // Show centered "waiting for data" overlay when not connected
        if (!_receiver.IsConnected)
            DrawWaitingOverlay();
    }

    // ─────────────────── Menu Bar ───────────────────

    private void DrawMainMenuBar()
    {
        if (!ImGui.BeginMainMenuBar())
            return;

        if (ImGui.BeginMenu("View"))
        {
            ImGui.MenuItem("Profiler Tree", null, ref _showProfilerTree);
            ImGui.MenuItem("FPS Drop Spikes", null, ref _showFpsDropSpikes);
            ImGui.MenuItem("Render Stats", null, ref _showRenderStats);
            ImGui.MenuItem("GPU Pipeline", null, ref _showGpuPipeline);
            ImGui.MenuItem("Thread Allocations", null, ref _showThreadAllocations);
            ImGui.MenuItem("Component Timings", null, ref _showComponentTimings);
            ImGui.MenuItem("BVH Metrics", null, ref _showBvhMetrics);
            ImGui.MenuItem("Job System", null, ref _showJobSystem);
            ImGui.MenuItem("Main Thread Invokes", null, ref _showMainThreadInvokes);
            ImGui.Separator();
            ImGui.MenuItem("Settings", null, ref _showSettings);
            ImGui.MenuItem("Connection Info", null, ref _showConnectionInfo);
            ImGui.Separator();
            if (ImGui.MenuItem("Reset Layout"))
                _dockLayoutInitialized = false;
            ImGui.EndMenu();
        }

        // Connection status indicator on the right side of the menu bar
        bool connected = _receiver.IsConnected;
        double hbAge = _receiver.SecondsSinceLastHeartbeat;
        string statusText;
        Vector4 statusColor;
        if (connected)
        {
            statusText = " CONNECTED ";
            statusColor = new Vector4(0.2f, 0.9f, 0.2f, 1.0f);
        }
        else if (hbAge == double.MaxValue)
        {
            statusText = " WAITING... ";
            statusColor = new Vector4(0.9f, 0.9f, 0.2f, 1.0f);
        }
        else
        {
            statusText = $" LOST ({hbAge:F0}s) ";
            statusColor = new Vector4(0.9f, 0.3f, 0.3f, 1.0f);
        }

        float windowWidth = ImGui.GetWindowWidth();
        float textWidth = ImGui.CalcTextSize(statusText).X;
        ImGui.SameLine(windowWidth - textWidth - 16);
        ImGui.TextColored(statusColor, statusText);

        ImGui.EndMainMenuBar();
    }

    // ─────────────────── Waiting Overlay ───────────────────

    private void DrawWaitingOverlay()
    {
        var viewport = ImGui.GetMainViewport();
        float menuBarHeight = ImGui.GetFrameHeight();
        var pos = new Vector2(viewport.Pos.X, viewport.Pos.Y + menuBarHeight);
        var size = new Vector2(viewport.Size.X, viewport.Size.Y - menuBarHeight);

        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(size);
        ImGui.SetNextWindowViewport(viewport.ID);
        ImGui.SetNextWindowBgAlpha(0.65f);

        ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoInputs;

        ImGui.Begin("##WaitingOverlay", flags);

        // Animated dots: cycle 1-3 dots every ~0.5s each
        double secs = _receiver.SecondsSinceLastHeartbeat;
        int dotCount = ((int)(ImGui.GetTime() / 0.5f) % 3) + 1;
        string dots = new('.', dotCount);

        string title;
        string subtitle;
        if (secs == double.MaxValue)
        {
            title = $"Waiting for engine data{dots}";
            subtitle = "Start the engine with  XRE_PROFILER_ENABLED=1  to begin profiling.";
        }
        else
        {
            title = $"Reconnecting{dots}";
            subtitle = $"Engine heartbeat lost {secs:F0}s ago. Waiting for reconnection...";
        }

        // Center the text
        var titleSize = ImGui.CalcTextSize(title);
        var subtitleSize = ImGui.CalcTextSize(subtitle);
        float centerX = pos.X + size.X * 0.5f;
        float centerY = pos.Y + size.Y * 0.5f;

        ImGui.SetCursorScreenPos(new Vector2(centerX - titleSize.X * 0.5f, centerY - 20f));
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1.0f), title);

        ImGui.SetCursorScreenPos(new Vector2(centerX - subtitleSize.X * 0.5f, centerY + 10f));
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.55f, 1.0f), subtitle);

        ImGui.End();
    }

    // ─────────────────── Dock Space ───────────────────

    private void DrawDockSpace()
    {
        var viewport = ImGui.GetMainViewport();
        float menuBarHeight = ImGui.GetFrameHeight();

        ImGui.SetNextWindowPos(new Vector2(viewport.Pos.X, viewport.Pos.Y + menuBarHeight));
        ImGui.SetNextWindowSize(new Vector2(viewport.Size.X, viewport.Size.Y - menuBarHeight));
        ImGui.SetNextWindowViewport(viewport.ID);

        ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus |
            ImGuiWindowFlags.NoBackground;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        ImGui.Begin("DockSpaceWindow", flags);
        ImGui.PopStyleVar(3);

        uint dockSpaceId = ImGui.GetID("ProfilerDockSpace");
        ImGui.DockSpace(dockSpaceId, Vector2.Zero, ImGuiDockNodeFlags.PassthruCentralNode);

        if (!_dockLayoutInitialized)
        {
            InitializeDefaultDockingLayout(dockSpaceId);
            _dockLayoutInitialized = true;
        }

        ImGui.End();
    }

    private static void InitializeDefaultDockingLayout(uint dockSpaceId)
    {
        var viewport = ImGui.GetMainViewport();
        float availableWidth = viewport.Size.X;
        float availableHeight = viewport.Size.Y - ImGui.GetFrameHeight();

        ImGuiDockBuilderNative.RemoveNode(dockSpaceId);
        ImGuiDockBuilderNative.AddNode(dockSpaceId, ImGuiDockNodeFlags.PassthruCentralNode);
        ImGuiDockBuilderNative.SetNodeSize(dockSpaceId, new Vector2(availableWidth, availableHeight));

        // Split: Left (Profiler Tree + FPS Spikes 55%) | Right (stats panels)
        ImGuiDockBuilderNative.SplitNode(dockSpaceId, ImGuiDir.Left, 0.55f,
            out uint leftDockId, out uint rightDockId);

        // Split left: Top (Profiler Tree 70%) | Bottom (FPS Drop Spikes)
        ImGuiDockBuilderNative.SplitNode(leftDockId, ImGuiDir.Down, 0.30f,
            out uint leftBottomId, out uint leftTopId);

        // Split right: Top-right (Render Stats) | Bottom-right (rest)
        ImGuiDockBuilderNative.SplitNode(rightDockId, ImGuiDir.Up, 0.35f,
            out uint rightTopId, out uint rightBottomId);

        // Split bottom-right: Mid (Thread Alloc + BVH) | Bottom (Job + Invokes)
        ImGuiDockBuilderNative.SplitNode(rightBottomId, ImGuiDir.Up, 0.50f,
            out uint rightMidId, out uint rightLowerId);

        ImGuiDockBuilderNative.DockWindow("Profiler Tree", leftTopId);
        ImGuiDockBuilderNative.DockWindow("FPS Drop Spikes", leftBottomId);
        ImGuiDockBuilderNative.DockWindow("Render Stats", rightTopId);
        ImGuiDockBuilderNative.DockWindow("GPU Pipeline", rightTopId);
        ImGuiDockBuilderNative.DockWindow("Thread Allocations", rightMidId);
        ImGuiDockBuilderNative.DockWindow("Component Timings", rightMidId);
        ImGuiDockBuilderNative.DockWindow("BVH Metrics", rightMidId);   // tabbed
        ImGuiDockBuilderNative.DockWindow("Job System", rightLowerId);
        ImGuiDockBuilderNative.DockWindow("Main Thread Invokes", rightLowerId); // tabbed
        ImGuiDockBuilderNative.DockWindow("Connection Info", rightLowerId);     // tabbed
        ImGuiDockBuilderNative.DockWindow("Profiler Settings", leftBottomId);   // tabbed with FPS Spikes

        ImGuiDockBuilderNative.Finish(dockSpaceId);
    }

    // ─────────────────── Theme ───────────────────

    /// <summary>
    /// Applies the same professional styling used by the in-editor profiler
    /// (<c>EnsureProfessionalImGuiStyling</c>) so both profiler UIs look identical.
    /// </summary>
    private static void ApplyDarkTheme()
    {
        var style = ImGui.GetStyle();
        style.WindowRounding = 8.0f;
        style.FrameRounding = 6.0f;
        style.GrabRounding = 6.0f;
        style.TabRounding = 6.0f;
        style.ScrollbarRounding = 8.0f;
        style.WindowBorderSize = 1.0f;
        style.FrameBorderSize = 1.0f;
        style.TabBorderSize = 1.0f;
        style.WindowPadding = new Vector2(14.0f, 10.0f);
        style.FramePadding = new Vector2(10.0f, 6.0f);
        style.ItemSpacing = new Vector2(10.0f, 8.0f);
        style.ItemInnerSpacing = new Vector2(6.0f, 4.0f);
        style.ScrollbarSize = 16.0f;
        style.GrabMinSize = 12.0f;

        var colors = style.Colors;
        Vector4 darkBg = new(0.12f, 0.13f, 0.15f, 1.00f);
        Vector4 midBg = new(0.18f, 0.19f, 0.22f, 1.00f);
        Vector4 hoverBg = new(0.24f, 0.26f, 0.30f, 1.00f);
        Vector4 activeBg = new(0.32f, 0.35f, 0.40f, 1.00f);
        Vector4 accent = new(0.15f, 0.55f, 0.95f, 1.00f);
        Vector4 accentHover = new(0.25f, 0.65f, 1.00f, 1.00f);
        Vector4 accentActive = new(0.10f, 0.45f, 0.85f, 1.00f);
        Vector4 textPrimary = new(0.92f, 0.94f, 0.97f, 1.00f);
        Vector4 textMuted = new(0.62f, 0.66f, 0.72f, 1.00f);

        colors[(int)ImGuiCol.Text] = textPrimary;
        colors[(int)ImGuiCol.TextDisabled] = textMuted;
        colors[(int)ImGuiCol.WindowBg] = darkBg;
        colors[(int)ImGuiCol.ChildBg] = new Vector4(darkBg.X, darkBg.Y, darkBg.Z, 0.75f);
        colors[(int)ImGuiCol.PopupBg] = new Vector4(0.10f, 0.11f, 0.13f, 0.98f);
        colors[(int)ImGuiCol.Border] = new Vector4(0.20f, 0.22f, 0.27f, 1.00f);
        colors[(int)ImGuiCol.BorderShadow] = new Vector4(0f, 0f, 0f, 0f);
        colors[(int)ImGuiCol.FrameBg] = midBg;
        colors[(int)ImGuiCol.FrameBgHovered] = hoverBg;
        colors[(int)ImGuiCol.FrameBgActive] = activeBg;
        colors[(int)ImGuiCol.TitleBg] = midBg;
        colors[(int)ImGuiCol.TitleBgActive] = hoverBg;
        colors[(int)ImGuiCol.TitleBgCollapsed] = new Vector4(midBg.X, midBg.Y, midBg.Z, 0.60f);
        colors[(int)ImGuiCol.MenuBarBg] = midBg;
        colors[(int)ImGuiCol.ScrollbarBg] = new Vector4(darkBg.X, darkBg.Y, darkBg.Z, 1.00f);
        colors[(int)ImGuiCol.ScrollbarGrab] = hoverBg;
        colors[(int)ImGuiCol.ScrollbarGrabHovered] = activeBg;
        colors[(int)ImGuiCol.ScrollbarGrabActive] = accentActive;
        colors[(int)ImGuiCol.CheckMark] = accent;
        colors[(int)ImGuiCol.SliderGrab] = accent;
        colors[(int)ImGuiCol.SliderGrabActive] = accentActive;
        colors[(int)ImGuiCol.Button] = midBg;
        colors[(int)ImGuiCol.ButtonHovered] = accentHover;
        colors[(int)ImGuiCol.ButtonActive] = accentActive;
        colors[(int)ImGuiCol.Header] = midBg;
        colors[(int)ImGuiCol.HeaderHovered] = hoverBg;
        colors[(int)ImGuiCol.HeaderActive] = activeBg;
        colors[(int)ImGuiCol.Separator] = new Vector4(0.28f, 0.30f, 0.34f, 1.00f);
        colors[(int)ImGuiCol.SeparatorHovered] = accentHover;
        colors[(int)ImGuiCol.SeparatorActive] = accent;
        colors[(int)ImGuiCol.ResizeGrip] = hoverBg;
        colors[(int)ImGuiCol.ResizeGripHovered] = accentHover;
        colors[(int)ImGuiCol.ResizeGripActive] = accentActive;
        colors[(int)ImGuiCol.Tab] = midBg;
        colors[(int)ImGuiCol.TabHovered] = hoverBg;
        colors[(int)ImGuiCol.TabActive] = activeBg;
        colors[(int)ImGuiCol.TabUnfocused] = new Vector4(midBg.X, midBg.Y, midBg.Z, 0.90f);
        colors[(int)ImGuiCol.TabUnfocusedActive] = hoverBg;
        colors[(int)ImGuiCol.DockingPreview] = new Vector4(accent.X, accent.Y, accent.Z, 0.35f);
        colors[(int)ImGuiCol.DockingEmptyBg] = new Vector4(0.08f, 0.08f, 0.09f, 1.00f);
        colors[(int)ImGuiCol.PlotLines] = accent;
        colors[(int)ImGuiCol.PlotLinesHovered] = accentHover;
        colors[(int)ImGuiCol.PlotHistogram] = new Vector4(accent.X, accent.Y, accent.Z, 0.70f);
        colors[(int)ImGuiCol.PlotHistogramHovered] = accentHover;
        colors[(int)ImGuiCol.TableHeaderBg] = midBg;
        colors[(int)ImGuiCol.TableBorderStrong] = new Vector4(0.20f, 0.22f, 0.27f, 1.00f);
        colors[(int)ImGuiCol.TableBorderLight] = new Vector4(0.16f, 0.18f, 0.21f, 1.00f);
        colors[(int)ImGuiCol.TextSelectedBg] = new Vector4(accent.X, accent.Y, accent.Z, 0.35f);
        colors[(int)ImGuiCol.DragDropTarget] = new Vector4(accent.X, accent.Y, accent.Z, 0.90f);
        colors[(int)ImGuiCol.NavHighlight] = accent;
        colors[(int)ImGuiCol.NavWindowingHighlight] = new Vector4(accent.X, accent.Y, accent.Z, 0.70f);
        colors[(int)ImGuiCol.NavWindowingDimBg] = new Vector4(0.05f, 0.05f, 0.05f, 0.60f);
        colors[(int)ImGuiCol.ModalWindowDimBg] = new Vector4(0.05f, 0.05f, 0.05f, 0.45f);
    }
}
