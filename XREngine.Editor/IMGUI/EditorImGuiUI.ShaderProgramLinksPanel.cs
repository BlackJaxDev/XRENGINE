using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using static XREngine.Rendering.XRRenderProgram;
using LinkSnapshot = XREngine.Rendering.OpenGL.OpenGLRenderer.GLRenderProgram.LinkDiagnosticsSnapshot;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static bool _showShaderProgramLinks;
    private static bool _shaderProgramLinksFrozen;
    private static bool _shaderProgramLinksFilterPendingOnly;
    private static bool _shaderProgramLinksFilterLinkedOnly;
    private static bool _shaderProgramLinksFilterFailedOnly;
    private static bool _shaderProgramLinksGrouped = true;
    private static bool _shaderProgramLinksSortDescending = true;
    private static string _shaderProgramLinksSearch = string.Empty;
    private static ShaderProgramLinksSortMode _shaderProgramLinksSortMode = ShaderProgramLinksSortMode.State;
    private static OpenGLRenderer.GLRenderProgram? _selectedShaderProgramLinkProgram;
    private const int ShaderProgramLinksDefaultSampleIntervalIndex = 4;
    private const int ShaderProgramLinksMaxRowsPerPassiveCapture = 2048;
    private static readonly double[] _shaderProgramLinksSampleIntervalsSeconds = [0.10, 0.25, 0.50, 1.00, 2.00];
    private static readonly string[] _shaderProgramLinksSampleIntervalLabels = ["100 ms", "250 ms", "500 ms", "1 s", "2 s"];
    private static int _shaderProgramLinksSampleIntervalIndex = ShaderProgramLinksDefaultSampleIntervalIndex;
    private static long _shaderProgramLinksLastCaptureTimestamp;
    private static double _shaderProgramLinksLastCaptureMilliseconds;
    private static int _shaderProgramLinksLastCaptureSkippedRows;
    private static bool _shaderProgramLinksSortDirty;
    private static bool _shaderProgramLinksViewDirty = true;
    private static IReadOnlyList<ShaderProgramLinkRow>? _shaderProgramLinksViewSource;
    private static ShaderProgramLinksSummary _shaderProgramLinksSummary;
    private static ShaderProgramLinkRow? _shaderProgramLinksSelectedRow;
    private static ShaderProgramLinkGroup? _shaderProgramLinksSelectedGroup;
    private static string? _selectedShaderProgramLinkGroupKey;
    private static readonly List<ShaderProgramLinkRow> _shaderProgramLinkRows = [];
    private static readonly List<ShaderProgramLinkRow> _frozenShaderProgramLinkRows = [];
    private static readonly List<ShaderProgramLinkRow> _shaderProgramLinkVisibleRows = [];
    private static readonly List<ShaderProgramLinkGroup> _shaderProgramLinkVisibleGroups = [];

    private enum ShaderProgramLinksSortMode
    {
        State,
        Refs,
        Program,
        Backend,
        QueueLatency,
        LinkTime,
        BinaryTime,
        Hash,
    }

    private sealed record ShaderProgramLinkRow(
        string WindowTitle,
        string RendererName,
        string XrName,
        OpenGLRenderer Renderer,
        OpenGLRenderer.GLRenderProgram Program,
        LinkSnapshot Snapshot,
        bool IsPending,
        bool IsPreparedOnly,
        bool IsQueued,
        bool IsFailed,
        bool UsesDriverParallel,
        bool UsesSharedContext,
        bool UsesBinaryCache,
        bool UsesSynchronousRenderThread,
        int StateSortRank,
        string ProgramName,
        string ProgramNameCell,
        string ProgramUse,
        string ProgramUseCell,
        string ShaderSourceCell,
        string StatusText,
        Vector4 StatusColor,
        string LinkType,
        string BackendText,
        string StageText,
        ulong DisplayHash,
        string HashText,
        string ShaderStagesCell,
        string FlagsText,
        string QueueText,
        string PendingText,
        string TimingText,
        string DetailText);

    private sealed class ShaderProgramLinkGroup(string key, ShaderProgramLinkRow representative)
    {
        public string Key { get; } = key;
        public ShaderProgramLinkRow Representative { get; private set; } = representative;
        public List<ShaderProgramLinkRow> Rows { get; } = [];
        public int Pending { get; private set; }
        public int Linked { get; private set; }
        public int Failed { get; private set; }
        public int Prepared { get; private set; }
        public int SharedHandleReferences { get; private set; }
        public uint SharedProgramId { get; private set; }

        public int LogicalReferences => Rows.Count;

        public void Add(ShaderProgramLinkRow row)
        {
            Rows.Add(row);
            if (row.StateSortRank > Representative.StateSortRank)
                Representative = row;
            if (row.IsPending)
                Pending++;
            if (row.Snapshot.IsLinked)
                Linked++;
            if (row.IsFailed)
                Failed++;
            if (row.IsPreparedOnly)
                Prepared++;
            if (row.Snapshot.SharedLinkedProgramReferenceCount > SharedHandleReferences)
                SharedHandleReferences = row.Snapshot.SharedLinkedProgramReferenceCount;
            if (SharedProgramId == 0 && row.Snapshot.SharedLinkedProgramId != 0)
                SharedProgramId = row.Snapshot.SharedLinkedProgramId;
        }
    }

    private readonly record struct ShaderProgramLinksSummary(
        int ProgramCount,
        int VisibleCount,
        int GroupCount,
        int UniqueEffectiveHashes,
        int UniqueBinaryKeys,
        int SharedHandleReferences,
        long PendingDestructions,
        int Pending,
        int Prepared,
        int Queued,
        int Linked,
        int Failed,
        int DriverParallel,
        int SharedContext,
        int BinaryCache,
        int Synchronous);

    private static void DrawShaderProgramLinksPanel()
    {
        if (!_showShaderProgramLinks)
            return;

        if (!ImGui.Begin("Shader Program Links", ref _showShaderProgramLinks, ImGuiWindowFlags.MenuBar))
        {
            ImGui.End();
            return;
        }

        if (ImGui.BeginMenuBar())
        {
            if (ImGui.MenuItem(_shaderProgramLinksFrozen ? "Unfreeze" : "Freeze"))
            {
                _shaderProgramLinksFrozen = !_shaderProgramLinksFrozen;
                if (_shaderProgramLinksFrozen)
                {
                    if (_shaderProgramLinkRows.Count == 0)
                        CaptureShaderProgramLinkRows(_shaderProgramLinkRows);
                    CopyShaderProgramLinkRows(_shaderProgramLinkRows, _frozenShaderProgramLinkRows);
                    MarkShaderProgramLinksViewDirty();
                }
                else
                {
                    MarkShaderProgramLinksViewDirty();
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Freeze keeps the current diagnostic snapshot visible while links continue in the renderer.");

            if (ImGui.MenuItem("Refresh Now"))
            {
                CaptureShaderProgramLinkRows(_shaderProgramLinksFrozen ? _frozenShaderProgramLinkRows : _shaderProgramLinkRows);
                MarkShaderProgramLinksViewDirty();
            }

            if (ImGui.MenuItem("Log Summary"))
                LogShaderProgramLifecycleSummary();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Writes ShaderProgramSummary counters to the OpenGL log for each active OpenGL renderer.");

            ImGui.EndMenuBar();
        }

        List<ShaderProgramLinkRow> rows;
        if (_shaderProgramLinksFrozen)
        {
            rows = _frozenShaderProgramLinkRows;
        }
        else
        {
            CaptureShaderProgramLinkRowsIfDue();
            rows = _shaderProgramLinkRows;
        }

        bool compactLayout = UseCompactShaderProgramLinksLayout();
        DrawShaderProgramLinksFilters(compactLayout);
        if (_shaderProgramLinksSortDirty)
        {
            SortShaderProgramLinkRows(rows);
            _shaderProgramLinksSortDirty = false;
            MarkShaderProgramLinksViewDirty();
        }

        EnsureShaderProgramLinksView(rows);
        ImGui.Separator();
        DrawShaderProgramLinksSummary(_shaderProgramLinksSummary, compactLayout);

        if (_shaderProgramLinksGrouped && _shaderProgramLinksSelectedGroup is { } selectedGroup)
        {
            ImGui.Separator();
            DrawShaderProgramLinkGroupDetails(selectedGroup);
        }
        else if (_shaderProgramLinksSelectedRow is { } selectedRow)
        {
            ImGui.Separator();
            DrawShaderProgramLinkDetails(selectedRow);
        }

        ImGui.Separator();
        if (_shaderProgramLinksGrouped)
            DrawShaderProgramLinkGroupsTable(_shaderProgramLinkVisibleGroups, rows.Count, compactLayout);
        else
            DrawShaderProgramLinksTable(_shaderProgramLinkVisibleRows, rows.Count, compactLayout);

        ImGui.End();
    }

    private static void CaptureShaderProgramLinkRowsIfDue()
    {
        if (_shaderProgramLinkRows.Count == 0 || _shaderProgramLinksLastCaptureTimestamp == 0)
        {
            CaptureShaderProgramLinkRows(_shaderProgramLinkRows);
            return;
        }

        double ageSeconds = StopwatchTicksToSeconds(Stopwatch.GetTimestamp() - _shaderProgramLinksLastCaptureTimestamp);
        if (ageSeconds >= GetShaderProgramLinksSampleIntervalSeconds())
            CaptureShaderProgramLinkRows(_shaderProgramLinkRows);
    }

    private static void CaptureShaderProgramLinkRows(List<ShaderProgramLinkRow> rows)
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        rows.Clear();

        int programCount = 0;
        int pending = 0;
        int prepared = 0;
        int linked = 0;
        int failed = 0;
        int queued = 0;
        int driverParallel = 0;
        int sharedContext = 0;
        int binary = 0;
        int synchronous = 0;
        int skippedRows = 0;
        int sharedHandleReferences = 0;
        HashSet<string> groupKeys = new(StringComparer.Ordinal);
        HashSet<ulong> effectiveHashes = [];
        HashSet<string> binaryKeys = new(StringComparer.Ordinal);
        bool materializeAllRows = ShouldMaterializeAllShaderProgramLinkRows();

        foreach (XRWindow? window in Engine.Windows)
        {
            if (window?.Renderer is not OpenGLRenderer renderer)
                continue;

            string windowTitle = GetShaderProgramLinkWindowTitle(window);
            foreach (var pair in renderer.RenderObjectCache)
            {
                if (pair.Key is not XRRenderProgram xrProgram ||
                    pair.Value is not OpenGLRenderer.GLRenderProgram glProgram)
                {
                    continue;
                }

                LinkSnapshot snapshot = glProgram.GetLinkDiagnosticsSnapshot();
                programCount++;
                if (IsShaderProgramPending(snapshot))
                    pending++;
                if (IsShaderProgramPreparedOnly(snapshot))
                    prepared++;
                if (snapshot.IsLinked)
                    linked++;
                if (IsShaderProgramFailed(snapshot))
                    failed++;
                if (IsShaderProgramQueued(snapshot))
                    queued++;
                if (UsesDriverParallel(snapshot))
                    driverParallel++;
                if (UsesSharedContext(snapshot))
                    sharedContext++;
                if (UsesBinaryCache(snapshot))
                    binary++;
                if (UsesSynchronousRenderThread(snapshot))
                    synchronous++;
                string groupKey = BuildShaderProgramGroupKey(snapshot);
                if (!string.IsNullOrWhiteSpace(groupKey))
                    groupKeys.Add(groupKey);
                if (snapshot.EffectiveSourceHash != 0)
                    effectiveHashes.Add(snapshot.EffectiveSourceHash);
                string? binaryKey = snapshot.BinaryCacheKey ?? snapshot.PreparedCacheKey ?? snapshot.ActiveBuildFingerprint ?? snapshot.LastBuildFingerprint;
                if (!string.IsNullOrWhiteSpace(binaryKey))
                    binaryKeys.Add(binaryKey);
                if (snapshot.SharedLinkedProgramReferenceCount > sharedHandleReferences)
                    sharedHandleReferences = snapshot.SharedLinkedProgramReferenceCount;

                if (materializeAllRows || ShouldMaterializeShaderProgramLinkRow(snapshot, rows.Count))
                {
                    rows.Add(CreateShaderProgramLinkRow(
                        windowTitle,
                        "OpenGL",
                        xrProgram,
                        renderer,
                        glProgram,
                        snapshot));
                }
                else
                {
                    skippedRows++;
                }
            }
        }

        SortShaderProgramLinkRows(rows);
        _shaderProgramLinksSummary = new ShaderProgramLinksSummary(
            programCount,
            rows.Count,
            groupKeys.Count,
            effectiveHashes.Count,
            binaryKeys.Count,
            sharedHandleReferences,
            XRObjectBase.PendingDestructionCount,
            pending,
            prepared,
            queued,
            linked,
            failed,
            driverParallel,
            sharedContext,
            binary,
            synchronous);
        _shaderProgramLinksLastCaptureSkippedRows = skippedRows;
        MarkShaderProgramLinksViewDirty();
        long endTimestamp = Stopwatch.GetTimestamp();
        _shaderProgramLinksLastCaptureTimestamp = endTimestamp;
        _shaderProgramLinksLastCaptureMilliseconds = StopwatchTicksToMilliseconds(endTimestamp - startTimestamp);
    }

    private static bool ShouldMaterializeAllShaderProgramLinkRows()
        => _shaderProgramLinksGrouped ||
           _shaderProgramLinksFilterPendingOnly ||
           _shaderProgramLinksFilterLinkedOnly ||
           _shaderProgramLinksFilterFailedOnly ||
           !string.IsNullOrWhiteSpace(_shaderProgramLinksSearch);

    private static bool ShouldMaterializeShaderProgramLinkRow(LinkSnapshot snapshot, int materializedCount)
    {
        if (materializedCount >= ShaderProgramLinksMaxRowsPerPassiveCapture)
            return false;

        if (IsShaderProgramFailed(snapshot) || HasLiveShaderProgramWork(snapshot) || IsShaderProgramPreparedOnly(snapshot))
            return true;

        return materializedCount < 256;
    }

    private static void CopyShaderProgramLinkRows(List<ShaderProgramLinkRow> source, List<ShaderProgramLinkRow> destination)
    {
        destination.Clear();
        destination.AddRange(source);
        MarkShaderProgramLinksViewDirty();
    }

    private static ShaderProgramLinkRow CreateShaderProgramLinkRow(
        string windowTitle,
        string rendererName,
        XRRenderProgram xrProgram,
        OpenGLRenderer renderer,
        OpenGLRenderer.GLRenderProgram program,
        LinkSnapshot snapshot)
    {
        bool isPending = IsShaderProgramPending(snapshot);
        bool isQueued = IsShaderProgramQueued(snapshot);
        bool isFailed = IsShaderProgramFailed(snapshot);
        bool isPreparedOnly = IsShaderProgramPreparedOnly(snapshot);
        bool usesDriverParallel = UsesDriverParallel(snapshot);
        bool usesSharedContext = UsesSharedContext(snapshot);
        bool usesBinaryCache = UsesBinaryCache(snapshot);
        bool usesSynchronousRenderThread = UsesSynchronousRenderThread(snapshot);
        string xrName = GetShaderProgramDisplayName(xrProgram);
        string programName = ResolveShaderProgramDisplayName(xrProgram, snapshot, xrName);
        string programUse = ResolveShaderProgramUse(programName, xrProgram, snapshot);
        string shaderSource = BuildShaderProgramSourceSummary(xrProgram);
        string backendText = snapshot.ActiveBuildBackend ?? snapshot.BackendName ?? snapshot.LastBuildBackend ?? "-";
        string detailText = snapshot.BackendDetail ?? snapshot.LastBuildFailureReason ?? snapshot.BackendFailureReason ?? "-";
        ulong displayHash = snapshot.Hash != 0 ? snapshot.Hash : snapshot.PreparedHash;

        return new ShaderProgramLinkRow(
            windowTitle,
            rendererName,
            xrName,
            renderer,
            program,
            snapshot,
            isPending,
            isPreparedOnly,
            isQueued,
            isFailed,
            usesDriverParallel,
            usesSharedContext,
            usesBinaryCache,
            usesSynchronousRenderThread,
            GetShaderProgramStateSortRank(snapshot),
            programName,
            TrimForTable(programName, 80),
            programUse,
            TrimForTable(programUse, 52),
            TrimForTable(shaderSource, 72),
            GetShaderProgramStatusText(snapshot),
            GetShaderProgramStatusColor(snapshot),
            GetShaderProgramLinkType(snapshot),
            backendText,
            snapshot.BackendStage.ToString(),
            displayHash,
            displayHash == 0 ? "-" : displayHash.ToString("X16", CultureInfo.InvariantCulture),
            TrimForTable(snapshot.ShaderStages, 40),
            BuildShaderProgramFlags(snapshot),
            BuildShaderProgramQueueText(snapshot),
            BuildShaderProgramPendingText(snapshot),
            BuildShaderProgramTimingText(snapshot),
            TrimForTable(detailText, 90));
    }

    private static void SortShaderProgramLinkRows(List<ShaderProgramLinkRow> rows)
        => rows.Sort(CompareShaderProgramLinkRows);

    private static string GetShaderProgramLinkWindowTitle(XRWindow window)
    {
        try
        {
            string? title = window.WindowTitle;
            if (!string.IsNullOrWhiteSpace(title))
                return title;
        }
        catch
        {
        }

        return $"Window 0x{window.GetHashCode():X}";
    }

    private static string GetShaderProgramDisplayName(XRRenderProgram program)
        => !string.IsNullOrWhiteSpace(program.Name)
            ? program.Name!
            : program.GetType().Name;

    private static string ResolveShaderProgramDisplayName(XRRenderProgram program, LinkSnapshot snapshot, string xrName)
    {
        if (!string.IsNullOrWhiteSpace(program.Name))
            return program.Name!;

        string? telemetryName = snapshot.ProgramName;
        if (!string.IsNullOrWhiteSpace(telemetryName) && !telemetryName.StartsWith("<unnamed ", StringComparison.Ordinal))
            return telemetryName;

        string sourceSummary = BuildShaderProgramSourceSummary(program);
        if (!string.IsNullOrWhiteSpace(sourceSummary) && sourceSummary != "-")
            return string.Concat(GetShaderStageTopology(program, snapshot), ":", sourceSummary);

        return string.IsNullOrWhiteSpace(telemetryName) ? xrName : telemetryName;
    }

    private static string ResolveShaderProgramUse(string programName, XRRenderProgram program, LinkSnapshot snapshot)
    {
        // Prefer the rich UsageTag set by the program's creator (mesh renderer / material) when available.
        // It encodes which mesh variant + pass the program is for, which is what engineers want to see
        // when scanning hundreds of thousands of programs in the panel.
        if (!string.IsNullOrWhiteSpace(program.UsageTag))
            return program.UsageTag!;

        if (programName.StartsWith("MaterialPipelineVariant:", StringComparison.Ordinal))
            return "Material fragment variant";
        if (programName.StartsWith("MaterialPipeline:", StringComparison.Ordinal))
            return "Material fragment pipeline";
        if (programName.StartsWith("SeparatedVertex:", StringComparison.Ordinal))
            return "Mesh vertex pipeline";
        if (programName.StartsWith("Combined:", StringComparison.Ordinal))
            return "Mesh combined program";

        if (snapshot.ShaderStages.Contains("Compute", StringComparison.OrdinalIgnoreCase))
            return "Compute dispatch";
        if (program.Separable && snapshot.ShaderStages.Contains("Fragment", StringComparison.OrdinalIgnoreCase))
            return "Fragment pipeline stage";
        if (snapshot.ShaderStages.Contains("Vertex", StringComparison.OrdinalIgnoreCase))
            return "Vertex/mesh draw stage";
        if (snapshot.ShaderStages.Contains("Fragment", StringComparison.OrdinalIgnoreCase))
            return "Fragment draw stage";

        return "Shader program";
    }

    private static string BuildShaderProgramGroupKey(LinkSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.ProgramDescriptorKey))
            return "descriptor:" + snapshot.ProgramDescriptorKey;
        if (!string.IsNullOrWhiteSpace(snapshot.BinaryCacheKey))
            return "binary:" + snapshot.BinaryCacheKey;
        if (!string.IsNullOrWhiteSpace(snapshot.PreparedCacheKey))
            return "prepared:" + snapshot.PreparedCacheKey;
        if (!string.IsNullOrWhiteSpace(snapshot.ActiveBuildFingerprint))
            return "active:" + snapshot.ActiveBuildFingerprint;
        if (!string.IsNullOrWhiteSpace(snapshot.LastBuildFingerprint))
            return "last:" + snapshot.LastBuildFingerprint;
        if (snapshot.EffectiveSourceHash != 0)
            return "source:" + snapshot.EffectiveSourceHash.ToString("X16", CultureInfo.InvariantCulture);
        if (snapshot.Hash != 0)
            return "hash:" + snapshot.Hash.ToString("X16", CultureInfo.InvariantCulture);
        if (snapshot.PreparedHash != 0)
            return "preparedHash:" + snapshot.PreparedHash.ToString("X16", CultureInfo.InvariantCulture);

        return string.Concat(
            "program:",
            snapshot.ProgramId.ToString(CultureInfo.InvariantCulture),
            ":",
            snapshot.ShaderStages,
            ":",
            snapshot.Separable ? "sep" : "mono");
    }

    private static string BuildShaderProgramSourceSummary(XRRenderProgram program)
    {
        var builder = new StringBuilder(96);
        foreach (XRShader shader in program.Shaders)
        {
            if (shader is null)
                continue;

            if (builder.Length > 0)
                builder.Append(", ");

            builder.Append(shader.Type).Append(':').Append(GetShaderProgramShaderName(shader));
        }

        return builder.Length == 0 ? "-" : builder.ToString();
    }

    private static string GetShaderProgramShaderName(XRShader shader)
    {
        if (!string.IsNullOrWhiteSpace(shader.Name))
            return shader.Name!;
        if (!string.IsNullOrWhiteSpace(shader.FilePath))
            return Path.GetFileName(shader.FilePath);
        string? sourcePath = shader.Source?.FilePath;
        if (!string.IsNullOrWhiteSpace(sourcePath))
            return Path.GetFileName(sourcePath);
        return shader.GetType().Name;
    }

    private static string GetShaderStageTopology(XRRenderProgram program, LinkSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.ShaderStages) && snapshot.ShaderStages != "-")
            return snapshot.ShaderStages;

        EProgramStageMask mask = program.GetShaderTypeMask();
        return mask == EProgramStageMask.None ? "Unknown" : mask.ToString();
    }

    private static double GetShaderProgramLinksSampleIntervalSeconds()
    {
        int index = Math.Clamp(_shaderProgramLinksSampleIntervalIndex, 0, _shaderProgramLinksSampleIntervalsSeconds.Length - 1);
        return _shaderProgramLinksSampleIntervalsSeconds[index];
    }

    private static string GetShaderProgramLinksSampleIntervalLabel()
    {
        int index = Math.Clamp(_shaderProgramLinksSampleIntervalIndex, 0, _shaderProgramLinksSampleIntervalLabels.Length - 1);
        return _shaderProgramLinksSampleIntervalLabels[index];
    }

    private static string GetShaderProgramLinksLastCaptureAgeText()
    {
        if (_shaderProgramLinksLastCaptureTimestamp == 0)
            return "never";

        double seconds = StopwatchTicksToSeconds(Stopwatch.GetTimestamp() - _shaderProgramLinksLastCaptureTimestamp);
        if (seconds < 1.0)
            return (seconds * 1000.0).ToString("F0", CultureInfo.InvariantCulture) + " ms ago";
        if (seconds < 60.0)
            return seconds.ToString("F1", CultureInfo.InvariantCulture) + " s ago";

        return (seconds / 60.0).ToString("F1", CultureInfo.InvariantCulture) + " min ago";
    }

    private static double StopwatchTicksToMilliseconds(long ticks)
        => ticks <= 0L ? 0.0 : ticks * 1000.0 / Stopwatch.Frequency;

    private static double StopwatchTicksToSeconds(long ticks)
        => ticks <= 0L ? 0.0 : (double)ticks / Stopwatch.Frequency;

    private static bool UseCompactShaderProgramLinksLayout()
    {
        Vector2 region = ImGui.GetContentRegionAvail();
        return region.X < 1040.0f || ImGui.GetWindowWidth() < ImGui.GetWindowHeight();
    }

    private static void DrawShaderProgramLinksSummary(ShaderProgramLinksSummary summary, bool compactLayout)
    {
        if (compactLayout)
        {
            ImGui.TextUnformatted(
                $"Programs {summary.ProgramCount:N0} ({summary.VisibleCount:N0} visible) | Groups {summary.GroupCount:N0} | Pending {summary.Pending:N0} | Prepared {summary.Prepared:N0} | Queued {summary.Queued:N0} | Linked {summary.Linked:N0} | Failed {summary.Failed:N0}");
            ImGui.TextUnformatted(
                $"Identity: hashes {summary.UniqueEffectiveHashes:N0} | binary keys {summary.UniqueBinaryKeys:N0} | shared refs {summary.SharedHandleReferences:N0} | destroy queue {summary.PendingDestructions:N0}");
        }
        else
        {
            ImGui.TextUnformatted(
                $"Programs: {summary.ProgramCount:N0} | Visible: {summary.VisibleCount:N0} | Groups: {summary.GroupCount:N0} | Pending: {summary.Pending:N0} | Prepared: {summary.Prepared:N0} | Queued: {summary.Queued:N0} | Linked: {summary.Linked:N0} | Failed: {summary.Failed:N0}");
            ImGui.TextUnformatted(
                $"Identity: Effective hashes {summary.UniqueEffectiveHashes:N0} | Binary keys {summary.UniqueBinaryKeys:N0} | Peak shared handle refs {summary.SharedHandleReferences:N0} | Pending destruction queue {summary.PendingDestructions:N0}");
            ImGui.TextUnformatted(
                $"Current/last link paths: Driver source {summary.DriverParallel:N0} | Shared-context source {summary.SharedContext:N0} | Binary cache {summary.BinaryCache:N0} | Sync source {summary.Synchronous:N0}");
        }

        ImGui.TextUnformatted(
            $"Lifecycle: binary hits {ShaderProgramLifecycleDiagnostics.BinaryCacheHits:N0} | misses {ShaderProgramLifecycleDiagnostics.BinaryCacheMisses:N0} | source builds {ShaderProgramLifecycleDiagnostics.SourceBuilds:N0} | source failures {ShaderProgramLifecycleDiagnostics.SourceFailures:N0} | combined pool hit/miss {ShaderProgramLifecycleDiagnostics.CombinedProgramPoolHits:N0}/{ShaderProgramLifecycleDiagnostics.CombinedProgramPoolMisses:N0} | gpu pool hit/miss {ShaderProgramLifecycleDiagnostics.GpuDrivenProgramPoolHits:N0}/{ShaderProgramLifecycleDiagnostics.GpuDrivenProgramPoolMisses:N0}");
        if (_shaderProgramLinksLastCaptureSkippedRows > 0)
            ImGui.TextDisabled($"Rows capped: showing {summary.VisibleCount:N0}, skipped {_shaderProgramLinksLastCaptureSkippedRows:N0} low-activity rows");
        ImGui.TextDisabled(
            $"Sampling: {GetShaderProgramLinksSampleIntervalLabel()} | Last capture: {GetShaderProgramLinksLastCaptureAgeText()} ({_shaderProgramLinksLastCaptureMilliseconds:F2} ms)");
    }

    private static void DrawShaderProgramLinksFilters(bool compactLayout)
    {
        if (compactLayout)
        {
            DrawShaderProgramLinksFiltersCompact();
            return;
        }

        ImGui.SetNextItemWidth(280.0f);
        if (ImGui.InputText("Search", ref _shaderProgramLinksSearch, 256))
            MarkShaderProgramLinksViewDirty();
        SameLineForShaderProgramLinks(86.0f);
        if (ImGui.Checkbox("Pending", ref _shaderProgramLinksFilterPendingOnly))
            MarkShaderProgramLinksViewDirty();
        SameLineForShaderProgramLinks(78.0f);
        if (ImGui.Checkbox("Linked", ref _shaderProgramLinksFilterLinkedOnly))
            MarkShaderProgramLinksViewDirty();
        SameLineForShaderProgramLinks(72.0f);
        if (ImGui.Checkbox("Failed", ref _shaderProgramLinksFilterFailedOnly))
            MarkShaderProgramLinksViewDirty();

        SameLineForShaderProgramLinks(82.0f);
        if (ImGui.Checkbox("Grouped", ref _shaderProgramLinksGrouped))
            MarkShaderProgramLinksViewDirty();

        string sortLabel = GetShaderProgramLinksSortLabel();

        SameLineForShaderProgramLinks(170.0f);
        ImGui.SetNextItemWidth(150.0f);
        if (ImGui.BeginCombo("Sort", sortLabel))
        {
            DrawShaderProgramLinksSortOptions();
            ImGui.EndCombo();
        }

        SameLineForShaderProgramLinks(120.0f);
        if (ImGui.Checkbox("Descending", ref _shaderProgramLinksSortDescending))
            _shaderProgramLinksSortDirty = true;

        SameLineForShaderProgramLinks(110.0f);
        ImGui.SetNextItemWidth(95.0f);
        if (ImGui.BeginCombo("Update", GetShaderProgramLinksSampleIntervalLabel()))
            DrawShaderProgramLinksSampleIntervalOptions();
    }

    private static void DrawShaderProgramLinksFiltersCompact()
    {
        ImGui.SetNextItemWidth(-1.0f);
        if (ImGui.InputTextWithHint("##ShaderProgramLinksSearch", "Search program, use, backend, shader, hash...", ref _shaderProgramLinksSearch, 256u))
            MarkShaderProgramLinksViewDirty();

        if (ImGui.Checkbox("Pending", ref _shaderProgramLinksFilterPendingOnly))
            MarkShaderProgramLinksViewDirty();
        SameLineForShaderProgramLinks(78.0f);
        if (ImGui.Checkbox("Linked", ref _shaderProgramLinksFilterLinkedOnly))
            MarkShaderProgramLinksViewDirty();
        SameLineForShaderProgramLinks(72.0f);
        if (ImGui.Checkbox("Failed", ref _shaderProgramLinksFilterFailedOnly))
            MarkShaderProgramLinksViewDirty();

        SameLineForShaderProgramLinks(86.0f);
        if (ImGui.Checkbox("Grouped", ref _shaderProgramLinksGrouped))
            MarkShaderProgramLinksViewDirty();

        SameLineForShaderProgramLinks(132.0f);
        ImGui.SetNextItemWidth(128.0f);
        if (ImGui.BeginCombo("##ShaderProgramLinksSort", $"Sort: {GetShaderProgramLinksSortLabel()}"))
        {
            DrawShaderProgramLinksSortOptions();
            ImGui.EndCombo();
        }

        SameLineForShaderProgramLinks(62.0f);
        if (ImGui.Checkbox("Desc", ref _shaderProgramLinksSortDescending))
            _shaderProgramLinksSortDirty = true;

        SameLineForShaderProgramLinks(118.0f);
        ImGui.SetNextItemWidth(112.0f);
        if (ImGui.BeginCombo("##ShaderProgramLinksUpdate", $"Update: {GetShaderProgramLinksSampleIntervalLabel()}"))
            DrawShaderProgramLinksSampleIntervalOptions();
    }

    private static string GetShaderProgramLinksSortLabel()
        => _shaderProgramLinksSortMode switch
        {
            ShaderProgramLinksSortMode.Refs => "Refs",
            ShaderProgramLinksSortMode.Program => "Program",
            ShaderProgramLinksSortMode.Backend => "Backend",
            ShaderProgramLinksSortMode.QueueLatency => "Queue latency",
            ShaderProgramLinksSortMode.LinkTime => "Link time",
            ShaderProgramLinksSortMode.BinaryTime => "Binary time",
            ShaderProgramLinksSortMode.Hash => "Hash",
            _ => "State",
        };

    private static void DrawShaderProgramLinksSortOptions()
    {
        DrawShaderProgramLinksSortOption(ShaderProgramLinksSortMode.State, "State");
        DrawShaderProgramLinksSortOption(ShaderProgramLinksSortMode.Refs, "Refs");
        DrawShaderProgramLinksSortOption(ShaderProgramLinksSortMode.Program, "Program");
        DrawShaderProgramLinksSortOption(ShaderProgramLinksSortMode.Backend, "Backend");
        DrawShaderProgramLinksSortOption(ShaderProgramLinksSortMode.QueueLatency, "Queue latency");
        DrawShaderProgramLinksSortOption(ShaderProgramLinksSortMode.LinkTime, "Link time");
        DrawShaderProgramLinksSortOption(ShaderProgramLinksSortMode.BinaryTime, "Binary time");
        DrawShaderProgramLinksSortOption(ShaderProgramLinksSortMode.Hash, "Hash");
    }

    private static void DrawShaderProgramLinksSampleIntervalOptions()
    {
        for (int i = 0; i < _shaderProgramLinksSampleIntervalLabels.Length; i++)
        {
            bool selected = i == _shaderProgramLinksSampleIntervalIndex;
            if (ImGui.Selectable(_shaderProgramLinksSampleIntervalLabels[i], selected))
                _shaderProgramLinksSampleIntervalIndex = i;
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private static void SameLineForShaderProgramLinks(float requiredWidth)
    {
        if (ImGui.GetContentRegionAvail().X > requiredWidth)
            ImGui.SameLine(0.0f, 8.0f);
    }

    private static void DrawShaderProgramLinksSortOption(ShaderProgramLinksSortMode mode, string label)
    {
        if (ImGui.Selectable(label, _shaderProgramLinksSortMode == mode))
        {
            _shaderProgramLinksSortMode = mode;
            _shaderProgramLinksSortDirty = true;
        }
    }

    private static unsafe void DrawShaderProgramLinkGroupsTable(IReadOnlyList<ShaderProgramLinkGroup> visibleGroups, int totalRowCount, bool compactLayout)
    {
        ImGuiTableFlags flags =
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.Reorderable |
            ImGuiTableFlags.Hideable |
            ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.BordersOuter |
            ImGuiTableFlags.BordersV |
            ImGuiTableFlags.SizingStretchProp;

        float tableHeight = MathF.Max(220.0f, ImGui.GetContentRegionAvail().Y);
        int columnCount = compactLayout ? 6 : 11;
        if (!ImGui.BeginTable("ShaderProgramLinkGroupsTable", columnCount, flags, new Vector2(0.0f, tableHeight)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Group", ImGuiTableColumnFlags.None, 230.0f);
        ImGui.TableSetupColumn("Refs", ImGuiTableColumnFlags.None, 58.0f);
        ImGui.TableSetupColumn("Program", ImGuiTableColumnFlags.None, 190.0f);
        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.None, 76.0f);
        ImGui.TableSetupColumn("Link", ImGuiTableColumnFlags.None, 145.0f);
        ImGui.TableSetupColumn("Handle", ImGuiTableColumnFlags.None, 120.0f);
        if (!compactLayout)
        {
            ImGui.TableSetupColumn("Backend", ImGuiTableColumnFlags.None, 110.0f);
            ImGui.TableSetupColumn("Hash", ImGuiTableColumnFlags.None, 120.0f);
            ImGui.TableSetupColumn("Shaders", ImGuiTableColumnFlags.None, 150.0f);
            ImGui.TableSetupColumn("Timing", ImGuiTableColumnFlags.None, 170.0f);
            ImGui.TableSetupColumn("Example Use", ImGuiTableColumnFlags.None, 240.0f);
        }
        ImGui.TableHeadersRow();

        var clipper = new ImGuiListClipper();
        ImGuiNative.ImGuiListClipper_Begin(&clipper, visibleGroups.Count, ImGui.GetTextLineHeightWithSpacing());
        while (ImGuiNative.ImGuiListClipper_Step(&clipper) != 0)
        {
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                ShaderProgramLinkGroup group = visibleGroups[i];
                ShaderProgramLinkRow row = group.Representative;
                bool selected = string.Equals(_selectedShaderProgramLinkGroupKey, group.Key, StringComparison.Ordinal);
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.PushID(i);
                if (ImGui.Selectable(TrimForTable(group.Key, 74), selected, ImGuiSelectableFlags.SpanAllColumns))
                {
                    _selectedShaderProgramLinkGroupKey = group.Key;
                    _shaderProgramLinksSelectedGroup = group;
                    _selectedShaderProgramLinkProgram = row.Program;
                    _shaderProgramLinksSelectedRow = row;
                }
                ImGui.PopID();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(BuildShaderProgramGroupClipboard(group));

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(group.LogicalReferences.ToString("N0", CultureInfo.InvariantCulture));

                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(row.ProgramNameCell);

                ImGui.TableSetColumnIndex(3);
                ImGui.TextColored(row.StatusColor, row.StatusText);

                ImGui.TableSetColumnIndex(4);
                ImGui.TextUnformatted(row.LinkType);

                ImGui.TableSetColumnIndex(5);
                ImGui.TextUnformatted(BuildShaderProgramHandleText(row.Snapshot, group));
                if (compactLayout)
                    continue;

                ImGui.TableSetColumnIndex(6);
                ImGui.TextUnformatted(row.BackendText);

                ImGui.TableSetColumnIndex(7);
                ImGui.TextUnformatted(row.HashText);

                ImGui.TableSetColumnIndex(8);
                ImGui.TextUnformatted(row.ShaderSourceCell);

                ImGui.TableSetColumnIndex(9);
                ImGui.TextUnformatted(row.TimingText);

                ImGui.TableSetColumnIndex(10);
                ImGui.TextUnformatted(row.ProgramUseCell);
            }
        }
        ImGuiNative.ImGuiListClipper_End(&clipper);

        ImGui.EndTable();

        if (visibleGroups.Count == 0)
            ImGui.TextDisabled(totalRowCount == 0 ? "No OpenGL shader programs are currently tracked." : "No shader program groups match the current filters.");
    }

    private static string BuildShaderProgramHandleText(LinkSnapshot snapshot, ShaderProgramLinkGroup group)
    {
        string source = snapshot.HandleSource switch
        {
            OpenGLRenderer.GLRenderProgram.ELinkedProgramHandleSource.SharedLinkedProgram => "shared",
            OpenGLRenderer.GLRenderProgram.ELinkedProgramHandleSource.OwnedBinary => "binary",
            OpenGLRenderer.GLRenderProgram.ELinkedProgramHandleSource.OwnedSource => "source",
            _ => "none",
        };

        if (group.SharedProgramId != 0)
            return $"{source} #{group.SharedProgramId} refs={group.SharedHandleReferences}";

        return snapshot.ProgramId == 0
            ? source
            : $"{source} #{snapshot.ProgramId}";
    }

    private static unsafe void DrawShaderProgramLinksTable(IReadOnlyList<ShaderProgramLinkRow> visibleRows, int totalRowCount, bool compactLayout)
    {
        ImGuiTableFlags flags =
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.Reorderable |
            ImGuiTableFlags.Hideable |
            ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.BordersOuter |
            ImGuiTableFlags.BordersV |
            ImGuiTableFlags.SizingStretchProp;

        float tableHeight = MathF.Max(220.0f, ImGui.GetContentRegionAvail().Y);
        int columnCount = compactLayout ? 6 : 13;
        if (!ImGui.BeginTable("ShaderProgramLinksTable", columnCount, flags, new Vector2(0.0f, tableHeight)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        if (compactLayout)
        {
            ImGui.TableSetupColumn("Program", ImGuiTableColumnFlags.None, 190.0f);
            ImGui.TableSetupColumn("Use", ImGuiTableColumnFlags.None, 130.0f);
            ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.None, 76.0f);
            ImGui.TableSetupColumn("Link", ImGuiTableColumnFlags.None, 145.0f);
            ImGui.TableSetupColumn("Work", ImGuiTableColumnFlags.None, 170.0f);
            ImGui.TableSetupColumn("Last", ImGuiTableColumnFlags.None, 145.0f);
        }
        else
        {
            ImGui.TableSetupColumn("Program", ImGuiTableColumnFlags.None, 190.0f);
            ImGui.TableSetupColumn("Use", ImGuiTableColumnFlags.None, 130.0f);
            ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.None, 78.0f);
            ImGui.TableSetupColumn("Link Type", ImGuiTableColumnFlags.None, 145.0f);
            ImGui.TableSetupColumn("Backend", ImGuiTableColumnFlags.None, 110.0f);
            ImGui.TableSetupColumn("Stage", ImGuiTableColumnFlags.None, 95.0f);
            ImGui.TableSetupColumn("Hash", ImGuiTableColumnFlags.None, 120.0f);
            ImGui.TableSetupColumn("Shaders", ImGuiTableColumnFlags.None, 150.0f);
            ImGui.TableSetupColumn("Flags", ImGuiTableColumnFlags.None, 190.0f);
            ImGui.TableSetupColumn("Queues", ImGuiTableColumnFlags.None, 190.0f);
            ImGui.TableSetupColumn("Timing", ImGuiTableColumnFlags.None, 170.0f);
            ImGui.TableSetupColumn("Window", ImGuiTableColumnFlags.None, 100.0f);
            ImGui.TableSetupColumn("Detail", ImGuiTableColumnFlags.None, 220.0f);
        }
        ImGui.TableHeadersRow();

        var clipper = new ImGuiListClipper();
        ImGuiNative.ImGuiListClipper_Begin(&clipper, visibleRows.Count, ImGui.GetTextLineHeightWithSpacing());
        while (ImGuiNative.ImGuiListClipper_Step(&clipper) != 0)
        {
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                ShaderProgramLinkRow row = visibleRows[i];
                bool selected = ReferenceEquals(_selectedShaderProgramLinkProgram, row.Program);
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.PushID(i);
                if (ImGui.Selectable(row.ProgramNameCell, selected, ImGuiSelectableFlags.SpanAllColumns))
                {
                    _selectedShaderProgramLinkProgram = row.Program;
                    _shaderProgramLinksSelectedRow = row;
                }
                ImGui.PopID();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(BuildShaderProgramLinkTooltip(row));

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(row.ProgramUseCell);

                ImGui.TableSetColumnIndex(2);
                ImGui.TextColored(row.StatusColor, row.StatusText);

                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(row.LinkType);

                ImGui.TableSetColumnIndex(4);
                if (compactLayout)
                {
                    ImGui.TextUnformatted(row.QueueText);
                    ImGui.TableSetColumnIndex(5);
                    ImGui.TextUnformatted(row.TimingText);
                    continue;
                }

                ImGui.TextUnformatted(row.BackendText);

                ImGui.TableSetColumnIndex(5);
                ImGui.TextUnformatted(row.StageText);

                ImGui.TableSetColumnIndex(6);
                ImGui.TextUnformatted(row.HashText);

                ImGui.TableSetColumnIndex(7);
                ImGui.TextUnformatted(row.ShaderSourceCell);

                ImGui.TableSetColumnIndex(8);
                ImGui.TextUnformatted(row.FlagsText);

                ImGui.TableSetColumnIndex(9);
                ImGui.TextUnformatted(row.QueueText);

                ImGui.TableSetColumnIndex(10);
                ImGui.TextUnformatted(row.TimingText);

                ImGui.TableSetColumnIndex(11);
                ImGui.TextUnformatted(row.WindowTitle);

                ImGui.TableSetColumnIndex(12);
                ImGui.TextUnformatted(row.DetailText);
            }
        }
        ImGuiNative.ImGuiListClipper_End(&clipper);

        ImGui.EndTable();

        if (visibleRows.Count == 0)
            ImGui.TextDisabled(totalRowCount == 0 ? "No OpenGL shader programs are currently tracked." : "No shader programs match the current filters.");
    }

    private static void EnsureShaderProgramLinksView(IReadOnlyList<ShaderProgramLinkRow> rows)
    {
        if (!_shaderProgramLinksViewDirty && ReferenceEquals(_shaderProgramLinksViewSource, rows))
            return;

        OpenGLRenderer.GLRenderProgram? selectedProgram = _selectedShaderProgramLinkProgram;
        ShaderProgramLinkRow? selectedRow = null;
        ShaderProgramLinkGroup? selectedGroup = null;
        List<ShaderProgramLinkRow> visibleRows = _shaderProgramLinkVisibleRows;
        List<ShaderProgramLinkGroup> visibleGroups = _shaderProgramLinkVisibleGroups;
        visibleRows.Clear();
        visibleGroups.Clear();
        Dictionary<string, ShaderProgramLinkGroup> groups = new(StringComparer.Ordinal);
        ShaderProgramLinksSummary capturedSummary = _shaderProgramLinksSummary;

        for (int i = 0; i < rows.Count; i++)
        {
            ShaderProgramLinkRow row = rows[i];
            if (selectedProgram is not null && ReferenceEquals(row.Program, selectedProgram))
                selectedRow = row;

            if (PassesShaderProgramLinksFilters(row))
            {
                visibleRows.Add(row);
                string groupKey = BuildShaderProgramGroupKey(row.Snapshot);
                if (!groups.TryGetValue(groupKey, out ShaderProgramLinkGroup? group))
                {
                    group = new ShaderProgramLinkGroup(groupKey, row);
                    groups.Add(groupKey, group);
                    visibleGroups.Add(group);
                }

                group.Add(row);
            }
        }

        SortShaderProgramLinkGroups(visibleGroups);
        if (!string.IsNullOrWhiteSpace(_selectedShaderProgramLinkGroupKey))
        {
            for (int i = 0; i < visibleGroups.Count; i++)
            {
                if (string.Equals(visibleGroups[i].Key, _selectedShaderProgramLinkGroupKey, StringComparison.Ordinal))
                {
                    selectedGroup = visibleGroups[i];
                    break;
                }
            }
        }

        _shaderProgramLinksSelectedRow = selectedRow;
        _shaderProgramLinksSelectedGroup = selectedGroup;
        _shaderProgramLinksSummary = capturedSummary with
        {
            VisibleCount = visibleRows.Count,
            GroupCount = visibleGroups.Count,
        };
        _shaderProgramLinksViewSource = rows;
        _shaderProgramLinksViewDirty = false;
    }

    private static void MarkShaderProgramLinksViewDirty()
        => _shaderProgramLinksViewDirty = true;

    private static bool PassesShaderProgramLinksFilters(ShaderProgramLinkRow row)
    {
        bool hasStateFilter = _shaderProgramLinksFilterPendingOnly ||
                              _shaderProgramLinksFilterLinkedOnly ||
                              _shaderProgramLinksFilterFailedOnly;
        if (hasStateFilter)
        {
            bool stateMatches =
                (_shaderProgramLinksFilterPendingOnly && row.IsPending) ||
                (_shaderProgramLinksFilterLinkedOnly && row.Snapshot.IsLinked) ||
                (_shaderProgramLinksFilterFailedOnly && row.IsFailed);
            if (!stateMatches)
                return false;
        }

        if (string.IsNullOrWhiteSpace(_shaderProgramLinksSearch))
            return true;

        string search = _shaderProgramLinksSearch.Trim();
        return ContainsSearch(row.WindowTitle, search) ||
               ContainsSearch(row.XrName, search) ||
               ContainsSearch(row.ProgramName, search) ||
               ContainsSearch(row.ProgramUse, search) ||
               ContainsSearch(row.ShaderSourceCell, search) ||
               ContainsSearch(row.BackendText, search) ||
               ContainsSearch(row.DetailText, search) ||
               ContainsSearch(row.ShaderStagesCell, search) ||
               ContainsSearch(row.HashText, search) ||
               ContainsSearch(BuildShaderProgramGroupKey(row.Snapshot), search) ||
               ContainsSearch(row.Snapshot.ProgramDescriptorKey, search) ||
               ContainsSearch(row.Snapshot.BinaryCacheKey, search);
    }

    private static bool ContainsSearch(string? value, string search)
        => !string.IsNullOrWhiteSpace(value) && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static int CompareShaderProgramLinkRows(ShaderProgramLinkRow left, ShaderProgramLinkRow right)
    {
        int result = _shaderProgramLinksSortMode switch
        {
            ShaderProgramLinksSortMode.Refs => 0,
            ShaderProgramLinksSortMode.Program => string.Compare(left.ProgramName, right.ProgramName, StringComparison.OrdinalIgnoreCase),
            ShaderProgramLinksSortMode.Backend => string.Compare(left.BackendText, right.BackendText, StringComparison.OrdinalIgnoreCase),
            ShaderProgramLinksSortMode.QueueLatency => left.Snapshot.LastBuildQueueLatencyMilliseconds.CompareTo(right.Snapshot.LastBuildQueueLatencyMilliseconds),
            ShaderProgramLinksSortMode.LinkTime => left.Snapshot.LastBuildLinkMilliseconds.CompareTo(right.Snapshot.LastBuildLinkMilliseconds),
            ShaderProgramLinksSortMode.BinaryTime => left.Snapshot.LastBuildBinaryLoadMilliseconds.CompareTo(right.Snapshot.LastBuildBinaryLoadMilliseconds),
            ShaderProgramLinksSortMode.Hash => left.DisplayHash.CompareTo(right.DisplayHash),
            _ => left.StateSortRank.CompareTo(right.StateSortRank),
        };

        if (_shaderProgramLinksSortDescending)
            result = -result;

        if (result != 0)
            return result;

        return string.Compare(left.ProgramName, right.ProgramName, StringComparison.OrdinalIgnoreCase);
    }

    private static void SortShaderProgramLinkGroups(List<ShaderProgramLinkGroup> groups)
        => groups.Sort(CompareShaderProgramLinkGroups);

    private static int CompareShaderProgramLinkGroups(ShaderProgramLinkGroup left, ShaderProgramLinkGroup right)
    {
        ShaderProgramLinkRow leftRow = left.Representative;
        ShaderProgramLinkRow rightRow = right.Representative;
        int result = _shaderProgramLinksSortMode switch
        {
            ShaderProgramLinksSortMode.Refs => left.LogicalReferences.CompareTo(right.LogicalReferences),
            ShaderProgramLinksSortMode.Program => string.Compare(leftRow.ProgramName, rightRow.ProgramName, StringComparison.OrdinalIgnoreCase),
            ShaderProgramLinksSortMode.Backend => string.Compare(leftRow.BackendText, rightRow.BackendText, StringComparison.OrdinalIgnoreCase),
            ShaderProgramLinksSortMode.QueueLatency => leftRow.Snapshot.LastBuildQueueLatencyMilliseconds.CompareTo(rightRow.Snapshot.LastBuildQueueLatencyMilliseconds),
            ShaderProgramLinksSortMode.LinkTime => leftRow.Snapshot.LastBuildLinkMilliseconds.CompareTo(rightRow.Snapshot.LastBuildLinkMilliseconds),
            ShaderProgramLinksSortMode.BinaryTime => leftRow.Snapshot.LastBuildBinaryLoadMilliseconds.CompareTo(rightRow.Snapshot.LastBuildBinaryLoadMilliseconds),
            ShaderProgramLinksSortMode.Hash => leftRow.DisplayHash.CompareTo(rightRow.DisplayHash),
            _ => leftRow.StateSortRank.CompareTo(rightRow.StateSortRank),
        };

        if (_shaderProgramLinksSortDescending)
            result = -result;

        if (result != 0)
            return result;

        result = right.LogicalReferences.CompareTo(left.LogicalReferences);
        return result != 0
            ? result
            : string.Compare(left.Key, right.Key, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetShaderProgramStateSortRank(LinkSnapshot snapshot)
    {
        if (IsShaderProgramFailed(snapshot))
            return 5;
        bool hasLiveWork = HasLiveShaderProgramWork(snapshot);
        if (hasLiveWork && (snapshot.BackendStage is EShaderProgramBackendStage.Compiling or EShaderProgramBackendStage.Linking or EShaderProgramBackendStage.DriverParallelPending))
            return 4;
        if (IsShaderProgramQueued(snapshot))
            return 3;
        if (hasLiveWork)
            return 2;
        if (IsShaderProgramPreparedOnly(snapshot))
            return 1;
        if (snapshot.IsLinked)
            return 0;
        return -1;
    }

    private static void DrawShaderProgramLinkGroupDetails(ShaderProgramLinkGroup group)
    {
        if (!ImGui.CollapsingHeader("Selected Program Group", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ShaderProgramLinkRow row = group.Representative;
        LinkSnapshot snapshot = row.Snapshot;
        if (ImGui.SmallButton("Copy Group Summary"))
            ImGui.SetClipboardText(BuildShaderProgramGroupClipboard(group));

        ImGui.SameLine();
        if (ImGui.SmallButton("Log Group Summary"))
            XREngine.Debug.OpenGL(BuildShaderProgramGroupLogLine(group));

        ImGui.SameLine();
        if (ImGui.SmallButton("Clear Selection"))
        {
            _selectedShaderProgramLinkGroupKey = null;
            _shaderProgramLinksSelectedGroup = null;
            _selectedShaderProgramLinkProgram = null;
            _shaderProgramLinksSelectedRow = null;
        }

        if (!ImGui.BeginTable("ShaderProgramLinkGroupDetails", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
            return;

        DrawShaderProgramDetailRow("Group Key", group.Key);
        DrawShaderProgramDetailRow("Logical Refs", group.LogicalReferences.ToString("N0", CultureInfo.InvariantCulture));
        DrawShaderProgramDetailRow("States", $"linked={group.Linked:N0}, pending={group.Pending:N0}, prepared={group.Prepared:N0}, failed={group.Failed:N0}");
        DrawShaderProgramDetailRow("Handle", BuildShaderProgramHandleText(snapshot, group));
        DrawShaderProgramDetailRow("Program", row.ProgramName);
        DrawShaderProgramDetailRow("Use", row.ProgramUse);
        DrawShaderProgramDetailRow("Descriptor", snapshot.ProgramDescriptorKey ?? "-");
        DrawShaderProgramDetailRow("Binary Key", snapshot.BinaryCacheKey ?? snapshot.PreparedCacheKey ?? "-");
        DrawShaderProgramDetailRow("Hash", row.HashText);
        DrawShaderProgramDetailRow("Backend", $"{row.BackendText} detail={snapshot.BackendDetail ?? "-"}");
        DrawShaderProgramDetailRow("Timings", row.TimingText);

        int listed = 0;
        var contributors = new StringBuilder(512);
        foreach (ShaderProgramLinkRow contributor in group.Rows)
        {
            if (listed >= 12)
                break;

            if (contributors.Length > 0)
                contributors.AppendLine();

            contributors
                .Append(contributor.ProgramName)
                .Append(" | ")
                .Append(contributor.ProgramUse)
                .Append(" | ")
                .Append(contributor.WindowTitle);
            listed++;
        }

        if (group.Rows.Count > listed)
            contributors.AppendLine().Append("... ").Append((group.Rows.Count - listed).ToString("N0", CultureInfo.InvariantCulture)).Append(" more");

        DrawShaderProgramDetailRow("Contributors", contributors.Length == 0 ? "-" : contributors.ToString());

        ImGui.EndTable();
    }

    private static void DrawShaderProgramLinkDetails(ShaderProgramLinkRow row)
    {
        if (!ImGui.CollapsingHeader("Selected Program", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        LinkSnapshot snapshot = row.Snapshot;
        if (ImGui.SmallButton("Copy Summary"))
            ImGui.SetClipboardText(BuildShaderProgramLinkClipboard(row));

        ImGui.SameLine();
        if (ImGui.SmallButton("Clear Selection"))
        {
            _selectedShaderProgramLinkProgram = null;
            _shaderProgramLinksSelectedRow = null;
        }

        if (!ImGui.BeginTable("ShaderProgramLinkDetails", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
            return;

        DrawShaderProgramDetailRow("Program", row.ProgramName);
        DrawShaderProgramDetailRow("Use", row.ProgramUse);
        DrawShaderProgramDetailRow("Status", $"{row.StatusText} / {row.StageText}");
        DrawShaderProgramDetailRow("Link Type", row.LinkType);
        DrawShaderProgramDetailRow("Hash", row.HashText);
        DrawShaderProgramDetailRow("Descriptor", snapshot.ProgramDescriptorKey ?? "-");
        DrawShaderProgramDetailRow("Binary Key", snapshot.BinaryCacheKey ?? snapshot.PreparedCacheKey ?? "-");
        DrawShaderProgramDetailRow("Program Id", $"{snapshot.ProgramId} (replacement {snapshot.ReplacementProgramId}, shared {snapshot.SharedLinkedProgramId}, refs {snapshot.SharedLinkedProgramReferenceCount}, owns={snapshot.OwnsCurrentProgramHandle}, source={snapshot.HandleSource})");
        DrawShaderProgramDetailRow("Shaders", $"{snapshot.ShaderCount} [{snapshot.ShaderStages}] separable={snapshot.Separable} sources={row.ShaderSourceCell}");
        DrawShaderProgramDetailRow("Backend", $"{row.BackendText} detail={snapshot.BackendDetail ?? "-"}");
        DrawShaderProgramDetailRow("Fingerprint", snapshot.ActiveBuildFingerprint ?? snapshot.BackendFingerprint ?? snapshot.LastBuildFingerprint ?? "-");
        DrawShaderProgramDetailRow("Settings", row.FlagsText);
        DrawShaderProgramDetailRow("Queues", row.QueueText);
        DrawShaderProgramDetailRow("Pending", row.PendingText);
        DrawShaderProgramDetailRow("Prepared", $"dataReady={snapshot.LinkDataPrepared}, registered={snapshot.PendingAsyncProgramRegistered}, preparedHash={(snapshot.PreparedHash == 0 ? "-" : snapshot.PreparedHash.ToString("X16", CultureInfo.InvariantCulture))}, cacheKey={snapshot.PreparedCacheKey ?? "-"}");
        DrawShaderProgramDetailRow("Timings", row.TimingText);
        DrawShaderProgramDetailRow("Failure", snapshot.BackendFailureReason ?? snapshot.LastBuildFailureReason ?? "-");

        ImGui.EndTable();
    }

    private static void DrawShaderProgramDetailRow(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(label);
        ImGui.TableSetColumnIndex(1);
        ImGui.TextWrapped(value);
    }

    private static string BuildShaderProgramLinkTooltip(ShaderProgramLinkRow row)
        => BuildShaderProgramLinkClipboard(row) +
           $"{Environment.NewLine}Queues: {row.QueueText}" +
           $"{Environment.NewLine}Pending: {row.PendingText}";

    private static string BuildShaderProgramGroupClipboard(ShaderProgramLinkGroup group)
    {
        ShaderProgramLinkRow row = group.Representative;
        LinkSnapshot s = row.Snapshot;
        return
            $"Group: {group.Key}{Environment.NewLine}" +
            $"LogicalRefs: {group.LogicalReferences}{Environment.NewLine}" +
            $"States: linked={group.Linked}, pending={group.Pending}, prepared={group.Prepared}, failed={group.Failed}{Environment.NewLine}" +
            $"Handle: {BuildShaderProgramHandleText(s, group)} ownsCurrent={s.OwnsCurrentProgramHandle}{Environment.NewLine}" +
            $"Program: {row.ProgramName}{Environment.NewLine}" +
            $"Use: {row.ProgramUse}{Environment.NewLine}" +
            $"Descriptor: {s.ProgramDescriptorKey ?? "-"}{Environment.NewLine}" +
            $"BinaryKey: {s.BinaryCacheKey ?? s.PreparedCacheKey ?? "-"}{Environment.NewLine}" +
            $"Hash: {row.HashText}{Environment.NewLine}" +
            $"Backend: {row.BackendText}{Environment.NewLine}" +
            $"Timings: {row.TimingText}";
    }

    private static string BuildShaderProgramGroupLogLine(ShaderProgramLinkGroup group)
    {
        ShaderProgramLinkRow row = group.Representative;
        LinkSnapshot s = row.Snapshot;
        return
            $"[ShaderProgramDedupSummary] group='{group.Key}' logicalRefs={group.LogicalReferences} " +
            $"linked={group.Linked} pending={group.Pending} prepared={group.Prepared} failed={group.Failed} " +
            $"program='{row.ProgramName}' use='{row.ProgramUse}' descriptor={s.ProgramDescriptorKey ?? "<none>"} " +
            $"binaryKey={s.BinaryCacheKey ?? s.PreparedCacheKey ?? "<none>"} hash={row.HashText} " +
            $"handleSource={s.HandleSource} programId={s.ProgramId} sharedProgramId={s.SharedLinkedProgramId} sharedRefs={s.SharedLinkedProgramReferenceCount} " +
            $"ownsHandle={s.OwnsCurrentProgramHandle} backend={row.BackendText} timing='{row.TimingText}'.";
    }

    private static string BuildShaderProgramLinkClipboard(ShaderProgramLinkRow row)
    {
        LinkSnapshot s = row.Snapshot;
        return
            $"Program: {row.ProgramName}{Environment.NewLine}" +
            $"Use: {row.ProgramUse}{Environment.NewLine}" +
            $"Window: {row.WindowTitle}{Environment.NewLine}" +
            $"Status: {row.StatusText} / {row.StageText}{Environment.NewLine}" +
            $"Link Type: {row.LinkType}{Environment.NewLine}" +
            $"Backend: {row.BackendText}{Environment.NewLine}" +
            $"Hash: {row.HashText}{Environment.NewLine}" +
            $"Descriptor: {s.ProgramDescriptorKey ?? "-"}{Environment.NewLine}" +
            $"BinaryKey: {s.BinaryCacheKey ?? s.PreparedCacheKey ?? "-"}{Environment.NewLine}" +
            $"Handle: source={s.HandleSource}, ProgramId={s.ProgramId}, ReplacementId={s.ReplacementProgramId}, SharedId={s.SharedLinkedProgramId}, SharedRefs={s.SharedLinkedProgramReferenceCount}, Owns={s.OwnsCurrentProgramHandle}{Environment.NewLine}" +
            $"Shaders: {s.ShaderCount} [{s.ShaderStages}], separable={s.Separable}, sources={row.ShaderSourceCell}{Environment.NewLine}" +
            $"Flags: {row.FlagsText}{Environment.NewLine}" +
            $"Pending: {row.PendingText}{Environment.NewLine}" +
            $"Timings: {row.TimingText}{Environment.NewLine}" +
            $"Detail: {s.BackendDetail ?? "-"}{Environment.NewLine}" +
            $"Failure: {s.BackendFailureReason ?? s.LastBuildFailureReason ?? "-"}";
    }

    private static string GetShaderProgramStatusText(LinkSnapshot snapshot)
    {
        if (IsShaderProgramFailed(snapshot))
            return snapshot.BackendStage == EShaderProgramBackendStage.Abandoned ? "Abandoned" : "Failed";
        bool hasLiveWork = HasLiveShaderProgramWork(snapshot);
        if (hasLiveWork && snapshot.BackendStage == EShaderProgramBackendStage.Compiling)
            return "Compiling";
        if (hasLiveWork && snapshot.BackendStage == EShaderProgramBackendStage.Linking)
            return "Linking";
        if (IsShaderProgramQueued(snapshot))
            return "Queued";
        if (hasLiveWork)
            return "Pending";
        if (IsShaderProgramPreparedOnly(snapshot))
            return "Prepared";
        if (snapshot.IsLinked)
            return "Linked";
        if (snapshot.LinkReady)
            return "Ready";
        return "Idle";
    }

    private static Vector4 GetShaderProgramStatusColor(LinkSnapshot snapshot)
    {
        if (IsShaderProgramFailed(snapshot))
            return new Vector4(1.0f, 0.35f, 0.25f, 1.0f);
        bool hasLiveWork = HasLiveShaderProgramWork(snapshot);
        if (hasLiveWork && (snapshot.BackendStage is EShaderProgramBackendStage.Compiling or EShaderProgramBackendStage.Linking))
            return new Vector4(0.45f, 0.8f, 1.0f, 1.0f);
        if (IsShaderProgramQueued(snapshot) || hasLiveWork)
            return new Vector4(1.0f, 0.78f, 0.25f, 1.0f);
        if (IsShaderProgramPreparedOnly(snapshot))
            return new Vector4(0.75f, 0.75f, 1.0f, 1.0f);
        if (snapshot.IsLinked)
            return new Vector4(0.45f, 1.0f, 0.55f, 1.0f);
        return new Vector4(0.78f, 0.78f, 0.78f, 1.0f);
    }

    private static string GetShaderProgramLinkType(LinkSnapshot snapshot)
    {
        string backend = snapshot.ActiveBuildBackend ?? snapshot.BackendName ?? snapshot.LastBuildBackend ?? string.Empty;
        return backend switch
        {
            "BinaryUploadAsync" => "Binary cache async upload",
            "BinaryUploadSynchronous" => "Binary cache sync upload",
            "BinaryProgramShared" => "Shared linked binary",
            "SharedContextSource" => "Shared-context source",
            "DriverParallelSource" => "Driver-parallel source",
            "SynchronousSource" => "Synchronous source",
            "CloneAndSwap" => "Hot-reload clone",
            "BinaryCache" when snapshot.BackendStage == EShaderProgramBackendStage.CacheHit => "Binary cache hit",
            "BinaryCache" when snapshot.BackendStage == EShaderProgramBackendStage.CacheMiss => "Source after cache miss",
            "SourceUnavailable" => "Source waiting",
            _ when IsShaderProgramPreparedOnly(snapshot) && snapshot.PreparedBinaryCacheHit => "Prepared binary cache",
            _ when IsShaderProgramPreparedOnly(snapshot) => "Prepared source data",
            _ when UsesBinaryCache(snapshot) => "Binary cache",
            _ when UsesDriverParallel(snapshot) => "Driver parallel",
            _ when UsesSharedContext(snapshot) => "Shared context",
            _ when UsesSynchronousRenderThread(snapshot) => "Synchronous",
            _ => "-",
        };
    }

    private static string BuildShaderProgramFlags(LinkSnapshot snapshot)
    {
        var builder = new StringBuilder(160);
        builder.Append("strategy=").Append(snapshot.ConfiguredStrategy);
        builder.Append(" | cache=").Append(snapshot.AllowBinaryProgramCaching ? "on" : "off");
        builder.Append(" | asyncSrc=").Append(snapshot.AsyncProgramCompilation ? "on" : "off");
        builder.Append(" | asyncBin=").Append(snapshot.AsyncProgramBinaryUpload ? "on" : "off");
        builder.Append(" | driver=").Append(snapshot.DriverParallelAvailable ? "yes" : "no");
        builder.Append(" | driverThreads=").Append(snapshot.OpenGLShaderCompilerThreadCount);
        builder.Append(" | hazard=").Append(snapshot.IsKnownAsyncLinkHazard ? "yes" : "no");
        builder.Append(" | sep=").Append(snapshot.Separable ? "yes" : "no");
        builder.Append(" | handle=").Append(snapshot.HandleSource);
        if (snapshot.HasSharedLinkedProgram)
            builder.Append(" | sharedProgram refs=").Append(snapshot.SharedLinkedProgramReferenceCount);
        if (snapshot.PreparedBinaryCacheHit || snapshot.HasCachedProgram)
            builder.Append(" | cached");
        return builder.ToString();
    }

    private static string BuildShaderProgramQueueText(LinkSnapshot snapshot)
    {
        string source = snapshot.SharedContextQueueAvailable
            ? $"src {snapshot.SharedContextInFlight}/{snapshot.SharedContextMaxInFlight} {snapshot.SharedContextWorkerCount}w"
            : "src off";
        if (snapshot.SharedContextQueueUnhealthy)
            source += " unhealthy";
        else if (snapshot.SharedContextQueueAvailable && !snapshot.SharedContextQueueCanEnqueue)
            source += " full";

        string binary = snapshot.BinaryUploadQueueAvailable
            ? $"bin {snapshot.BinaryUploadInFlight}/{snapshot.BinaryUploadMaxInFlight} keys {snapshot.BinaryUploadInFlightCacheKeys}"
            : "bin off";
        if (snapshot.BinaryUploadQueueUnhealthy)
            binary += " unhealthy";
        else if (snapshot.BinaryUploadQueueAvailable && !snapshot.BinaryUploadQueueCanEnqueue)
            binary += " full";

        double oldest = Math.Max(snapshot.SharedContextOldestPendingSeconds, snapshot.BinaryUploadOldestPendingSeconds);
        return oldest > 0.0
            ? $"{source} | {binary} | oldest {oldest:F1}s"
            : $"{source} | {binary}";
    }

    private static string BuildShaderProgramPendingText(LinkSnapshot snapshot)
        => $"work={snapshot.HasPendingAsyncWork}, queuedWork={snapshot.HasQueuedOrRunningAsyncWork}, registered={snapshot.PendingAsyncProgramRegistered}, " +
           $"asyncBuild={snapshot.IsAsyncBuildPending}, phase={snapshot.AsyncLinkPhase}, age={snapshot.AsyncPendingSeconds:F2}s, " +
           $"prep={snapshot.LinkPreparationPending}, dataReady={snapshot.LinkDataPrepared}, " +
           $"binWait={snapshot.AsyncBinaryUploadQueueWaitPending}, binUpload={snapshot.AsyncBinaryUploadPending}, srcLink={snapshot.AsyncCompileLinkPending}, " +
           $"queueWait={snapshot.AsyncCompileLinkQueueWaitPending}, dupWait={snapshot.AsyncCompileDuplicateHashWaitPending}, replacement={snapshot.ReplacementProgramPending}";

    private static string BuildShaderProgramTimingText(LinkSnapshot snapshot)
        => $"q={FormatShaderProgramMilliseconds(snapshot.LastBuildQueueLatencyMilliseconds)} " +
           $"c={FormatShaderProgramMilliseconds(Math.Max(snapshot.LastBuildCompileMilliseconds, snapshot.BackendCompileMilliseconds))} " +
           $"l={FormatShaderProgramMilliseconds(Math.Max(snapshot.LastBuildLinkMilliseconds, snapshot.BackendLinkMilliseconds))} " +
           $"b={FormatShaderProgramMilliseconds(snapshot.LastBuildBinaryLoadMilliseconds)} " +
           $"r={FormatShaderProgramMilliseconds(snapshot.LastBuildReflectionMilliseconds)}";

    private static string FormatShaderProgramMilliseconds(double ms)
    {
        if (ms <= 0.0)
            return "-";

        return ms >= 1000.0
            ? (ms / 1000.0).ToString("F2", CultureInfo.InvariantCulture) + "s"
            : ms.ToString("F1", CultureInfo.InvariantCulture) + "ms";
    }

    private static bool IsShaderProgramPending(LinkSnapshot snapshot)
        => HasLiveShaderProgramWork(snapshot);

    private static bool HasLiveShaderProgramWork(LinkSnapshot snapshot)
        => snapshot.HasPendingAsyncWork ||
           snapshot.HasQueuedOrRunningAsyncWork ||
           snapshot.PendingAsyncProgramRegistered ||
           snapshot.LinkPreparationPending ||
           snapshot.AsyncBinaryUploadQueueWaitPending ||
           snapshot.AsyncBinaryUploadPending ||
           snapshot.AsyncCompileLinkPending ||
           snapshot.AsyncCompileLinkQueueWaitPending ||
           snapshot.AsyncCompileDuplicateHashWaitPending ||
           snapshot.ReplacementProgramPending;

    private static bool IsShaderProgramPreparedOnly(LinkSnapshot snapshot)
        => snapshot.LinkDataPrepared &&
           !snapshot.HasPendingAsyncWork &&
           !snapshot.HasQueuedOrRunningAsyncWork &&
           !snapshot.PendingAsyncProgramRegistered &&
           !snapshot.IsLinked;

    private static bool IsShaderProgramQueued(LinkSnapshot snapshot)
        => HasLiveShaderProgramWork(snapshot) &&
           (snapshot.BackendStage is EShaderProgramBackendStage.BinaryUploadPending
               or EShaderProgramBackendStage.SourceQueued
               or EShaderProgramBackendStage.QueueBackpressure
               or EShaderProgramBackendStage.DriverParallelPending);

    private static bool IsShaderProgramFailed(LinkSnapshot snapshot)
        => snapshot.BackendStage is EShaderProgramBackendStage.Failed or EShaderProgramBackendStage.Abandoned ||
           !string.IsNullOrWhiteSpace(snapshot.BackendFailureReason);

    private static bool UsesBinaryCache(LinkSnapshot snapshot)
    {
        string? backend = snapshot.ActiveBuildBackend ?? snapshot.BackendName ?? snapshot.LastBuildBackend;
        return snapshot.PreparedBinaryCacheHit ||
               snapshot.HasCachedProgram ||
               snapshot.AsyncBinaryUploadQueueWaitPending ||
               snapshot.AsyncBinaryUploadPending ||
               snapshot.BackendStage is EShaderProgramBackendStage.CacheHit
                   or EShaderProgramBackendStage.BinaryUploadPending
                   or EShaderProgramBackendStage.BinaryUploadReady
                   or EShaderProgramBackendStage.BinaryUploadFailed ||
               (backend?.Contains("Binary", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static bool UsesSharedContext(LinkSnapshot snapshot)
    {
        string? backend = snapshot.ActiveBuildBackend ?? snapshot.BackendName ?? snapshot.LastBuildBackend;
        return string.Equals(backend, "SharedContextSource", StringComparison.Ordinal) ||
               string.Equals(backend, "BinaryUploadAsync", StringComparison.Ordinal) ||
               snapshot.AsyncCompileLinkPending ||
               snapshot.AsyncBinaryUploadQueueWaitPending ||
               snapshot.AsyncBinaryUploadPending;
    }

    private static bool UsesDriverParallel(LinkSnapshot snapshot)
    {
        string? backend = snapshot.ActiveBuildBackend ?? snapshot.BackendName ?? snapshot.LastBuildBackend;
        return string.Equals(backend, "DriverParallelSource", StringComparison.Ordinal) ||
               snapshot.BackendStage == EShaderProgramBackendStage.DriverParallelPending;
    }

    private static bool UsesSynchronousRenderThread(LinkSnapshot snapshot)
    {
        string? backend = snapshot.ActiveBuildBackend ?? snapshot.BackendName ?? snapshot.LastBuildBackend;
        return string.Equals(backend, "SynchronousSource", StringComparison.Ordinal) ||
               string.Equals(backend, "BinaryUploadSynchronous", StringComparison.Ordinal);
    }

    private static string TrimForTable(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "-";
        if (value.Length <= maxChars)
            return value;
        return value[..Math.Max(0, maxChars - 3)] + "...";
    }

    private static void LogShaderProgramLifecycleSummary()
    {
        foreach (XRWindow? window in Engine.Windows)
        {
            if (window?.Renderer is OpenGLRenderer renderer)
                ShaderProgramLifecycleDiagnostics.LogSummary(renderer.ProgramBinaryUploadQueue);
        }
    }
}
