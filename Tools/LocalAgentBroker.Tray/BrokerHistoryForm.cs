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
    private readonly RichTextBox _conversation;
    private readonly MarkdownRichTextRenderer _markdownRenderer;
    private readonly RichTextBoxMotionController _motionController;
    private readonly ToolStripButton _deleteButton;
    private IReadOnlyList<BrokerHistoryRecord> _allRecords = [];
    private string? _selectedRunId;
    private string? _renderedRunId;
    private string _renderedResponseText = string.Empty;
    private string _renderedFailureText = string.Empty;
    private string _renderedListVersion = string.Empty;
    private MarkdownPreviewDocument _renderedResponsePreview = MarkdownPreviewDocument.Empty;
    private int _responsePreviewStart;
    private int _responsePreviewLength;
    private bool _renderedWasActive;
    private bool _forceFullRender;
    private bool _isDarkTheme;
    private bool _allowClose;

    public BrokerHistoryForm()
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
        _conversation = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(38, 43, 52),
            ReadOnly = true,
            DetectUrls = true,
            Font = new Font("Segoe UI", 10F),
        };
        _markdownRenderer = new MarkdownRichTextRenderer(_conversation);
        _motionController = new RichTextBoxMotionController(_conversation);
        _conversation.LinkClicked += OpenConversationLink;
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
        _markdownRenderer.SetTheme(_isDarkTheme);
        _forceFullRender = true;
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
        if (!string.Equals(selectedRunId, _selectedRunId, StringComparison.Ordinal))
            ResetRenderedConversation();
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
            if (!string.Equals(runId, _selectedRunId, StringComparison.Ordinal))
                ResetRenderedConversation();
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
            _motionController.Reset(followTail: false);
            _conversation.Clear();
            _deleteButton.Enabled = false;
            ResetRenderedConversation();
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

        string response = record.ResponseText;
        string failure = BuildFailureText(record);
        bool isSameRun = string.Equals(record.RunId, _renderedRunId, StringComparison.Ordinal);
        bool canAppendResponse = isSameRun
            && !_forceFullRender
            && HasValidResponsePreviewRange()
            && response.Length > _renderedResponseText.Length
            && response.StartsWith(_renderedResponseText, StringComparison.Ordinal)
            && string.Equals(failure, _renderedFailureText, StringComparison.Ordinal);
        if (canAppendResponse)
        {
            AppendResponseDelta(response);
            _renderedResponseText = response;
            _renderedWasActive = record.IsActive;
            return;
        }

        bool conversationUnchanged = isSameRun
            && !_forceFullRender
            && string.Equals(response, _renderedResponseText, StringComparison.Ordinal)
            && string.Equals(failure, _renderedFailureText, StringComparison.Ordinal)
            && (response.Length > 0 || record.IsActive == _renderedWasActive);
        if (conversationUnchanged)
            return;

        if (!isSameRun)
            _motionController.Reset(record.IsActive);
        RichTextUpdateState updateState = _motionController.BeginContentUpdate();
        try
        {
            _conversation.SuspendLayout();
            try
            {
                _conversation.Clear();
                if (!string.IsNullOrWhiteSpace(record.SystemInstructions))
                    AppendSection("SYSTEM", record.SystemInstructions, SystemHeadingColor());
                AppendSection("PROMPT", record.PromptText, PromptHeadingColor());
                AppendResponseSection(record, response);
                if (failure.Length > 0)
                    AppendSection("FAILURE", failure, FailureHeadingColor());
            }
            finally
            {
                _conversation.ResumeLayout();
            }

            _renderedRunId = record.RunId;
            _renderedResponseText = response;
            _renderedFailureText = failure;
            _renderedWasActive = record.IsActive;
            _forceFullRender = false;

            RichTextUpdateState completionState = isSameRun
                ? updateState
                : new RichTextUpdateState(
                    Point.Empty,
                    record.IsActive ? _conversation.TextLength : 0,
                    0,
                    record.IsActive);
            _motionController.EndContentUpdate(completionState);
        }
        catch
        {
            _motionController.AbortContentUpdate(updateState);
            throw;
        }
    }

    private void AppendSection(string heading, string body, Color headingColor)
    {
        AppendHeading(heading, headingColor);
        _conversation.AppendText(body.TrimEnd() + Environment.NewLine + Environment.NewLine);
    }

    private void AppendHeading(string heading, Color headingColor)
    {
        _conversation.SelectionColor = headingColor;
        using var headingFont = new Font(_conversation.Font, FontStyle.Bold);
        _conversation.SelectionFont = headingFont;
        _conversation.AppendText(heading + Environment.NewLine);
        _conversation.SelectionColor = _conversation.ForeColor;
        _conversation.SelectionFont = _conversation.Font;
    }

    private void AppendResponseSection(BrokerHistoryRecord record, string response)
    {
        AppendHeading("RESPONSE", ResponseHeadingColor());
        _responsePreviewStart = _conversation.TextLength;
        _renderedResponsePreview = response.Length > 0
            ? MarkdownPreviewParser.Parse(response)
            : new MarkdownPreviewDocument(
                record.IsActive ? "Waiting for output…" : "No response text was returned.",
                []);
        _conversation.AppendText(_renderedResponsePreview.Text);
        _responsePreviewLength = _renderedResponsePreview.Text.Length;
        _markdownRenderer.Apply(
            _renderedResponsePreview,
            _responsePreviewStart,
            previewStart: 0,
            fadeFromPreviewOffset: int.MaxValue);
        if (!record.IsActive)
            _conversation.AppendText(Environment.NewLine + Environment.NewLine);
    }

    private void AppendResponseDelta(string response)
    {
        MarkdownPreviewDocument preview = MarkdownPreviewParser.Parse(response);
        int commonPrefix = CommonPrefixLength(_renderedResponsePreview.Text, preview.Text);
        int replacementStart = StartOfLine(preview.Text, commonPrefix);
        replacementStart = Math.Min(replacementStart, _renderedResponsePreview.Text.Length);

        RichTextUpdateState updateState = _motionController.BeginContentUpdate();
        try
        {
            _conversation.Select(
                _responsePreviewStart + replacementStart,
                _responsePreviewLength - replacementStart);
            _conversation.SelectedText = preview.Text[replacementStart..];
            _responsePreviewLength = preview.Text.Length;
            IReadOnlyList<RichTextFadeRun> fadeRuns = _markdownRenderer.Apply(
                preview,
                _responsePreviewStart,
                replacementStart,
                commonPrefix);
            _renderedResponsePreview = preview;
            _motionController.EndContentUpdate(updateState, fadeRuns);
        }
        catch
        {
            _motionController.AbortContentUpdate(updateState);
            throw;
        }
    }

    private void ResetRenderedConversation()
    {
        _renderedRunId = null;
        _renderedResponseText = string.Empty;
        _renderedFailureText = string.Empty;
        _renderedWasActive = false;
        _renderedResponsePreview = MarkdownPreviewDocument.Empty;
        _responsePreviewStart = 0;
        _responsePreviewLength = 0;
        _forceFullRender = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _motionController.Dispose();
            _markdownRenderer.Dispose();
        }
        base.Dispose(disposing);
    }

    private static int CommonPrefixLength(string first, string second)
    {
        int length = Math.Min(first.Length, second.Length);
        int index = 0;
        while (index < length && first[index] == second[index])
            index++;
        return index;
    }

    private static int StartOfLine(string text, int offset)
    {
        int searchStart = Math.Min(offset, text.Length) - 1;
        if (searchStart < 0)
            return 0;
        int newline = text.LastIndexOf('\n', searchStart);
        return newline < 0 ? 0 : newline + 1;
    }

    private bool HasValidResponsePreviewRange()
    {
        if (_responsePreviewStart < 0
            || _responsePreviewLength < 0
            || _responsePreviewStart + _responsePreviewLength > _conversation.TextLength
            || _responsePreviewLength != _renderedResponsePreview.Text.Length)
        {
            return false;
        }

        return _conversation.Text.AsSpan(
            _responsePreviewStart,
            _responsePreviewLength).SequenceEqual(_renderedResponsePreview.Text);
    }

    private static void OpenConversationLink(object? sender, LinkClickedEventArgs eventArgs)
    {
        if (!Uri.TryCreate(eventArgs.LinkText, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true,
        });
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

    private static string BuildFailureText(BrokerHistoryRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.FailureSummary))
            return string.Empty;
        return string.IsNullOrWhiteSpace(record.FailureDetail)
            ? record.FailureSummary
            : $"{record.FailureSummary}\n\n{record.FailureDetail}";
    }

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

    private Color SystemHeadingColor()
        => _isDarkTheme ? Color.FromArgb(190, 154, 230) : Color.FromArgb(112, 86, 155);

    private Color PromptHeadingColor()
        => _isDarkTheme ? Color.FromArgb(105, 169, 245) : Color.FromArgb(38, 101, 175);

    private Color ResponseHeadingColor()
        => _isDarkTheme ? Color.FromArgb(91, 190, 127) : Color.FromArgb(36, 128, 82);

    private Color FailureHeadingColor()
        => _isDarkTheme ? Color.FromArgb(242, 112, 112) : Color.FromArgb(180, 54, 54);

    private static string OneLine(string text)
    {
        string value = text.ReplaceLineEndings(" ").Trim();
        return value.Length == 0 ? "Untitled prompt" : value;
    }
}
