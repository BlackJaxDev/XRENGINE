using System.Drawing;
using XREngine.AgentOrchestration;
using XREngine.LocalAgentBroker.Shared;

namespace XREngine.LocalAgentBroker.Tray;

/// <summary>Owns tray lifetime, live history refresh, and idle/retention policies.</summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private static readonly TimeSpan s_refreshInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan s_cleanupInterval = TimeSpan.FromMinutes(1);

    private readonly BrokerHistoryStore _store;
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly FileSystemWatcher _historyWatcher;
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
    private readonly HashSet<string> _knownRunIds = new(StringComparer.Ordinal);
    private BrokerHistoryForm? _historyForm;
    private BrokerUiSettings _settings;
    private IReadOnlyList<BrokerHistoryRecord> _records = [];
    private DateTimeOffset _lastCleanupUtc = DateTimeOffset.MinValue;
    private string _activeMenuSignature = string.Empty;
    private string? _notificationRunId;
    private bool? _appliedDarkTheme;
    private int _historyRefreshScheduled;
    private bool _exiting;

    public TrayApplicationContext(string repositoryRoot)
    {
        var paths = new BrokerUiPaths(repositoryRoot);
        _store = new BrokerHistoryStore(paths);
        _settings = _store.LoadSettings();
        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "XRENGINE Agent Broker",
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowHistory();
        _notifyIcon.BalloonTipClicked += (_, _) => ShowHistory(_notificationRunId);

        _historyWatcher = new FileSystemWatcher(paths.RunsDirectory, "*.json")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _historyWatcher.Created += HandleHistoryFileChanged;
        _historyWatcher.Changed += HandleHistoryFileChanged;
        _historyWatcher.Deleted += HandleHistoryFileChanged;
        _historyWatcher.Renamed += HandleHistoryFileChanged;
        _historyWatcher.EnableRaisingEvents = true;

        _refreshTimer = new System.Windows.Forms.Timer
        {
            Interval = (int)s_refreshInterval.TotalMilliseconds,
            Enabled = true,
        };
        _refreshTimer.Tick += (_, _) => RefreshState();
        RefreshState(forceMenu: true);
    }

    protected override void ExitThreadCore()
    {
        if (_exiting)
            return;
        _exiting = true;
        _historyWatcher.EnableRaisingEvents = false;
        _historyWatcher.Dispose();
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _historyForm?.CloseForExit();
        _historyForm?.Dispose();
        base.ExitThreadCore();
    }

    private void RefreshState(bool forceMenu = false)
    {
        if (_exiting)
            return;

        CleanupExpiredRecords();
        IReadOnlyList<BrokerHistoryRecord> refreshedRecords = _store.LoadRecords();
        ShowNewPromptNotifications(refreshedRecords);
        _records = refreshedRecords;
        _historyForm?.UpdateRecords(_records);
        bool themeChanged = RefreshTheme();
        UpdateTrayMenu(forceMenu || themeChanged);
        ApplyIdleExitPolicy();
    }

    private void UpdateTrayMenu(bool force)
    {
        BrokerHistoryRecord[] activeRecords = _records
            .Where(static record => record.IsActive)
            .OrderBy(static record => record.CreatedUtc)
            .ToArray();
        string signature = string.Join(
            '|',
            activeRecords.Select(static record =>
                $"{record.RunId}:{record.Status}"));
        if (!force && string.Equals(signature, _activeMenuSignature, StringComparison.Ordinal))
            return;
        _activeMenuSignature = signature;

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem(
            activeRecords.Length == 1 ? "1 running task" : $"{activeRecords.Length} running tasks")
        {
            Enabled = false,
        });
        if (activeRecords.Length == 0)
        {
            menu.Items.Add(new ToolStripMenuItem("No prompts are active") { Enabled = false });
        }
        else
        {
            foreach (BrokerHistoryRecord record in activeRecords)
            {
                var item = new ToolStripMenuItem(TrayTaskLabel(record))
                {
                    Tag = record.RunId,
                    ToolTipText = record.Objective,
                };
                item.Click += (_, _) => ShowHistory((string)item.Tag);
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new ToolStripSeparator());
        var openItem = new ToolStripMenuItem("Open prompt history");
        openItem.Click += (_, _) => ShowHistory();
        menu.Items.Add(openItem);
        var settingsItem = new ToolStripMenuItem("Settings...");
        settingsItem.Click += (_, _) => ShowSettings();
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitThread();
        menu.Items.Add(exitItem);
        BrokerTheme.Apply(menu, _appliedDarkTheme == true);

        ContextMenuStrip? previousMenu = _notifyIcon.ContextMenuStrip;
        _notifyIcon.ContextMenuStrip = menu;
        previousMenu?.Dispose();
        _notifyIcon.Text = activeRecords.Length == 0
            ? "XRENGINE Agent Broker — idle"
            : $"XRENGINE Agent Broker — {activeRecords.Length} active";
    }

    private void ShowHistory(string? runId = null)
    {
        if (_historyForm is null || _historyForm.IsDisposed)
        {
            _historyForm = new BrokerHistoryForm();
            _historyForm.DeleteRecordRequested += DeleteRecord;
            _historyForm.SettingsRequested += ShowSettings;
            _historyForm.SetTheme(_settings.Theme);
            _historyForm.UpdateRecords(_records);
        }

        _historyForm.ShowRecord(runId);
    }

    private void ShowSettings()
    {
        using var dialog = new BrokerSettingsForm(_settings);
        if (dialog.ShowDialog(_historyForm) != DialogResult.OK)
            return;
        _settings = dialog.Settings;
        _store.SaveSettings(_settings);
        _lastCleanupUtc = DateTimeOffset.MinValue;
        RefreshState(forceMenu: true);
    }

    private bool RefreshTheme()
    {
        bool isDarkTheme = BrokerTheme.ResolveDark(_settings.Theme);
        if (_appliedDarkTheme == isDarkTheme)
            return false;

        _appliedDarkTheme = isDarkTheme;
        _historyForm?.SetTheme(_settings.Theme);
        return true;
    }

    private void HandleHistoryFileChanged(object sender, FileSystemEventArgs eventArgs)
    {
        if (_exiting
            || !eventArgs.FullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || Interlocked.Exchange(ref _historyRefreshScheduled, 1) != 0)
        {
            return;
        }

        BrokerHistoryForm? historyForm = _historyForm;
        if (historyForm is null || historyForm.IsDisposed || !historyForm.IsHandleCreated)
        {
            Interlocked.Exchange(ref _historyRefreshScheduled, 0);
            return;
        }

        try
        {
            historyForm.BeginInvoke((Action)(() =>
            {
                Interlocked.Exchange(ref _historyRefreshScheduled, 0);
                if (!_exiting && historyForm.Visible)
                    RefreshState();
            }));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _historyRefreshScheduled, 0);
        }
    }

    private void DeleteRecord(string runId)
    {
        BrokerHistoryRecord? record = _records.FirstOrDefault(candidate => candidate.RunId == runId);
        if (record?.IsActive == true)
            return;
        _store.DeleteRecord(runId);
        RefreshState();
    }

    private void CleanupExpiredRecords()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_settings.RecordRetentionHours is null
            || now - _lastCleanupUtc < s_cleanupInterval)
        {
            return;
        }

        _store.DeleteTerminalRecordsOlderThan(
            TimeSpan.FromHours(_settings.RecordRetentionHours.Value));
        _lastCleanupUtc = now;
    }

    private void ApplyIdleExitPolicy()
    {
        if (_settings.IdleExitMinutes is null || _records.Any(static record => record.IsActive))
            return;

        DateTimeOffset idleSince = _records.Count == 0
            ? _startedUtc
            : _records.Max(static record => record.UpdatedUtc);
        if (DateTimeOffset.UtcNow - idleSince
            >= TimeSpan.FromMinutes(_settings.IdleExitMinutes.Value))
        {
            ExitThread();
        }
    }

    private void ShowNewPromptNotifications(IReadOnlyList<BrokerHistoryRecord> records)
    {
        foreach (BrokerHistoryRecord record in records.OrderBy(static record => record.CreatedUtc))
        {
            if (!_knownRunIds.Add(record.RunId)
                || !_settings.NotificationsEnabled
                || record.CreatedUtc < _startedUtc - TimeSpan.FromSeconds(30))
            {
                continue;
            }

            _notificationRunId = record.RunId;
            string objective = string.IsNullOrWhiteSpace(record.Objective)
                ? "A local agent prompt was accepted."
                : record.Objective.ReplaceLineEndings(" ").Trim();
            if (objective.Length > 220)
                objective = objective[..219] + "…";
            _notifyIcon.ShowBalloonTip(
                timeout: 5_000,
                tipTitle: "Local agent prompt started",
                tipText: objective,
                tipIcon: ToolTipIcon.Info);
        }
    }

    private static string TrayTaskLabel(BrokerHistoryRecord record)
    {
        const int maximumLength = 54;
        string objective = string.IsNullOrWhiteSpace(record.Objective)
            ? record.RunId[..Math.Min(8, record.RunId.Length)]
            : record.Objective.ReplaceLineEndings(" ").Trim();
        if (objective.Length > maximumLength)
            objective = objective[..(maximumLength - 1)] + "…";
        return $"{objective}  ·  {FormatStatus(record)}";
    }

    private static string FormatStatus(BrokerHistoryRecord record)
        => record.Status switch
        {
            AgentRunStatus.Queued => "queued",
            AgentRunStatus.Running when !string.IsNullOrWhiteSpace(record.ProgressMessage)
                => record.ProgressMessage.Replace('_', ' '),
            AgentRunStatus.Running => "running",
            _ => record.Status.ToString().ToLowerInvariant(),
        };
}
