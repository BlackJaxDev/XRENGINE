using System.Drawing;
using XREngine.AgentOrchestration;
using XREngine.LocalAgentBroker.Shared;

namespace XREngine.LocalAgentBroker.Tray;

/// <summary>Clean prompt-history browser with live response presentation.</summary>
internal sealed class BrokerHistoryForm : Form
{
    private readonly TextBox _searchBox;
    private readonly ListView _runList;
    private readonly Label _titleLabel;
    private readonly Label _metadataLabel;
    private readonly BrokerConversationView _conversation;
    private readonly ToolStripButton _deleteButton;
    private IReadOnlyList<BrokerHistoryRecord> _allRecords = [];
    private string? _selectedRunId;
    private string _renderedListVersion = string.Empty;
    private bool _isDarkTheme;
    private bool _allowClose;

    public BrokerHistoryForm(string previewUserDataDirectory)
    {
        Text = "Local Agent Broker — Prompt History";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(850, 560);
        Size = new Size(1160, 760);
        BackColor = Color.FromArgb(245, 247, 250);
        Font = new Font("Segoe UI", 9F);

        var toolbar = new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden,
            Padding = new Padding(8, 5, 8, 5),
            BackColor = Color.White,
            RenderMode = ToolStripRenderMode.System,
        };
        toolbar.Items.Add(new ToolStripLabel("Search"));
        _searchBox = new TextBox { Width = 260, PlaceholderText = "Prompt, response, model..." };
        _searchBox.TextChanged += (_, _) => HandleSearchChanged();
        toolbar.Items.Add(new ToolStripControlHost(_searchBox) { Margin = new Padding(6, 0, 12, 0) });
        toolbar.Items.Add(new ToolStripSeparator());
        _deleteButton = new ToolStripButton("Delete selected") { Enabled = false };
        _deleteButton.Click += (_, _) => RequestDeleteSelected();
        toolbar.Items.Add(_deleteButton);
        var rawButton = new ToolStripButton("Raw text") { CheckOnClick = true };
        toolbar.Items.Add(rawButton);
        var copyButton = new ToolStripButton("Copy response");
        copyButton.Click += (_, _) => CopyResponse();
        toolbar.Items.Add(copyButton);
        var settingsButton = new ToolStripButton("Settings");
        settingsButton.Click += (_, _) => SettingsRequested?.Invoke();
        toolbar.Items.Add(settingsButton);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 390,
            FixedPanel = FixedPanel.Panel1,
            BackColor = Color.FromArgb(220, 224, 230),
        };
        split.Panel1.Padding = new Padding(8);
        split.Panel2.Padding = new Padding(16, 12, 16, 16);

        _runList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
        };
        _runList.Columns.Add("Status", 78);
        _runList.Columns.Add("Prompt", 205);
        _runList.Columns.Add("Started", 90);
        _runList.SelectedIndexChanged += (_, _) => SelectFromList();
        split.Panel1.Controls.Add(_runList);

        var details = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.White,
            Padding = new Padding(18),
        };
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _titleLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            Font = new Font("Segoe UI Semibold", 16F),
            ForeColor = Color.FromArgb(30, 36, 46),
            Margin = new Padding(0, 0, 0, 6),
        };
        _metadataLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(95, 104, 120),
            Margin = new Padding(0, 0, 0, 16),
        };
        _conversation = new BrokerConversationView(previewUserDataDirectory) { Dock = DockStyle.Fill };
        rawButton.CheckedChanged += (_, _) => _conversation.SetRawView(rawButton.Checked);
        details.Controls.Add(_titleLabel, 0, 0);
        details.Controls.Add(_metadataLabel, 0, 1);
        details.Controls.Add(_conversation, 0, 2);
        split.Panel2.Controls.Add(details);

        Controls.Add(split);
        Controls.Add(toolbar);
        toolbar.Dock = DockStyle.Top;
        FormClosing += HandleFormClosing;
    }

    public event Action<string>? DeleteRecordRequested;

    public event Action? SettingsRequested;

    public void UpdateRecords(IReadOnlyList<BrokerHistoryRecord> records)
    {
        _allRecords = records;
        string? currentSelection = _selectedRunId;
        bool listChanged = RebuildList();
        if (listChanged && currentSelection is not null)
            SelectRun(currentSelection);
        RenderSelected();
    }

    public void SetTheme(BrokerUiThemePreference preference)
    {
        bool isDarkTheme = BrokerTheme.ResolveDark(preference);
        if (isDarkTheme == _isDarkTheme && IsHandleCreated)
            return;

        _isDarkTheme = isDarkTheme;
        BrokerTheme.Apply(this, _isDarkTheme);
        _titleLabel.ForeColor = BrokerTheme.TextColor(_isDarkTheme);
        _metadataLabel.ForeColor = BrokerTheme.MutedTextColor(_isDarkTheme);
        _conversation.BackColor = BrokerTheme.SurfaceColor(_isDarkTheme);
        _conversation.ForeColor = BrokerTheme.TextColor(_isDarkTheme);
        _conversation.SetTheme(_isDarkTheme);
        RebuildList(force: true);
        if (_selectedRunId is not null)
            SelectRun(_selectedRunId);
        RenderSelected();
    }

    public void ShowRecord(string? runId)
    {
        if (!Visible)
            Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();

        string? target = runId
            ?? _selectedRunId
            ?? _allRecords.FirstOrDefault(static record => record.IsActive)?.RunId
            ?? _allRecords.FirstOrDefault()?.RunId;
        if (target is not null)
            SelectRun(target);
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        BrokerTheme.ApplyTitleBar(this, _isDarkTheme);
    }

    private bool RebuildList(bool force = false)
    {
        string filter = _searchBox.Text.Trim();
        BrokerHistoryRecord[] filtered = _allRecords
            .Where(record => MatchesFilter(record, filter))
            .OrderByDescending(static record => record.IsActive)
            .ThenByDescending(static record => record.CreatedUtc)
            .ToArray();
        string listVersion = filter + "\n" + string.Join(
            '\n',
            filtered.Select(static record =>
                $"{record.RunId}:{record.Status}:{record.CreatedUtc.UtcTicks}:{record.Objective}"));
        if (!force && string.Equals(listVersion, _renderedListVersion, StringComparison.Ordinal))
            return false;
        _renderedListVersion = listVersion;

        _runList.BeginUpdate();
        try
        {
            _runList.Items.Clear();
            foreach (BrokerHistoryRecord record in filtered)
            {
                var item = new ListViewItem(StatusLabel(record))
                {
                    Tag = record.RunId,
                    ForeColor = StatusColor(record.Status, _isDarkTheme),
                };
                item.SubItems.Add(OneLine(record.Objective));
                item.SubItems.Add(record.CreatedUtc.ToLocalTime().ToString("g"));
                _runList.Items.Add(item);
            }
        }
        finally
        {
            _runList.EndUpdate();
        }
        return true;
    }

    private void HandleSearchChanged()
    {
        string? currentSelection = _selectedRunId;
        RebuildList(force: true);
        if (currentSelection is not null)
            SelectRun(currentSelection);
        RenderSelected();
    }

    private void SelectFromList()
    {
        if (_runList.SelectedItems.Count == 0)
            return;
        string? selectedRunId = _runList.SelectedItems[0].Tag as string;
        _selectedRunId = selectedRunId;
        RenderSelected();
    }

    private void SelectRun(string runId)
    {
        foreach (ListViewItem item in _runList.Items)
        {
            if (!string.Equals(item.Tag as string, runId, StringComparison.Ordinal))
                continue;
            item.Selected = true;
            item.Focused = true;
            item.EnsureVisible();
            _selectedRunId = runId;
            RenderSelected();
            return;
        }
    }

    private void RenderSelected()
    {
        BrokerHistoryRecord? record = _allRecords.FirstOrDefault(candidate =>
            string.Equals(candidate.RunId, _selectedRunId, StringComparison.Ordinal));
        if (record is null)
        {
            _titleLabel.Text = _allRecords.Count == 0 ? "No prompt history yet" : "Select a prompt";
            _metadataLabel.Text = _allRecords.Count == 0
                ? "Accepted broker prompts will appear here automatically."
                : string.Empty;
            _conversation.ShowRecord(null);
            _deleteButton.Enabled = false;
            return;
        }

        _titleLabel.Text = string.IsNullOrWhiteSpace(record.Objective)
            ? "Untitled broker prompt"
            : record.Objective.Trim();
        string model = string.IsNullOrWhiteSpace(record.ActualModel)
            ? record.RequestedModel
            : $"{record.RequestedModel} → {record.ActualModel}";
        string status = StatusLabel(record);
        if (record.Status == AgentRunStatus.Running
            && !string.IsNullOrWhiteSpace(record.ProgressMessage))
        {
            status += $" ({record.ProgressMessage.Replace('_', ' ')})";
        }
        _metadataLabel.Text =
            $"{status}  ·  {model}  ·  {record.CreatedUtc.ToLocalTime():g}  ·  "
            + $"{record.Usage.InputTokens:N0} in / {record.Usage.OutputTokens:N0} out";
        _deleteButton.Enabled = !record.IsActive;

        _conversation.ShowRecord(record);
    }

    private void RequestDeleteSelected()
    {
        BrokerHistoryRecord? record = _allRecords.FirstOrDefault(candidate =>
            string.Equals(candidate.RunId, _selectedRunId, StringComparison.Ordinal));
        if (record is null || record.IsActive)
            return;
        DialogResult result = MessageBox.Show(
            this,
            "Delete this prompt and response record? This cannot be undone.",
            "Delete prompt record",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);
        if (result == DialogResult.OK)
            DeleteRecordRequested?.Invoke(record.RunId);
    }

    private void HandleFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (_allowClose || eventArgs.CloseReason != CloseReason.UserClosing)
            return;
        eventArgs.Cancel = true;
        Hide();
    }

    private void CopyResponse()
    {
        string? response = _allRecords.FirstOrDefault(record => record.RunId == _selectedRunId)?.ResponseText;
        if (string.IsNullOrEmpty(response))
            return;
        try
        {
            Clipboard.SetText(response);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, "The clipboard is busy. Please try again.", "Copy response", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private static bool MatchesFilter(BrokerHistoryRecord record, string filter)
        => filter.Length == 0
            || record.Objective.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || record.PromptText.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || record.ResponseText.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || record.RequestedModel.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || record.ActualModel.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static string StatusLabel(BrokerHistoryRecord record)
        => record.Status switch
        {
            AgentRunStatus.Queued => "Queued",
            AgentRunStatus.Running => "Running",
            AgentRunStatus.Completed => "Completed",
            AgentRunStatus.Failed => "Failed",
            AgentRunStatus.Cancelled => "Cancelled",
            _ => record.Status.ToString(),
        };

    private static Color StatusColor(AgentRunStatus status, bool dark)
        => status switch
        {
            AgentRunStatus.Queued => dark ? Color.FromArgb(224, 174, 76) : Color.FromArgb(166, 111, 21),
            AgentRunStatus.Running => dark ? Color.FromArgb(105, 169, 245) : Color.FromArgb(35, 105, 185),
            AgentRunStatus.Completed => dark ? Color.FromArgb(91, 190, 127) : Color.FromArgb(42, 132, 82),
            AgentRunStatus.Failed => dark ? Color.FromArgb(242, 112, 112) : Color.FromArgb(185, 53, 53),
            AgentRunStatus.Cancelled => dark ? Color.FromArgb(165, 169, 178) : Color.FromArgb(110, 110, 118),
            _ => BrokerTheme.TextColor(dark),
        };

    private static string OneLine(string text)
    {
        string value = text.ReplaceLineEndings(" ").Trim();
        return value.Length == 0 ? "Untitled prompt" : value;
    }
}
