using System.Diagnostics;
using ImGuiNET;
using XREngine.AgentRuntimes;

namespace XREngine.Editor.UI.Tools;

/// <summary>
/// Shares Codex account and model discovery between the assistant and shader editor.
/// </summary>
internal static class CodexEditorConnection
{
    private static readonly object Sync = new();
    private static IReadOnlyList<CodexModelInfo> _models = [];
    private static string _status = "Checking the Codex connection...";
    private static string _runtimeVersion = string.Empty;
    private static string _observedExecutable = string.Empty;
    private static string? _accountFingerprint;
    private static CancellationTokenSource? _signInCancellation;
    private static bool _checking;
    private static bool _signingIn;
    private static bool _autoProbeRequested;
    private static int _connectionRevision;

    public static int ConnectionRevision => Volatile.Read(ref _connectionRevision);

    public static string Model => Engine.EditorPreferences?.CodexModel ?? string.Empty;

    public static IReadOnlyList<CodexModelInfo> Models
    {
        get
        {
            lock (Sync)
                return _models;
        }
    }

    public static bool IsChecking
    {
        get
        {
            lock (Sync)
                return _checking;
        }
    }

    public static bool IsSigningIn
    {
        get
        {
            lock (Sync)
                return _signingIn;
        }
    }

    public static bool IsConnected
    {
        get
        {
            lock (Sync)
                return !_checking && !_signingIn && _accountFingerprint is not null;
        }
    }

    public static bool IsModelReady
    {
        get
        {
            lock (Sync)
                return !_checking && !_signingIn
                    && ContainsSupportedModel(_models, Engine.EditorPreferences?.CodexModel ?? string.Empty);
        }
    }

    public static string Status
    {
        get
        {
            lock (Sync)
                return _status;
        }
    }

    public static string RuntimeVersion
    {
        get
        {
            lock (Sync)
                return _runtimeVersion;
        }
    }

    public static CodexRunExecutor CreateExecutor(bool requireCurrentAccount = true)
    {
        string? fingerprint;
        lock (Sync)
            fingerprint = requireCurrentAccount ? _accountFingerprint : null;

        return new CodexRunExecutor(new CodexRuntimeOptions
        {
            ExecutablePath = Engine.EditorPreferences?.CodexExecutablePath ?? "codex",
            ExpectedAccountFingerprint = fingerprint,
        });
    }

    /// <summary>
    /// Starts one account check when the selected executable or account context changes.
    /// A failed check remains visible until the user retries or changes the connection.
    /// </summary>
    public static void EnsureConnectionChecked()
    {
        string executable = Engine.EditorPreferences?.CodexExecutablePath ?? "codex";
        bool startProbe;
        lock (Sync)
        {
            if (!string.Equals(_observedExecutable, executable, StringComparison.Ordinal))
            {
                _observedExecutable = executable;
                _models = [];
                _runtimeVersion = string.Empty;
                _accountFingerprint = null;
                _status = "Checking the Codex connection...";
                _autoProbeRequested = false;
                Interlocked.Increment(ref _connectionRevision);
            }

            startProbe = !_autoProbeRequested && !_checking && !_signingIn;
            if (startProbe)
                _autoProbeRequested = true;
        }

        if (startProbe)
            _ = ProbeAsync();
    }

    /// <summary>Draws exact account models. Shader requests retain their fixed medium effort requirement.</summary>
    public static void DrawModelPicker(string label, bool shaderModelOverride = false)
    {
        EnsureConnectionChecked();
        EditorPreferences? preferences = Engine.EditorPreferences;
        if (preferences is null)
            return;

        IReadOnlyList<CodexModelInfo> models = Models;
        string selected = shaderModelOverride && !string.IsNullOrWhiteSpace(preferences.ShaderEditorCodexModel)
            ? preferences.ShaderEditorCodexModel
            : preferences.CodexModel;
        bool selectedAvailable = ContainsSupportedModel(models, selected, shaderModelOverride);
        string preview = models.Count == 0 ? "Connect to see models"
            : string.IsNullOrWhiteSpace(selected) ? "Choose a model"
            : selectedAvailable ? selected : "Saved model unavailable";

        if (models.Count == 0)
            ImGui.BeginDisabled();
        if (ImGui.BeginCombo(label, preview))
        {
            for (int i = 0; i < models.Count; i++)
            {
                CodexModelInfo model = models[i];
                bool supported = !shaderModelOverride || SupportsMediumEffort(model);
                if (!supported)
                    ImGui.BeginDisabled();
                bool isSelected = string.Equals(model.Id, selected, StringComparison.Ordinal);
                if (ImGui.Selectable(model.Id, isSelected))
                    SelectModel(model.Id, shaderModelOverride);
                if (isSelected)
                    ImGui.SetItemDefaultFocus();
                if (!supported)
                {
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip("This model does not support the assistant's current response setting.");
                }
            }

            ImGui.EndCombo();
        }
        if (models.Count == 0)
            ImGui.EndDisabled();

        if (models.Count > 0 && !string.IsNullOrWhiteSpace(selected) && !selectedAvailable)
            ImGui.TextWrapped("The saved model is unavailable. Choose a model from this account.");
    }

    /// <summary>Draws the executable picker without repeating account actions.</summary>
    public static void DrawExecutableSettings()
    {
        ImGui.TextWrapped("Codex runs through the installed app on this computer.");
        if (ImGui.Button("Choose Codex app..."))
            BrowseExecutable();
        ImGui.SameLine();
        ImGui.TextDisabled(Engine.EditorPreferences?.CodexExecutablePath ?? "codex");
    }

    /// <summary>Draws account actions in the Manage connection popup.</summary>
    public static void DrawConnectionActions(bool includeExecutable)
    {
        if (includeExecutable)
            DrawExecutableSettings();
        bool disableActions = IsChecking || IsSigningIn;
        if (disableActions)
            ImGui.BeginDisabled();
        if (ImGui.Button("Check connection"))
            _ = ProbeAsync();
        ImGui.SameLine();
        if (ImGui.Button(IsConnected ? "Switch account" : "Sign in to ChatGPT"))
            _ = SignInAsync();
        if (disableActions)
            ImGui.EndDisabled();

        if (IsSigningIn)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel sign-in"))
                CancelSignIn();
        }

        ImGui.TextWrapped(Status);
        ImGui.TextWrapped("XRENGINE keeps this sign-in separate from the normal Codex app.");
    }

    public static void DrawSettings(bool shaderModelOverride = false)
    {
        ImGui.SetNextItemWidth(MathF.Min(360f, ImGui.GetContentRegionAvail().X));
        DrawModelPicker(shaderModelOverride ? "Shader model##CodexShader" : "Model##Codex", shaderModelOverride);
        if (ImGui.CollapsingHeader(shaderModelOverride ? "Connection##CodexShader" : "Connection##Codex"))
        {
            DrawExecutableSettings();
            DrawConnectionActions(includeExecutable: false);
        }
        else
            ImGui.TextWrapped(Status);
    }

    public static string ResolveShaderModel()
    {
        string? overrideModel = Engine.EditorPreferences?.ShaderEditorCodexModel;
        return string.IsNullOrWhiteSpace(overrideModel) ? Model : overrideModel;
    }

    /// <summary>Sets a model only when the current account lists it for medium effort.</summary>
    public static bool SelectModel(string modelId, bool shaderModelOverride = false)
    {
        lock (Sync)
        {
            CodexModelInfo? model = _models.FirstOrDefault(candidate => candidate.Id == modelId);
            if (model is null || shaderModelOverride && !SupportsMediumEffort(model))
                return false;
            EditorPreferences? preferences = Engine.EditorPreferences;
            if (preferences is null)
                return false;
            if (shaderModelOverride)
            {
                if (string.Equals(preferences.ShaderEditorCodexModel, modelId, StringComparison.Ordinal))
                    return true;
                preferences.ShaderEditorCodexModel = modelId;
            }
            else
            {
                if (string.Equals(preferences.CodexModel, modelId, StringComparison.Ordinal))
                    return true;
                preferences.CodexModel = modelId;
            }

            Interlocked.Increment(ref _connectionRevision);
            return true;
        }
    }

    public static void RecordAccountFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
            return;

        lock (Sync)
        {
            if (_accountFingerprint is not null
                && !string.Equals(_accountFingerprint, fingerprint, StringComparison.Ordinal))
            {
                _models = [];
                _runtimeVersion = string.Empty;
                _status = "The Codex account changed. Check the connection to see its models.";
                _autoProbeRequested = false;
                Interlocked.Increment(ref _connectionRevision);
            }

            _accountFingerprint = fingerprint;
        }
    }

    public static void ResetAccountContext()
    {
        lock (Sync)
        {
            _accountFingerprint = null;
            _models = [];
            _runtimeVersion = string.Empty;
            _autoProbeRequested = false;
            _status = "Codex account authentication changed. Check the connection before another request.";
            Interlocked.Increment(ref _connectionRevision);
        }
    }

    public static async Task ProbeAsync()
    {
        int revision;
        string executable;
        lock (Sync)
        {
            if (_checking || _signingIn)
                return;
            _checking = true;
            _autoProbeRequested = true;
            _status = "Checking Codex...";
            revision = _connectionRevision;
            executable = Engine.EditorPreferences?.CodexExecutablePath ?? "codex";
        }

        try
        {
            CodexConnectionInfo info = await new CodexRunExecutor(new CodexRuntimeOptions
            {
                ExecutablePath = executable,
            }).ProbeAsync();
            PublishConnection(info, executable, revision);
        }
        catch (Exception ex)
        {
            lock (Sync)
            {
                if (MatchesSelection(executable, revision))
                {
                    _models = [];
                    _runtimeVersion = string.Empty;
                    _status = $"Codex connection failed: {ex.Message}";
                }
            }
        }
        finally
        {
            lock (Sync)
                _checking = false;
        }
    }

    public static async Task SignInAsync()
    {
        var cancellation = new CancellationTokenSource();
        int revision;
        string executable;
        lock (Sync)
        {
            if (_checking || _signingIn)
            {
                cancellation.Dispose();
                return;
            }

            _signInCancellation = cancellation;
            _signingIn = true;
            _autoProbeRequested = true;
            _models = [];
            _accountFingerprint = null;
            _status = "Waiting for ChatGPT sign-in...";
            revision = Interlocked.Increment(ref _connectionRevision);
            executable = Engine.EditorPreferences?.CodexExecutablePath ?? "codex";
        }

        try
        {
            CodexConnectionInfo info = await new CodexRunExecutor(new CodexRuntimeOptions
            {
                ExecutablePath = executable,
            }).SignInAsync(uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }), cancellation.Token);
            PublishConnection(info, executable, revision);
        }
        catch (OperationCanceledException)
        {
            lock (Sync)
            {
                if (MatchesSelection(executable, revision))
                    _status = "Sign-in canceled.";
            }
        }
        catch (Exception ex)
        {
            lock (Sync)
            {
                if (MatchesSelection(executable, revision))
                    _status = $"Sign-in failed: {ex.Message}";
            }
        }
        finally
        {
            lock (Sync)
            {
                if (ReferenceEquals(_signInCancellation, cancellation))
                    _signInCancellation = null;
                _signingIn = false;
            }

            cancellation.Dispose();
        }
    }

    public static void CancelSignIn()
    {
        CancellationTokenSource? cancellation;
        lock (Sync)
            cancellation = _signInCancellation;
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Sign-in finished after the cancellation source was read.
        }
    }

    public static void BrowseExecutable()
    {
        string executable = Engine.EditorPreferences?.CodexExecutablePath ?? "codex";
        string? directory = Path.IsPathRooted(executable) && File.Exists(executable)
            ? Path.GetDirectoryName(executable)
            : null;
        ImGuiFileBrowser.OpenFile("CodexEditorExecutable", "Choose Codex app", result =>
        {
            if (!result.Success || string.IsNullOrWhiteSpace(result.SelectedPath))
                return;
            string path = result.SelectedPath;
            if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                lock (Sync)
                    _status = "Choose a Codex .exe file.";
                return;
            }

            EditorPreferences? preferences = Engine.EditorPreferences;
            if (preferences is null)
                return;
            preferences.CodexExecutablePath = path;
            EnsureConnectionChecked();
        }, "Applications|*.exe", directory);
    }

    private static void PublishConnection(CodexConnectionInfo info, string executable, int revision)
    {
        lock (Sync)
        {
            if (!MatchesSelection(executable, revision))
                return;

            if (_accountFingerprint is not null
                && !string.Equals(_accountFingerprint, info.AccountFingerprint, StringComparison.Ordinal))
                Interlocked.Increment(ref _connectionRevision);

            _accountFingerprint = info.AccountFingerprint;
            _models = info.Models.ToArray();
            _runtimeVersion = info.Version;
            _status = $"Connected to ChatGPT. {_models.Count} models available.";

            EditorPreferences? preferences = Engine.EditorPreferences;
            if (preferences is null || !string.IsNullOrWhiteSpace(preferences.CodexModel))
                return;
            CodexModelInfo? first = _models
                .OrderBy(static candidate => candidate.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (first is not null)
            {
                preferences.CodexModel = first.Id;
                Interlocked.Increment(ref _connectionRevision);
            }
        }
    }

    private static bool MatchesSelection(string executable, int revision)
        => revision == _connectionRevision
            && string.Equals(executable, Engine.EditorPreferences?.CodexExecutablePath ?? "codex", StringComparison.Ordinal);

    private static bool ContainsSupportedModel(IReadOnlyList<CodexModelInfo> models, string selected, bool requireMediumEffort = false)
    {
        for (int i = 0; i < models.Count; i++)
        {
            CodexModelInfo model = models[i];
            if (string.Equals(model.Id, selected, StringComparison.Ordinal)
                && (!requireMediumEffort || SupportsMediumEffort(model)))
                return true;
        }

        return false;
    }

    internal static bool SupportsMediumEffort(CodexModelInfo model)
    {
        for (int i = 0; i < model.SupportedReasoningEfforts.Count; i++)
            if (model.SupportedReasoningEfforts[i] == "medium")
                return true;
        return false;
    }
}
