using XREngine.Components;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;
using System.Runtime.InteropServices;
using XREngine.Data.Profiling;

namespace XREngine.Editor;

/// <summary>Shows the latest in-process profiler sample.</summary>
public partial class ProfilerPanel : NativeVirtualTextPanel
{
    private const int MaximumIndexStepsPerTick = 2048;
    private static readonly ENativeProfilerView[] ViewOptions = Enum.GetValues<ENativeProfilerView>();
    private readonly NativeProfilerRows _rows = new();
    private readonly NativeProfilerWorstFrameSelector _worstCpuFrame = new();
    private readonly UIVirtualTableProvider<NativeProfilerDisplayRow> _tableProvider;
    private readonly Dictionary<UIBoundableTransform, NativeProfilerValueCellState> _valueCells = [];
    private NativeProfilerSettingOwner? _settingsOwner;
    private long _settingsOwnerRevision;
    private DateTime _lastRefresh;
    private bool _paused;
    private bool _terminal;
    private bool _ownerActive;
    private bool _tickRegistered;
    private UIComboBoxComponent? _viewCombo;
    private NativePropertyPopupHost? _viewPopupHost;
    private UITextComponent? _viewLabel;
    private UIButtonComponent? _pauseButton;
    private UIButtonComponent? _captureButton;
    private UIButtonComponent? _gpuDumpButton;
    private UIButtonComponent? _cpuDumpButton;
    private UIButtonComponent? _externalProfilerButton;
    private string _externalProfilerStatus = string.Empty;
    private string _gpuDumpStatus = string.Empty;
    private readonly List<NativeProfilerDisplayRow> _diagnosticScratch = [];
    private float _rootMinimumMilliseconds;
    private float _rootMaximumMilliseconds;
    private bool _sortRootsByTime = true;
    private long _rootViewRevision;
    private string? _selectedCpuRootName;
    private ProfilerScopeKind _selectedCpuRootKind;

    internal float RootMinimumMilliseconds => _rootMinimumMilliseconds;
    internal float RootMaximumMilliseconds => _rootMaximumMilliseconds;
    internal bool IsRootViewCurrent => IsProfilerOwnerCurrent && _rows.View == ENativeProfilerView.CpuRoots;
    internal long RootViewRevision => _rootViewRevision;

    internal void SetRootLimit(bool minimum, float value)
    {
        if (!IsRootViewCurrent) return;
        if (minimum)
            _rootMinimumMilliseconds = value;
        else
            _rootMaximumMilliseconds = value;
        int previousCount = _rows.Count;
        if (_rows.SetRootParameters(_rootMinimumMilliseconds, _rootMaximumMilliseconds, _sortRootsByTime))
        {
            _rootViewRevision++;
            SignalRowsChanged(previousCount);
        }
    }

    public ProfilerPanel()
    {
        _tableProvider = new UIVirtualTableProvider<NativeProfilerDisplayRow>(_rows,
            UIVirtualTableColumn<NativeProfilerDisplayRow>.Text("name", "Name", 320, row => row.Scope),
            new UIVirtualTableColumn<NativeProfilerDisplayRow>("value", "Value", 100,
                CreateValueCell, BindValueCell, UnbindValueCell, BindValueCell),
            UIVirtualTableColumn<NativeProfilerDisplayRow>.Text("details", "Details", 420, row => row.Self));
        RowProviderOverride = _tableProvider;
        _tableProvider.Activated += OnProfilerRowActivated;
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        if (_terminal || !IsActiveInHierarchy) return;
        _ownerActive = true;
        UpdateCaptureState();
        if (!_tickRegistered)
        {
            RegisterTick(ETickGroup.Late, ETickOrder.Scene, RefreshTick);
            _tickRegistered = true;
        }
    }

    protected override void OnComponentDeactivated()
    {
        _ownerActive = false;
        _worstCpuFrame.Reset();
        _rows.CancelPending();
        try { DetachTick(); }
        finally
        {
            try { base.OnComponentDeactivated(); }
            finally
            {
                if (!_ownerActive && !_terminal)
                    _viewCombo?.Close();
            }
        }
    }

    protected override void OnDestroying()
    {
        _terminal = true;
        _ownerActive = false;
        _settingsOwner = null;
        _settingsOwnerRevision++;
        _diagnosticScratch.Clear();
        ClearSelectedGpuWorstTree();
        ClearGpuRankPage();
        _gpuTraversal.Clear();
        _trendPlot = null;
        _invokesValid = false;
        _spikes.Release();
        _worstCpuFrame.Reset();
        UIComboBoxComponent? combo = _viewCombo;
        NativePropertyPopupHost? popupHost = _viewPopupHost;
        UIButtonComponent? pauseButton = _pauseButton;
        UIButtonComponent? captureButton = _captureButton;
        UIButtonComponent? gpuDumpButton = _gpuDumpButton;
        UIButtonComponent? cpuDumpButton = _cpuDumpButton;
        UIButtonComponent? externalProfilerButton = _externalProfilerButton;
        _viewCombo = null;
        _viewPopupHost = null;
        _viewLabel = null;
        _pauseButton = null;
        _captureButton = null;
        _gpuDumpButton = null;
        _cpuDumpButton = null;
        _externalProfilerButton = null;
        List<Exception>? errors = null;
        void Attempt(Action action)
        {
            try { action(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        Attempt(_rows.Clear);
        Attempt(() => _tableProvider.Activated -= OnProfilerRowActivated);
        NativeProfilerValueCellState[] valueCells = [.. _valueCells.Values];
        _valueCells.Clear();
        foreach (NativeProfilerValueCellState cell in valueCells)
            Attempt(cell.Unbind);
        if (combo is not null)
        {
            Attempt(() => combo.Opening -= OnViewOpening);
            Attempt(() => combo.SelectionChanged -= OnViewSelectionChanged);
        }
        if (pauseButton is not null)
            Attempt(() => pauseButton.UnregisterClickActions(OnPauseClicked));
        if (captureButton is not null)
            Attempt(() => captureButton.UnregisterClickActions(OnCaptureClicked));
        if (gpuDumpButton is not null)
            Attempt(() => gpuDumpButton.UnregisterClickActions(OnGpuDumpClicked));
        if (cpuDumpButton is not null)
            Attempt(() => cpuDumpButton.UnregisterClickActions(OnCpuDumpClicked));
        if (externalProfilerButton is not null)
            Attempt(() => externalProfilerButton.UnregisterClickActions(OnExternalProfilerClicked));
        if (popupHost is not null && !popupHost.IsDestroyed && !popupHost.IsDestroyQueued)
            Attempt(() => popupHost.Destroy(true));
        Attempt(DetachTick);
        Attempt(() => base.OnDestroying());
        if (errors is not null)
            throw new AggregateException(errors);
    }

    private void DetachTick()
    {
        if (!_tickRegistered) return;
        _tickRegistered = false;
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, RefreshTick);
    }

    protected override void CreateCommands(SceneNode commands)
    {
        var pause = NativeUIElements.CreateButton(commands, "Pause", null, width: 88);
        _pauseButton = pause.Button;
        pause.Button.RegisterClickActions(OnPauseClicked);
        var capture = NativeUIElements.CreateButton(commands, "Components", null, width: 88);
        _captureButton = capture.Button;
        capture.Button.RegisterClickActions(OnCaptureClicked);
        var cpuDump = NativeUIElements.CreateButton(commands, "Dump CPU", null, width: 96);
        _cpuDumpButton = cpuDump.Button;
        cpuDump.Button.RegisterClickActions(OnCpuDumpClicked);
        var externalProfiler = NativeUIElements.CreateButton(commands, "External", null, width: 92);
        _externalProfilerButton = externalProfiler.Button;
        externalProfiler.Button.RegisterClickActions(OnExternalProfilerClicked);
        var gpuDump = NativeUIElements.CreateButton(commands, "Dump GPU", null, width: 88);
        _gpuDumpButton = gpuDump.Button;
        gpuDump.Button.RegisterClickActions(OnGpuDumpClicked);
        SceneNode viewNode = commands.NewChild();
        viewNode.SetTransform<UIBoundableTransform>().Width = 112;
        UIComboBoxComponent combo = viewNode.AddComponent<UIComboBoxComponent>()!;
        NativePropertyPopupHost popupHost = viewNode.AddComponent<NativePropertyPopupHost>()!;
        combo.Popup = popupHost.CreatePopup();
        ENativeProfilerView[] views = ViewOptions;
        UIComboOption[] options = new UIComboOption[views.Length];
        for (int index = 0; index < views.Length; index++)
            options[index] = new UIComboOption((ulong)views[index] + 1, ViewLabel(views[index]));
        combo.SetOptions(options);
        combo.Select((ulong)_rows.View + 1);
        _viewCombo = combo;
        _viewPopupHost = popupHost;
        combo.Opening += OnViewOpening;
        combo.SelectionChanged += OnViewSelectionChanged;
        viewNode.NewChild<UITextComponent>(out UITextComponent label);
        _viewLabel = label;
        label.Text = ViewLabel(_rows.View);
        label.VerticalAlignment = EVerticalAlignment.Center;
        UpdateCaptureState();
    }

    private static string ViewLabel(ENativeProfilerView view) => view switch
    {
        ENativeProfilerView.CpuTimings => "CPU Timings",
        ENativeProfilerView.CpuRoots => "CPU Roots",
        ENativeProfilerView.ComponentTimings => "Components",
        ENativeProfilerView.BvhMetrics => "BVH Metrics",
        ENativeProfilerView.JobSystem => "Job System",
        ENativeProfilerView.ThreadAllocations => "Allocations",
        ENativeProfilerView.MainThreadInvokes => "Invokes",
        ENativeProfilerView.FpsSpikes => "FPS Spikes",
        ENativeProfilerView.RenderStats => "Render Stats",
        ENativeProfilerView.GpuTimings => "GPU Timings",
        ENativeProfilerView.Settings => "Settings",
        ENativeProfilerView.RendererState => "Renderer",
        ENativeProfilerView.SceneAssets => "Scene Assets",
        ENativeProfilerView.FrameLifecycle => "Lifecycle",
        ENativeProfilerView.FrameOutputs => "Outputs",
        _ => "Profiler",
    };

    private UIBoundableTransform CreateValueCell(SceneNode parent)
    {
        SceneNode cell = new(parent) { Name = "value" };
        UIBoundableTransform bounds = cell.SetTransform<UIBoundableTransform>();
        _valueCells.Add(bounds, new NativeProfilerValueCellState(cell, CreateSettingBinding));
        return bounds;
    }

    private IProfilerSettingBinding CreateSettingBinding(ENativeProfilerSetting setting)
        => setting is ENativeProfilerSetting.RootMinimum or ENativeProfilerSetting.RootMaximum
            ? new NativeProfilerRootLimitBinding(this, setting == ENativeProfilerSetting.RootMinimum)
            : setting == ENativeProfilerSetting.SpikeMinimumDrop
            ? new NativeProfilerSpikeThresholdBinding(this)
            : new NativeProfilerNumericSettingBinding(setting,
                _settingsOwner is { IsCurrent: true } owner ? owner : new NativeProfilerSettingOwner());

    internal bool IsProfilerOwnerCurrent => !_terminal && _ownerActive && IsActiveInHierarchy &&
        !IsDestroyed && !IsDestroyQueued;

    private void BindValueCell(UIBoundableTransform cell, NativeProfilerDisplayRow row, ulong _)
    {
        if (!_terminal && _valueCells.TryGetValue(cell, out NativeProfilerValueCellState? state))
            state.Bind(row);
    }

    private void UnbindValueCell(UIBoundableTransform cell)
    {
        if (_valueCells.TryGetValue(cell, out NativeProfilerValueCellState? state))
            state.Unbind();
    }

    private void OnViewOpening(UIComboBoxComponent combo)
    {
        if (ReferenceEquals(_viewCombo, combo) && _viewPopupHost is { } host)
            combo.PopupPosition = host.GetPosition();
    }

    private void OnViewSelectionChanged(UIValueChange<ulong?> change)
    {
        if (_terminal || !_ownerActive || !IsActiveInHierarchy || change.NewValue is not { } id ||
            id == 0 || id > (ulong)ViewOptions.Length)
            return;
        ENativeProfilerView view = (ENativeProfilerView)(id - 1);
        int previousCount = _rows.Count;
        if (!_rows.SelectView(view)) return;
        _rootViewRevision++;
        if (view == ENativeProfilerView.CpuRoots)
            _rows.SetRootParameters(_rootMinimumMilliseconds, _rootMaximumMilliseconds, _sortRootsByTime);
        ResetTrend(view);
        if (view == ENativeProfilerView.CpuTimings)
            _worstCpuFrame.Reset();
        _invokesValid = false;
        _fpsViewInitialized = false;
        if (_viewLabel is not null) _viewLabel.Text = ViewLabel(view);
        if (_terminal || !_ownerActive || !IsActiveInHierarchy) return;
        if (!_paused || view is ENativeProfilerView.Settings or ENativeProfilerView.FpsSpikes)
            CaptureSelectedView();
        SignalRowsChanged(previousCount);
    }

    private void OnPauseClicked(UIInteractableComponent _) => TogglePause();

    private void OnProfilerRowActivated(ulong id)
    {
        if (_terminal || !_ownerActive || !IsActiveInHierarchy ||
            !_rows.TryGetIndexOfItemId(id, out int index))
            return;
        if (_rows.View == ENativeProfilerView.FpsSpikes)
        {
            if (_rows.GetItem(index).Scope != "Tracked hot paths" || !_spikes.Clear()) return;
            _fpsViewInitialized = false;
            RefreshRows();
            return;
        }
        if (_rows.View == ENativeProfilerView.CpuRoots)
        {
            if (index == 1)
            {
                _sortRootsByTime = !_sortRootsByTime;
                int previousCount = _rows.Count;
                if (_rows.SetRootParameters(_rootMinimumMilliseconds, _rootMaximumMilliseconds, _sortRootsByTime))
                {
                    _rootViewRevision++;
                    SignalRowsChanged(previousCount);
                }
            }
            else if (_rows.TryGetRootAtRow(index, out NativeProfilerRootAggregate? root) && root is not null)
            {
                _selectedCpuRootName = root.Name;
                _selectedCpuRootKind = root.Kind;
                int previousCount = _rows.Count;
                if (_rows.SelectRoot(root.Name, root.Kind))
                    SignalRowsChanged(previousCount);
                ResetTrend(ENativeProfilerView.CpuRoots);
                SampleTrend();
            }
            return;
        }
        if (_rows.View == ENativeProfilerView.GpuTimings)
        {
            NativeProfilerDisplayRow gpuRow = _rows.GetItem(index);
            if (gpuRow.GpuRankMetric >= 0)
            {
                SelectGpuRankPage(id, index, gpuRow);
                return;
            }
            if (gpuRow.GpuHistoryGeneration != 0)
            {
                SelectGpuWorstFrame(id, index, gpuRow);
                return;
            }
            string? pipeline = gpuRow.PipelineName;
            if (pipeline is null) return;
            _selectedGpuPipeline = pipeline;
            ClearSelectedGpuWorstTree();
            ClearGpuRankPage();
            ResetTrend(ENativeProfilerView.GpuTimings);
            SampleTrend();
            RefreshRows();
            return;
        }
        if (_rows.View != ENativeProfilerView.Settings) return;
        NativeProfilerSettingOwner? owner = _settingsOwner;
        if (owner is not { IsCurrent: true }) return;
        NativeProfilerDisplayRow sourceRow = _rows.GetItem(index);
        if (sourceRow.OwnerRevision != _settingsOwnerRevision || sourceRow.SettingIndex < 0)
            return;
        var debug = owner.EffectiveDebug;
        switch ((ENativeProfilerSetting)sourceRow.SettingIndex)
        {
            case ENativeProfilerSetting.Pause:
                TogglePause();
                return;
            case ENativeProfilerSetting.ComponentTiming:
                NativeProfilerSettingWriter.Set(owner, !debug.EnableProfilerComponentTiming,
                    static current => current.EnableProfilerComponentTiming,
                    static overrides => overrides.EnableProfilerComponentTimingOverride,
                    static (global, enabled) => global.EnableProfilerComponentTiming = enabled);
                break;
            case ENativeProfilerSetting.FrameLogging:
                NativeProfilerSettingWriter.Set(owner, !debug.EnableProfilerFrameLogging,
                    static current => current.EnableProfilerFrameLogging,
                    static overrides => overrides.EnableProfilerFrameLoggingOverride,
                    static (global, enabled) => global.EnableProfilerFrameLogging = enabled);
                break;
            case ENativeProfilerSetting.RenderStatsTracking:
                NativeProfilerSettingWriter.Set(owner, !debug.EnableRenderStatisticsTracking,
                    static current => current.EnableRenderStatisticsTracking,
                    static overrides => overrides.EnableRenderStatisticsTrackingOverride,
                    static (global, enabled) => global.EnableRenderStatisticsTracking = enabled);
                break;
            case ENativeProfilerSetting.AllocationTracking:
                NativeProfilerSettingWriter.Set(owner, !debug.EnableThreadAllocationTracking,
                    static current => current.EnableThreadAllocationTracking,
                    static overrides => overrides.EnableThreadAllocationTrackingOverride,
                    static (global, enabled) => global.EnableThreadAllocationTracking = enabled);
                break;
            case ENativeProfilerSetting.InvokeDiagnostics:
                NativeProfilerSettingWriter.Set(owner, !debug.EnableMainThreadInvokeDiagnostics,
                    static current => current.EnableMainThreadInvokeDiagnostics,
                    static overrides => overrides.EnableMainThreadInvokeDiagnosticsOverride,
                    static (global, enabled) => global.EnableMainThreadInvokeDiagnostics = enabled);
                break;
            case ENativeProfilerSetting.GpuTiming:
                NativeProfilerSettingWriter.Set(owner, !debug.EnableGpuRenderPipelineProfiling,
                    static current => current.EnableGpuRenderPipelineProfiling,
                    static overrides => overrides.EnableGpuRenderPipelineProfilingOverride,
                    static (global, enabled) => global.EnableGpuRenderPipelineProfiling = enabled);
                break;
            case ENativeProfilerSetting.UdpSending:
                NativeProfilerSettingWriter.Set(owner, !debug.EnableProfilerUdpSending,
                    static current => current.EnableProfilerUdpSending,
                    static overrides => overrides.EnableProfilerUdpSendingOverride,
                    static (global, enabled) => global.EnableProfilerUdpSending = enabled);
                break;
            case ENativeProfilerSetting.CpuRawLine:
                NativeProfilerSettingWriter.Set(owner, !debug.ProfilerPanelShowCpuTimingRawMsLine,
                    static current => current.ProfilerPanelShowCpuTimingRawMsLine,
                    static overrides => overrides.ProfilerPanelShowCpuTimingRawMsLineOverride,
                    static (global, enabled) => global.ProfilerPanelShowCpuTimingRawMsLine = enabled);
                break;
            case ENativeProfilerSetting.CpuSmoothedLine:
                NativeProfilerSettingWriter.Set(owner, !debug.ProfilerPanelShowCpuTimingSmoothedMsLine,
                    static current => current.ProfilerPanelShowCpuTimingSmoothedMsLine,
                    static overrides => overrides.ProfilerPanelShowCpuTimingSmoothedMsLineOverride,
                    static (global, enabled) => global.ProfilerPanelShowCpuTimingSmoothedMsLine = enabled);
                break;
            case ENativeProfilerSetting.GpuRawLine:
                NativeProfilerSettingWriter.Set(owner, !debug.ProfilerPanelShowGpuTimingRawMsLine,
                    static current => current.ProfilerPanelShowGpuTimingRawMsLine,
                    static overrides => overrides.ProfilerPanelShowGpuTimingRawMsLineOverride,
                    static (global, enabled) => global.ProfilerPanelShowGpuTimingRawMsLine = enabled);
                break;
            case ENativeProfilerSetting.GpuSmoothedLine:
                NativeProfilerSettingWriter.Set(owner, !debug.ProfilerPanelShowGpuTimingSmoothedMsLine,
                    static current => current.ProfilerPanelShowGpuTimingSmoothedMsLine,
                    static overrides => overrides.ProfilerPanelShowGpuTimingSmoothedMsLineOverride,
                    static (global, enabled) => global.ProfilerPanelShowGpuTimingSmoothedMsLine = enabled);
                break;
            case ENativeProfilerSetting.CpuInterpolation:
                NativeProfilerSettingWriter.Set(owner, !debug.ProfilerPanelInterpolateCpuTimingGraphs,
                    static current => current.ProfilerPanelInterpolateCpuTimingGraphs,
                    static overrides => overrides.ProfilerPanelInterpolateCpuTimingGraphsOverride,
                    static (global, enabled) => global.ProfilerPanelInterpolateCpuTimingGraphs = enabled);
                break;
            case ENativeProfilerSetting.GpuInterpolation:
                NativeProfilerSettingWriter.Set(owner, !debug.ProfilerPanelInterpolateGpuTimingGraphs,
                    static current => current.ProfilerPanelInterpolateGpuTimingGraphs,
                    static overrides => overrides.ProfilerPanelInterpolateGpuTimingGraphsOverride,
                    static (global, enabled) => global.ProfilerPanelInterpolateGpuTimingGraphs = enabled);
                break;
            case ENativeProfilerSetting.CpuDisplayMode:
                NativeProfilerSettingWriter.Set(owner, NextDisplayMode(debug.ProfilerPanelCpuTimingDisplayMode),
                    static current => current.ProfilerPanelCpuTimingDisplayMode,
                    static overrides => overrides.ProfilerPanelCpuTimingDisplayModeOverride,
                    static (global, value) => global.ProfilerPanelCpuTimingDisplayMode = value);
                break;
            case ENativeProfilerSetting.GpuDisplayMode:
                NativeProfilerSettingWriter.Set(owner, NextDisplayMode(debug.ProfilerPanelGpuTimingDisplayMode),
                    static current => current.ProfilerPanelGpuTimingDisplayMode,
                    static overrides => overrides.ProfilerPanelGpuTimingDisplayModeOverride,
                    static (global, value) => global.ProfilerPanelGpuTimingDisplayMode = value);
                break;
            default:
                return;
        }
        if (_terminal || !_ownerActive || !IsActiveInHierarchy || !owner.IsCurrent ||
            _rows.View != ENativeProfilerView.Settings ||
            !_rows.TryGetIndexOfItemId(id, out int acceptedIndex) || acceptedIndex != index ||
            _rows.GetItem(index).OwnerRevision != sourceRow.OwnerRevision ||
            _rows.GetItem(index).SettingIndex != sourceRow.SettingIndex)
            return;
        UpdateCaptureState();
        RefreshRows();
    }

    private static ProfilerTimingDisplayMode NextDisplayMode(ProfilerTimingDisplayMode current)
        => current switch
        {
            ProfilerTimingDisplayMode.Latest => ProfilerTimingDisplayMode.Average,
            ProfilerTimingDisplayMode.Average => ProfilerTimingDisplayMode.Worst,
            _ => ProfilerTimingDisplayMode.Latest,
        };

    private void OnCaptureClicked(UIInteractableComponent _)
    {
        if (_terminal || !_ownerActive || !IsActiveInHierarchy) return;
        NativeProfilerSettingOwner owner = new();
        var debug = owner.EffectiveDebug;
        NativeProfilerSettingWriter.Set(owner, !debug.EnableProfilerComponentTiming,
            static current => current.EnableProfilerComponentTiming,
            static overrides => overrides.EnableProfilerComponentTimingOverride,
            static (global, enabled) => global.EnableProfilerComponentTiming = enabled);
        if (_terminal || !_ownerActive || !IsActiveInHierarchy || !owner.IsCurrent) return;
        UpdateCaptureState();
        if (_rows.View == ENativeProfilerView.Settings ||
            _rows.View == ENativeProfilerView.ComponentTimings && !_paused)
            RefreshRows();
    }

    private void OnGpuDumpClicked(UIInteractableComponent _)
    {
        if (_terminal || !_ownerActive || !IsActiveInHierarchy ||
            _rows.View != ENativeProfilerView.GpuTimings)
            return;
        bool success = RuntimeEngine.Rendering.Stats.GpuPipelineProfiler
            .TryDumpAllGpuRenderPipelineTimingHistories(out string[] files, out string? error);
        if (_terminal || !_ownerActive || !IsActiveInHierarchy) return;
        _gpuDumpStatus = success
            ? $"Saved {files.Length} timing histories."
            : error ?? "No GPU timing history is available to dump.";
        RefreshRows();
    }

    private void OnCpuDumpClicked(UIInteractableComponent _)
    {
        if (_terminal || !_ownerActive || !IsActiveInHierarchy)
            return;
        ProfilerDiagnosticDumps.DumpResult result = ProfilerDiagnosticDumps.DumpCpuFrameTimingHistory();
        if (_terminal || !_ownerActive || !IsActiveInHierarchy)
            return;
        if (_cpuDumpButton?.TextComponent is { } label)
            label.Text = result.Success ? "CPU saved" : "CPU dump failed";
    }

    private void OnExternalProfilerClicked(UIInteractableComponent source)
    {
        if (_terminal || !_ownerActive || !IsActiveInHierarchy ||
            !ReferenceEquals(_externalProfilerButton, source))
            return;
        bool launched = EditorImGuiUI.TryLaunchExternalProfiler(out string? error);
        if (_terminal || !_ownerActive || !IsActiveInHierarchy ||
            !ReferenceEquals(_externalProfilerButton, source))
            return;
        _externalProfilerStatus = launched ? "External profiler started." :
            error ?? "The external profiler did not start.";
        RefreshRows();
    }

    private void UpdateCaptureState()
    {
        if (_captureButton is not null)
            _captureButton.IsSelected = Engine.EditorPreferences.Debug.EnableProfilerComponentTiming;
    }

    private void TogglePause()
    {
        if (_terminal || !_ownerActive || !IsActiveInHierarchy) return;
        SetField(ref _paused, !_paused, publishNotifications: false);
        if (_pauseButton?.TextComponent is { } label)
            label.Text = _paused ? "Resume" : "Pause";
        if (_paused)
        {
            _rows.CancelPending();
            if (_rows.View == ENativeProfilerView.Settings)
            {
                int previousCount = _rows.Count;
                _diagnosticScratch.Clear();
                CaptureSettings(_diagnosticScratch);
                if (_rows.SetDiagnostics(CollectionsMarshal.AsSpan(_diagnosticScratch)))
                    SignalRowsChanged(previousCount);
            }
        }
        else RefreshRows();
    }

    private void RefreshTick()
    {
        if (_terminal || !_ownerActive || !IsActiveInHierarchy) return;
        if (_paused)
        {
            AdvanceRows();
            return;
        }
        DateTime now = DateTime.UtcNow;
        float interval = Engine.EditorPreferences.Debug.ProfilerPanelUpdateIntervalSeconds;
        if (!float.IsFinite(interval))
            interval = 0.25f;
        if (now - _lastRefresh >= TimeSpan.FromSeconds(interval))
        {
            _lastRefresh = now;
            UpdateCaptureState();
            int previousCount = _rows.Count;
            if (CaptureSelectedView())
                SignalRowsChanged(previousCount);
            SampleTrend();
        }
        AdvanceRows();
        AnimateTrend();
    }

    protected override void RefreshRows()
    {
        if (_terminal || _paused && _rows.View is not (ENativeProfilerView.Settings or ENativeProfilerView.FpsSpikes)) return;
        int previousCount = _rows.Count;
        if (CaptureSelectedView())
            SignalRowsChanged(previousCount);
        AdvanceRows();
    }

    private bool CaptureSelectedView()
    {
        switch (_rows.View)
        {
            case ENativeProfilerView.CpuTimings:
                Engine.Profiler.TryGetSnapshot(out var timingFrame, out _);
                _worstCpuFrame.Observe(timingFrame, DateTime.UtcNow);
                _rows.Request(_worstCpuFrame.DisplayFrame, _worstCpuFrame.IsCompletedWindow);
                return false;
            case ENativeProfilerView.CpuRoots:
                Engine.Profiler.TryGetSnapshot(out var cpu, out _);
                _rows.Request(cpu);
                return false;
            case ENativeProfilerView.ComponentTimings:
                Engine.Profiler.TryGetSnapshot(out var component, out _);
                return _rows.SetComponentFrame(component, Engine.Profiler.EnableComponentTiming);
            default:
                _diagnosticScratch.Clear();
                if (!CaptureSmallDiagnostic(_rows.View, _diagnosticScratch))
                    return false;
                return _rows.SetDiagnostics(CollectionsMarshal.AsSpan(_diagnosticScratch));
        }
    }

    private void AdvanceRows()
    {
        int previousCount = _rows.Count;
        if (_rows.Advance(MaximumIndexStepsPerTick))
            SignalRowsChanged(previousCount);
    }
}
