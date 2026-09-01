using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using XREngine.LocalAgentBroker.Shared;

namespace XREngine.LocalAgentBroker.Tray;

/// <summary>Hosts an offline, untrusted-content Markdown/math preview with an explicit raw-text fallback.</summary>
internal sealed class BrokerConversationView : UserControl
{
    private const string PreviewHost = "broker-preview.invalid";
    private const string PreviewUrl = "https://" + PreviewHost + "/index.html";
    private readonly WebView2 _browser = new() { Dock = DockStyle.Fill };
    private readonly RichTextBox _raw = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
        DetectUrls = false, Font = new Font("Cascadia Mono", 10F),
    };
    private readonly Label _notice = new() { Dock = DockStyle.Top, AutoSize = true };
    private readonly string _userDataDirectory;
    private readonly RichTextBoxMotionController _rawMotion;
    private BrokerHistoryRecord? _record;
    private bool _dark;
    private bool _showRaw;
    private bool _started;
    private bool _ready;
    private bool _failed;
    private string? _lastPayload;
    private string? _rawRunId;

    public BrokerConversationView(string userDataDirectory)
    {
        _userDataDirectory = userDataDirectory;
        _rawMotion = new RichTextBoxMotionController(_raw);
        Controls.Add(_browser);
        Controls.Add(_raw);
        Controls.Add(_notice);
        UpdateVisibility();
    }

    public void ShowRecord(BrokerHistoryRecord? record)
    {
        _record = record;
        if (_showRaw || !_ready)
            UpdateRaw();
        SendSnapshot();
    }

    public void SetTheme(bool dark)
    {
        _dark = dark;
        BackColor = _raw.BackColor = _notice.BackColor = BrokerTheme.SurfaceColor(dark);
        ForeColor = _raw.ForeColor = _notice.ForeColor = BrokerTheme.TextColor(dark);
        _browser.DefaultBackgroundColor = BackColor;
        SendSnapshot();
    }

    public void SetRawView(bool showRaw)
    {
        _showRaw = showRaw;
        if (showRaw)
            UpdateRaw();
        UpdateVisibility();
    }

    protected override async void OnLoad(EventArgs eventArgs)
    {
        base.OnLoad(eventArgs);
        if (_started)
            return;
        _started = true;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _userDataDirectory);
            if (IsDisposed)
                return;
            await _browser.EnsureCoreWebView2Async(environment);
            if (IsDisposed)
                return;
            CoreWebView2 core = _browser.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.SetVirtualHostNameToFolderMapping(
                PreviewHost, Path.Combine(AppContext.BaseDirectory, "Preview"),
                CoreWebView2HostResourceAccessKind.Deny);
            core.NavigationStarting += (_, args) => args.Cancel = args.Uri != PreviewUrl;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out Uri? uri)
                    || uri.Scheme != "https" || uri.Host != PreviewHost || !uri.IsDefaultPort)
                {
                    args.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                }
            };
            core.WebMessageReceived += HandleMessage;
            core.ProcessFailed += (_, _) => ShowFailure("The preview process stopped. Reopen the broker app to retry.");
            core.NavigationCompleted += async (_, args) =>
            {
                if (!args.IsSuccess)
                    ShowFailure($"The bundled preview could not load ({args.WebErrorStatus}).");
                else
                {
                    try
                    {
                        // A missing/broken local script must not leave a blank preview indefinitely.
                        string loaded = await core.ExecuteScriptAsync("window.brokerPreviewReady === true");
                        if (!IsDisposed && loaded != "true")
                            ShowFailure("The bundled preview scripts could not initialize.");
                    }
                    catch (Exception exception)
                    {
                        if (!IsDisposed)
                            ShowFailure(exception.Message);
                    }
                }
            };
            core.Navigate(PreviewUrl);
        }
        catch (Exception exception)
        {
            if (!IsDisposed)
                ShowFailure("Math preview requires Microsoft Edge WebView2 Runtime. " + exception.Message);
        }
    }

    private void HandleMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (args.Source != PreviewUrl || IsDisposed)
            return;
        using JsonDocument document = JsonDocument.Parse(args.WebMessageAsJson);
        JsonElement message = document.RootElement;
        if (!message.TryGetProperty("type", out JsonElement type))
            return;
        if (type.GetString() == "ready")
        {
            _ready = true;
            _failed = false;
            _lastPayload = null;
            UpdateVisibility();
            SendSnapshot();
        }
        else if (type.GetString() == "error")
        {
            ShowFailure("The response preview could not render. Raw text remains available.");
        }
        else if (type.GetString() == "link"
            && message.TryGetProperty("url", out JsonElement url)
            && Uri.TryCreate(url.GetString(), UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "http" or "https")
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                MessageBox.Show(this, exception.Message, "Could not open link", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private void SendSnapshot()
    {
        if (!_ready || IsDisposed)
            return;
        string payload = JsonSerializer.Serialize(new
        {
            runId = _record?.RunId, system = _record?.SystemInstructions ?? "",
            prompt = _record?.PromptText ?? "", response = _record?.ResponseText ?? "",
            failure = FailureText(_record), active = _record?.IsActive ?? false,
            dark = _dark, motion = SystemInformation.IsMenuAnimationEnabled,
        });
        if (payload == _lastPayload)
            return;
        try
        {
            // JSON messages are data, never interpolated into executable JavaScript or HTML.
            _browser.CoreWebView2.PostWebMessageAsJson(payload);
            _lastPayload = payload;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            ShowFailure(exception.Message);
        }
    }

    private void UpdateRaw()
    {
        string text = _record is null ? "" :
            (string.IsNullOrWhiteSpace(_record.SystemInstructions) ? "" : "SYSTEM\n" + _record.SystemInstructions + "\n\n")
            + "PROMPT\n" + _record.PromptText + "\n\nRESPONSE\n" + _record.ResponseText
            + (FailureText(_record) is { Length: > 0 } failure ? "\n\nFAILURE\n" + failure : "");
        if (_raw.Text == text)
            return;
        bool sameRun = _rawRunId == _record?.RunId;
        if (!sameRun)
            _rawMotion.Reset(_record?.IsActive ?? false);
        RichTextUpdateState state = _rawMotion.BeginContentUpdate();
        try
        {
            _raw.Text = text;
            _rawRunId = _record?.RunId;
            _rawMotion.EndContentUpdate(sameRun ? state : new RichTextUpdateState(Point.Empty, 0, 0, _record?.IsActive ?? false));
        }
        catch
        {
            _rawMotion.AbortContentUpdate(state);
            throw;
        }
    }

    private void ShowFailure(string reason)
    {
        if (IsDisposed)
            return;
        _ready = false;
        _failed = true;
        _notice.Text = "Showing raw Markdown. " + reason;
        UpdateRaw();
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        _browser.Visible = _ready && !_showRaw;
        _raw.Visible = !_browser.Visible;
        _notice.Visible = !_ready;
        if (!_failed)
            _notice.Text = "Loading math preview… Raw text is available below.";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _rawMotion.Dispose();
        base.Dispose(disposing);
    }

    private static string FailureText(BrokerHistoryRecord? record)
        => string.IsNullOrWhiteSpace(record?.FailureSummary) ? ""
            : string.IsNullOrWhiteSpace(record.FailureDetail) ? record.FailureSummary
            : record.FailureSummary + "\n\n" + record.FailureDetail;
}
