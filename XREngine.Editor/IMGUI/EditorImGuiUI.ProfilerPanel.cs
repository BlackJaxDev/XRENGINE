using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using XREngine.Profiler.UI;

namespace XREngine.Editor;

/// <summary>
/// Thin wrapper around the shared <see cref="ProfilerPanelRenderer"/>.
/// Uses <see cref="EngineProfilerDataSource"/> for in-process engine data.
/// Provides F11 toggle, UDP-sending toggle, and a button to launch the
/// external profiler application.
/// </summary>
public static partial class EditorImGuiUI
{
    private const string ProfilerDockSpaceWindowId = "Profiler Dockspace";
    private const string ProfilerDockSpaceId = "ProfilerDockSpace";

    internal static void SetProfilerVisible(bool visible)
    {
        bool changed = _showProfiler != visible;
        _showProfiler = visible;

        if (visible && changed)
            RequestProfilerDockLayoutReset();
    }

    internal static void ToggleProfilerVisible()
    {
        _showProfiler = !_showProfiler;
        if (_showProfiler)
            RequestProfilerDockLayoutReset();
    }

    internal static void RenderProfilerOverlay()
    {
        if (!_showProfiler)
            return;

        EnsureProfessionalImGuiStyling();
        DrawProfilerPanel();
    }

    // Shared renderer + data source (created on first use)
    private static EngineProfilerDataSource? _engineProfilerDataSource;
    private static ProfilerPanelRenderer? _engineProfilerRenderer;

    // Panel visibility toggles (controlled by _showProfiler in ImGui.cs)
    private static bool _showProfilerTree = true;
    private static bool _showFpsDropSpikes = true;
    private static bool _showRenderStats = true;
    private static bool _showThreadAllocations = true;
    private static bool _showBvhMetrics = true;
    private static bool _showJobSystem = true;
    private static bool _showMainThreadInvokes = true;
    private static bool _profilerDockLayoutInitialized;
    private static bool _profilerDockLayoutRequested;

    /// <summary>Whether UDP profiler sending is active (disables in-editor panels).</summary>
    private static bool _profilerUdpEnabled;

    private static void EnsureProfilerInitialized()
    {
        if (_engineProfilerDataSource is not null) return;
        _engineProfilerDataSource = new EngineProfilerDataSource();
        _engineProfilerRenderer = new ProfilerPanelRenderer(_engineProfilerDataSource);
    }

    /// <summary>
    /// Main entry point called from <see cref="RenderEditor"/>.
    /// Draws the in-editor profiler window when <c>_showProfiler</c> is true.
    /// </summary>
    private static void DrawProfilerPanel()
    {
        if (!_showProfiler) return;

        EnsureProfilerInitialized();
        DrawProfilerDockSpace();

        // If UDP sending is active, show a thin notice instead of the full panels.
        if (_profilerUdpEnabled)
        {
            if (!ImGui.Begin("Profiler"))
            {
                ImGui.End();
                return;
            }

            ImGui.TextColored(new Vector4(0.3f, 0.85f, 1f, 1f),
                "UDP profiler sending is active — use the external profiler for live telemetry.");
            ImGui.Spacing();

            DrawLaunchExternalProfilerButton();

            ImGui.Spacing();
            if (ImGui.Button("Disable UDP Sending"))
            {
                _profilerUdpEnabled = false;
                Engine.EditorPreferences.Debug.EnableProfilerUdpSending = false;
            }

            ImGui.End();
            return;
        }

        // ── Normal in-editor profiler ──

        // Collect fresh data from engine statics
        _engineProfilerDataSource!.CollectFromEngine();
        _engineProfilerRenderer!.ProcessLatestData();

        // Controls / toggles window
        if (ImGui.Begin("Profiler"))
        {
            // Engine-specific toggles (these are not in the shared renderer)
            bool enableFrameLogging = Engine.EditorPreferences.Debug.EnableProfilerFrameLogging;
            if (ImGui.Checkbox("Enable Frame Logging", ref enableFrameLogging))
                Engine.EditorPreferences.Debug.EnableProfilerFrameLogging = enableFrameLogging;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("When disabled, profiler method timing is skipped to reduce overhead.");

            ImGui.SameLine();
            bool enableStatsTracking = Engine.EditorPreferences.Debug.EnableRenderStatisticsTracking;
            if (ImGui.Checkbox("Enable Stats Tracking", ref enableStatsTracking))
                Engine.EditorPreferences.Debug.EnableRenderStatisticsTracking = enableStatsTracking;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("When disabled, per-frame render statistics (draw calls, triangles) are not tracked.");

            ImGui.SameLine();
            bool enableAllocTracking = Engine.EditorPreferences.Debug.EnableThreadAllocationTracking;
            if (ImGui.Checkbox("Enable Alloc Tracking", ref enableAllocTracking))
                Engine.EditorPreferences.Debug.EnableThreadAllocationTracking = enableAllocTracking;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("When disabled, GC allocation deltas are not measured per tick/frame.");

            ImGui.Separator();

            // UDP toggle
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "External Profiler:");
            ImGui.SameLine();
            bool udpEnabled = _profilerUdpEnabled;
            if (ImGui.Checkbox("Enable UDP Sending", ref udpEnabled))
            {
                _profilerUdpEnabled = udpEnabled;
                Engine.EditorPreferences.Debug.EnableProfilerUdpSending = udpEnabled;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Send profiler telemetry via UDP to the external XREngine.Profiler application.\nWhen enabled, this in-editor profiler panel is disabled to avoid duplicate overhead.");

            ImGui.SameLine();
            DrawLaunchExternalProfilerButton();

            ImGui.Separator();

            // Sub-panel visibility toggles
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "Panels:");
            ImGui.SameLine(); ImGui.Checkbox("Tree", ref _showProfilerTree);
            ImGui.SameLine(); ImGui.Checkbox("Spikes", ref _showFpsDropSpikes);
            ImGui.SameLine(); ImGui.Checkbox("Render", ref _showRenderStats);
            ImGui.SameLine(); ImGui.Checkbox("Allocs", ref _showThreadAllocations);
            ImGui.SameLine(); ImGui.Checkbox("BVH", ref _showBvhMetrics);
            ImGui.SameLine(); ImGui.Checkbox("Jobs", ref _showJobSystem);
            ImGui.SameLine(); ImGui.Checkbox("Invokes", ref _showMainThreadInvokes);
        }
        ImGui.End();

        // Draw shared profiler panels
        if (_showProfilerTree) _engineProfilerRenderer!.DrawProfilerTreePanel(ref _showProfilerTree, allowClose: false);
        if (_showFpsDropSpikes) _engineProfilerRenderer!.DrawFpsDropSpikesPanel(ref _showFpsDropSpikes, allowClose: false);
        if (_showRenderStats) _engineProfilerRenderer!.DrawRenderStatsPanel(ref _showRenderStats, allowClose: false);
        if (_showThreadAllocations) _engineProfilerRenderer!.DrawThreadAllocationsPanel(ref _showThreadAllocations, allowClose: false);
        if (_showBvhMetrics) _engineProfilerRenderer!.DrawBvhMetricsPanel(ref _showBvhMetrics, allowClose: false);
        if (_showJobSystem) _engineProfilerRenderer!.DrawJobSystemPanel(ref _showJobSystem, allowClose: false);
        if (_showMainThreadInvokes) _engineProfilerRenderer!.DrawMainThreadInvokesPanel(ref _showMainThreadInvokes, allowClose: false);
    }

    private static void RequestProfilerDockLayoutReset()
    {
        _profilerDockLayoutInitialized = false;
        _profilerDockLayoutRequested = true;
    }

    private static void DrawProfilerDockSpace()
    {
        var viewport = ImGui.GetMainViewport();
        Vector2 defaultSize = new(viewport.Size.X * 0.78f, viewport.Size.Y * 0.78f);
        Vector2 defaultPos = new(
            viewport.Pos.X + (viewport.Size.X - defaultSize.X) * 0.5f,
            viewport.Pos.Y + (viewport.Size.Y - defaultSize.Y) * 0.5f);

        ImGui.SetNextWindowSize(defaultSize, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(defaultPos, ImGuiCond.FirstUseEver);

        ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse;

        ImGui.Begin(ProfilerDockSpaceWindowId, flags);

        uint dockSpaceId = ImGui.GetID(ProfilerDockSpaceId);
        ImGui.DockSpace(dockSpaceId, Vector2.Zero, ImGuiDockNodeFlags.PassthruCentralNode);

        if (_profilerDockLayoutRequested || !_profilerDockLayoutInitialized)
        {
            InitializeProfilerDockingLayout(dockSpaceId, ImGui.GetWindowSize());
            _profilerDockLayoutInitialized = true;
            _profilerDockLayoutRequested = false;
        }

        ImGui.End();
    }

    private static void InitializeProfilerDockingLayout(uint dockSpaceId, Vector2 dockSize)
    {
        float availableWidth = dockSize.X;
        float availableHeight = dockSize.Y;

        ImGuiDockBuilderNative.RemoveNode(dockSpaceId);
        ImGuiDockBuilderNative.AddNode(dockSpaceId, ImGuiDockNodeFlags.PassthruCentralNode);
        ImGuiDockBuilderNative.SetNodeSize(dockSpaceId, new Vector2(availableWidth, availableHeight));

        ImGuiDockBuilderNative.SplitNode(dockSpaceId, ImGuiDir.Left, 0.55f,
            out uint leftDockId, out uint rightDockId);

        ImGuiDockBuilderNative.SplitNode(leftDockId, ImGuiDir.Down, 0.30f,
            out uint leftBottomId, out uint leftTopId);

        ImGuiDockBuilderNative.SplitNode(rightDockId, ImGuiDir.Up, 0.35f,
            out uint rightTopId, out uint rightBottomId);

        ImGuiDockBuilderNative.SplitNode(rightBottomId, ImGuiDir.Up, 0.50f,
            out uint rightMidId, out uint rightLowerId);

        ImGuiDockBuilderNative.DockWindow("Profiler", rightTopId);
        ImGuiDockBuilderNative.DockWindow("Profiler Tree", leftTopId);
        ImGuiDockBuilderNative.DockWindow("FPS Drop Spikes", leftBottomId);
        ImGuiDockBuilderNative.DockWindow("Render Stats", rightTopId);
        ImGuiDockBuilderNative.DockWindow("Thread Allocations", rightMidId);
        ImGuiDockBuilderNative.DockWindow("BVH Metrics", rightMidId);
        ImGuiDockBuilderNative.DockWindow("Job System", rightLowerId);
        ImGuiDockBuilderNative.DockWindow("Main Thread Invokes", rightLowerId);

        ImGuiDockBuilderNative.Finish(dockSpaceId);
    }

    private static void DrawLaunchExternalProfilerButton()
    {
        if (ImGui.Button("Launch External Profiler"))
        {
            try
            {
                string? profilerExe = FindExternalProfilerExe();
                if (!string.IsNullOrWhiteSpace(profilerExe) && File.Exists(profilerExe))
                {
                    Process.Start(new ProcessStartInfo(profilerExe!)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(profilerExe) ?? ".",
                    });
                }
                else
                {
                    Console.Error.WriteLine($"[Profiler] External profiler not found. Build XREngine.Profiler first.");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Profiler] Failed to launch external profiler: {ex.Message}");
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Launch the standalone XREngine.Profiler application.\nMake sure UDP sending is enabled to stream data to it.");
    }

    private static string? FindExternalProfilerExe()
    {
        string? editorDir = AppContext.BaseDirectory;
        var candidates = new List<string>(4);

        if (!string.IsNullOrWhiteSpace(editorDir))
        {
            candidates.Add(Path.Combine(editorDir!, "XREngine.Profiler.exe"));

            string? repoRoot = TryFindRepoRoot(editorDir!);
            if (!string.IsNullOrWhiteSpace(repoRoot))
            {
                candidates.Add(Path.Combine(repoRoot!, "XREngine.Profiler", "bin", "Debug", "net10.0-windows7.0", "XREngine.Profiler.exe"));
                candidates.Add(Path.Combine(repoRoot!, "XREngine.Profiler", "bin", "Release", "net10.0-windows7.0", "XREngine.Profiler.exe"));
            }
        }

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static string? TryFindRepoRoot(string startDir)
    {
        string? dir = startDir;
        for (int i = 0; i < 8 && !string.IsNullOrWhiteSpace(dir); i++)
        {
            string slnPath = Path.Combine(dir!, "XRENGINE.sln");
            if (File.Exists(slnPath))
                return dir;

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return null;
    }
}
