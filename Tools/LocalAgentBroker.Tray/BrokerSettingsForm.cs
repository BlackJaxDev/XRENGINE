using System.Drawing;
using XREngine.LocalAgentBroker.Shared;

namespace XREngine.LocalAgentBroker.Tray;

/// <summary>Edits tray appearance, notification, lifetime, and history preferences.</summary>
internal sealed class BrokerSettingsForm : Form
{
    private readonly ComboBox _theme;
    private readonly CheckBox _neverExit;
    private readonly NumericUpDown _idleMinutes;
    private readonly CheckBox _neverCleanup;
    private readonly NumericUpDown _retentionHours;
    private readonly CheckBox _notificationsEnabled;
    private bool _isDarkTheme;

    public BrokerSettingsForm(BrokerUiSettings settings)
    {
        Text = "Local Agent Broker Settings";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(470, 380);
        Font = new Font("Segoe UI", 9F);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 9,
            Padding = new Padding(18),
        };
        for (int index = 0; index < 8; index++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(Heading("Appearance"));
        _theme = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 180,
            Margin = new Padding(0, 4, 0, 6),
        };
        _theme.Items.AddRange(["Use Windows setting", "Light", "Dark"]);
        _theme.SelectedIndex = settings.Theme switch
        {
            BrokerUiThemePreference.Light => 1,
            BrokerUiThemePreference.Dark => 2,
            _ => 0,
        };
        _theme.SelectedIndexChanged += (_, _) => ApplySelectedTheme();
        layout.Controls.Add(_theme);

        layout.Controls.Add(Heading("Notifications", new Padding(0, 12, 0, 3)));
        _notificationsEnabled = new CheckBox
        {
            Text = "Show a Windows notification when a new prompt starts",
            AutoSize = true,
            Checked = settings.NotificationsEnabled,
            Margin = new Padding(0, 4, 0, 6),
        };
        layout.Controls.Add(_notificationsEnabled);

        layout.Controls.Add(Heading("Tray lifetime", new Padding(0, 12, 0, 3)));
        var idlePanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        _neverExit = new CheckBox
        {
            Text = "Never auto-close",
            AutoSize = true,
            Checked = settings.IdleExitMinutes is null,
            Margin = new Padding(0, 7, 18, 0),
        };
        _idleMinutes = new NumericUpDown
        {
            Minimum = 1,
            Maximum = BrokerUiSettings.MaximumIdleExitMinutes,
            Value = settings.IdleExitMinutes ?? 30,
            Width = 82,
            Enabled = settings.IdleExitMinutes is not null,
        };
        _neverExit.CheckedChanged += (_, _) => _idleMinutes.Enabled = !_neverExit.Checked;
        idlePanel.Controls.Add(_neverExit);
        idlePanel.Controls.Add(new Label { Text = "Close after", AutoSize = true, Margin = new Padding(0, 7, 6, 0) });
        idlePanel.Controls.Add(_idleMinutes);
        idlePanel.Controls.Add(new Label { Text = "minutes with no active prompts", AutoSize = true, Margin = new Padding(6, 7, 0, 0) });
        layout.Controls.Add(idlePanel);

        layout.Controls.Add(Heading("Prompt records", new Padding(0, 18, 0, 3)));
        var retentionPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        _neverCleanup = new CheckBox
        {
            Text = "Never auto-delete",
            AutoSize = true,
            Checked = settings.RecordRetentionHours is null,
            Margin = new Padding(0, 7, 18, 0),
        };
        _retentionHours = new NumericUpDown
        {
            Minimum = 1,
            Maximum = BrokerUiSettings.MaximumRecordRetentionHours,
            Value = settings.RecordRetentionHours ?? 720,
            Width = 82,
            Enabled = settings.RecordRetentionHours is not null,
        };
        _neverCleanup.CheckedChanged += (_, _) => _retentionHours.Enabled = !_neverCleanup.Checked;
        retentionPanel.Controls.Add(_neverCleanup);
        retentionPanel.Controls.Add(new Label { Text = "Delete after", AutoSize = true, Margin = new Padding(0, 7, 6, 0) });
        retentionPanel.Controls.Add(_retentionHours);
        retentionPanel.Controls.Add(new Label { Text = "hours after completion", AutoSize = true, Margin = new Padding(6, 7, 0, 0) });
        layout.Controls.Add(retentionPanel);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            WrapContents = false,
            Padding = new Padding(0, 18, 0, 0),
        };
        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
        ApplySelectedTheme();
    }

    public BrokerUiSettings Settings
        => new()
        {
            NotificationsEnabled = _notificationsEnabled.Checked,
            Theme = SelectedTheme,
            IdleExitMinutes = _neverExit.Checked ? null : decimal.ToInt32(_idleMinutes.Value),
            RecordRetentionHours = _neverCleanup.Checked
                ? null
                : decimal.ToInt32(_retentionHours.Value),
        };

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        BrokerTheme.ApplyTitleBar(this, _isDarkTheme);
    }

    private BrokerUiThemePreference SelectedTheme
        => _theme.SelectedIndex switch
        {
            1 => BrokerUiThemePreference.Light,
            2 => BrokerUiThemePreference.Dark,
            _ => BrokerUiThemePreference.System,
        };

    private void ApplySelectedTheme()
    {
        _isDarkTheme = BrokerTheme.ResolveDark(SelectedTheme);
        BrokerTheme.Apply(this, _isDarkTheme);
    }

    private static Label Heading(string text, Padding? margin = null)
        => new()
        {
            Text = text,
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 11F),
            ForeColor = Color.FromArgb(35, 41, 52),
            Margin = margin ?? new Padding(0, 0, 0, 3),
        };
}
