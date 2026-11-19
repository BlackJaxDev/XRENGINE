using System.ComponentModel;
using System.Diagnostics;
using ImGuiNET;
using XREngine.AgentOrchestration;
using XREngine.AgentRuntimes;
using XREngine.Editor.UI;

namespace XREngine.Editor.UI.Tools;

/// <summary>
/// Keeps each installed assistant account separate. The Assistant and shader
/// editor read the same model snapshot for the selected client.
/// </summary>
internal static class SubscriptionEditorConnection
{
    private sealed class ConnectionState
    {
        public readonly object Sync = new();
        public IReadOnlyList<SubscriptionModelInfo> Models = [];
        public string Status = "Check the connection to load models.";
        public string RuntimeVersion = string.Empty;
        public string ObservedExecutable = string.Empty;
        public string? AccountFingerprint;
        public bool Connected;
        public bool SupportsConversationReplay = true;
        public CancellationTokenSource? SignInCancellation;
        public bool Checking;
        public bool SigningIn;
        public bool AutoProbeRequested;
        public int Revision;
    }

    private static readonly ConnectionState Gemini = new();
    private static readonly ConnectionState Claude = new();
    private static readonly ConnectionState Copilot = new();

    public static string Model(SubscriptionAssistantBrand brand, bool shaderModelOverride = false)
    {
        EditorPreferences? preferences = Engine.EditorPreferences;
        if (preferences is null)
            return string.Empty;
        if (brand == SubscriptionAssistantBrand.Codex)
            return shaderModelOverride && !string.IsNullOrWhiteSpace(preferences.ShaderEditorCodexModel)
                ? preferences.ShaderEditorCodexModel : preferences.CodexModel;
        if (brand == SubscriptionAssistantBrand.Claude)
            return shaderModelOverride && !string.IsNullOrWhiteSpace(preferences.ShaderEditorClaudeModel)
                ? preferences.ShaderEditorClaudeModel : preferences.ClaudeCliModel;
        if (brand == SubscriptionAssistantBrand.Gemini)
            return shaderModelOverride && !string.IsNullOrWhiteSpace(preferences.ShaderEditorGeminiModel)
                ? preferences.ShaderEditorGeminiModel : preferences.GeminiCliModel;
        return shaderModelOverride && !string.IsNullOrWhiteSpace(preferences.ShaderEditorCopilotModel)
            ? preferences.ShaderEditorCopilotModel : preferences.CopilotCliModel;
    }

    public static bool IsConnected(SubscriptionAssistantBrand brand)
    {
        if (brand == SubscriptionAssistantBrand.Codex)
            return CodexEditorConnection.IsConnected;
        ConnectionState state = GetState(brand);
        lock (state.Sync)
            return !state.Checking && !state.SigningIn
                && state.Connected;
    }

    public static bool IsModelReady(SubscriptionAssistantBrand brand, bool shaderModelOverride = false)
    {
        if (brand == SubscriptionAssistantBrand.Codex && !shaderModelOverride)
            return CodexEditorConnection.IsModelReady;
        string selected = Model(brand, shaderModelOverride);
        if (brand == SubscriptionAssistantBrand.Codex)
        {
            if (!CodexEditorConnection.IsConnected)
                return false;
            IReadOnlyList<CodexModelInfo> models = CodexEditorConnection.Models;
            for (int i = 0; i < models.Count; i++)
            {
                CodexModelInfo model = models[i];
                if (model.Id == selected && CodexEditorConnection.SupportsMediumEffort(model))
                    return true;
            }
            return false;
        }

        ConnectionState state = GetState(brand);
        lock (state.Sync)
        {
            if (state.Checking || state.SigningIn || !state.Connected)
                return false;
            for (int i = 0; i < state.Models.Count; i++)
                if (state.Models[i].Id == selected)
                    return true;
            return false;
        }
    }

    public static bool IsChecking(SubscriptionAssistantBrand brand)
    {
        if (brand == SubscriptionAssistantBrand.Codex)
            return CodexEditorConnection.IsChecking;
        ConnectionState state = GetState(brand);
        lock (state.Sync)
            return state.Checking;
    }

    /// <summary>Returns the selected model's reported effort choices without allocating a new menu each frame.</summary>
    public static IReadOnlyList<string> SupportedReasoningEfforts(SubscriptionAssistantBrand brand)
    {
        string selected = Model(brand);
        if (brand == SubscriptionAssistantBrand.Codex)
        {
            IReadOnlyList<CodexModelInfo> models = CodexEditorConnection.Models;
            for (int i = 0; i < models.Count; i++)
                if (models[i].Id == selected)
                    return models[i].SupportedReasoningEfforts;
            return Array.Empty<string>();
        }
        ConnectionState state = GetState(brand);
        lock (state.Sync)
        {
            for (int i = 0; i < state.Models.Count; i++)
                if (state.Models[i].Id == selected)
                    return state.Models[i].SupportedReasoningEfforts;
            return Array.Empty<string>();
        }
    }

    public static bool SupportsConversationReplay(SubscriptionAssistantBrand brand)
    {
        if (brand == SubscriptionAssistantBrand.Codex)
            return true;
        ConnectionState state = GetState(brand);
        lock (state.Sync)
            return state.SupportsConversationReplay;
    }

    public static bool IsSigningIn(SubscriptionAssistantBrand brand)
    {
        if (brand == SubscriptionAssistantBrand.Codex)
            return CodexEditorConnection.IsSigningIn;
        ConnectionState state = GetState(brand);
        lock (state.Sync)
            return state.SigningIn;
    }

    public static string Status(SubscriptionAssistantBrand brand)
    {
        if (brand == SubscriptionAssistantBrand.Codex)
            return CodexEditorConnection.Status;
        ConnectionState state = GetState(brand);
        lock (state.Sync)
            return state.Status;
    }

    public static string RuntimeVersion(SubscriptionAssistantBrand brand)
    {
        if (brand == SubscriptionAssistantBrand.Codex)
            return CodexEditorConnection.RuntimeVersion;
        ConnectionState state = GetState(brand);
        lock (state.Sync)
            return state.RuntimeVersion;
    }

    public static int ConnectionRevision(SubscriptionAssistantBrand brand)
    {
        if (brand == SubscriptionAssistantBrand.Codex)
            return CodexEditorConnection.ConnectionRevision;
        ConnectionState state = GetState(brand);
        lock (state.Sync)
            return state.Revision;
    }

    public static IAgentRunExecutor CreateExecutor(SubscriptionAssistantBrand brand)
    {
        if (brand == SubscriptionAssistantBrand.Codex)
            return CodexEditorConnection.CreateExecutor();
        ConnectionState state = GetState(brand);
        string? fingerprint;
        lock (state.Sync)
            fingerprint = state.AccountFingerprint;
        return CreateNativeRuntime(brand, GetExecutable(brand), fingerprint);
    }

    public static AgentExecutionMode ExecutionMode(SubscriptionAssistantBrand brand) => brand switch
    {
        SubscriptionAssistantBrand.Codex => AgentExecutionMode.CodexAppServer,
        SubscriptionAssistantBrand.Gemini => AgentExecutionMode.AntigravityCli,
        SubscriptionAssistantBrand.Claude => AgentExecutionMode.ClaudeCli,
        SubscriptionAssistantBrand.Copilot => AgentExecutionMode.CopilotCli,
        _ => throw new ArgumentOutOfRangeException(nameof(brand)),
    };

    /// <summary>Starts one probe for a selected executable and keeps other brands unchanged.</summary>
    public static void EnsureConnectionChecked(SubscriptionAssistantBrand brand)
    {
        if (brand == SubscriptionAssistantBrand.Codex)
        {
            CodexEditorConnection.EnsureConnectionChecked();
            return;
        }
        ConnectionState state = GetState(brand);
        string executable = GetExecutable(brand);
        bool startProbe;
        lock (state.Sync)
        {
            if (!string.Equals(state.ObservedExecutable, executable, StringComparison.Ordinal))
            {
                state.ObservedExecutable = executable;
                ClearAccount(state);
                state.Status = "Checking the connection...";
                state.AutoProbeRequested = false;
                state.Revision++;
            }
            startProbe = !state.AutoProbeRequested && !state.Checking && !state.SigningIn;
            if (startProbe)
                state.AutoProbeRequested = true;
        }
        if (startProbe)
            _ = ProbeAsync(brand);
    }

    public static async Task ProbeAsync(SubscriptionAssistantBrand brand)
    {
        if (brand == SubscriptionAssistantBrand.Codex)
        {
            await CodexEditorConnection.ProbeAsync();
            return;
        }
        ConnectionState state = GetState(brand);
        string executable = GetExecutable(brand);
        int revision;
        lock (state.Sync)
        {
            if (state.Checking || state.SigningIn)
                return;
            state.Checking = true;
            state.AutoProbeRequested = true;
            state.Status = "Checking the connection...";
            revision = state.Revision;
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            SubscriptionConnectionInfo info = await CreateNativeRuntime(brand, executable).ProbeAsync(timeout.Token);
            PublishConnection(brand, state, info, executable, revision);
        }
        catch (Exception exception)
        {
            lock (state.Sync)
            {
                if (MatchesSelection(brand, state, executable, revision))
                {
                    ClearAccount(state);
                    state.Revision++;
                    state.Status = SafeConnectionError(brand, exception);
                }
            }
        }
        finally
        {
            lock (state.Sync)
                state.Checking = false;
        }
    }

    public static async Task SignInAsync(SubscriptionAssistantBrand brand)
    {
        if (brand == SubscriptionAssistantBrand.Codex)
        {
            await CodexEditorConnection.SignInAsync();
            return;
        }
        ConnectionState state = GetState(brand);
        var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        string executable = GetExecutable(brand);
        int revision;
        lock (state.Sync)
        {
            if (state.Checking || state.SigningIn)
            {
                cancellation.Dispose();
                return;
            }
            state.SignInCancellation = cancellation;
            state.SigningIn = true;
            state.AutoProbeRequested = true;
            ClearAccount(state);
            state.Status = "Waiting for sign-in...";
            revision = ++state.Revision;
        }
        try
        {
            SubscriptionConnectionInfo info = await CreateNativeRuntime(brand, executable)
                .SignInAsync(uri => OpenAuthorizationPage(brand, uri), cancellation.Token);
            PublishConnection(brand, state, info, executable, revision);
        }
        catch (OperationCanceledException)
        {
            lock (state.Sync)
                if (MatchesSelection(brand, state, executable, revision))
                    state.Status = "Sign-in canceled or timed out.";
        }
        catch (Exception exception)
        {
            lock (state.Sync)
                if (MatchesSelection(brand, state, executable, revision))
                    state.Status = SafeConnectionError(brand, exception);
        }
        finally
        {
            lock (state.Sync)
            {
                if (ReferenceEquals(state.SignInCancellation, cancellation))
                    state.SignInCancellation = null;
                state.SigningIn = false;
            }
            cancellation.Dispose();
        }
    }

    public static void CancelSignIn(SubscriptionAssistantBrand brand)
    {
        if (brand == SubscriptionAssistantBrand.Codex)
        {
            CodexEditorConnection.CancelSignIn();
            return;
        }
        ConnectionState state = GetState(brand);
        CancellationTokenSource? cancellation;
        lock (state.Sync)
            cancellation = state.SignInCancellation;
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The sign-in operation can finish after this reference is read.
        }
    }

    public static void RecordAccountFingerprint(SubscriptionAssistantBrand brand, string? fingerprint)
    {
        if (brand == SubscriptionAssistantBrand.Codex)
        {
            CodexEditorConnection.RecordAccountFingerprint(fingerprint);
            return;
        }
        if (string.IsNullOrWhiteSpace(fingerprint))
            return;
        ConnectionState state = GetState(brand);
        lock (state.Sync)
        {
            if (state.AccountFingerprint is not null && state.AccountFingerprint != fingerprint)
            {
                ClearAccount(state);
                state.Status = "The account changed. Check the connection before another request.";
                state.AutoProbeRequested = false;
                state.Revision++;
            }
        }
    }

    public static void ResetAccountContext(SubscriptionAssistantBrand brand)
    {
        if (brand == SubscriptionAssistantBrand.Codex)
        {
            CodexEditorConnection.ResetAccountContext();
            return;
        }
        ConnectionState state = GetState(brand);
        lock (state.Sync)
        {
            ClearAccount(state);
            state.AutoProbeRequested = false;
            state.Status = "Authentication changed. Check the connection before another request.";
            state.Revision++;
        }
    }

    public static void DrawModelPicker(SubscriptionAssistantBrand brand, string label, bool shaderModelOverride = false)
    {
        if (brand == SubscriptionAssistantBrand.Codex)
        {
            CodexEditorConnection.DrawModelPicker(label, shaderModelOverride);
            return;
        }
        EnsureConnectionChecked(brand);
        ConnectionState state = GetState(brand);
        IReadOnlyList<SubscriptionModelInfo> models;
        lock (state.Sync)
            models = state.Models;
        string selected = Model(brand, shaderModelOverride);
        bool available = IsModelReady(brand, shaderModelOverride);
        string preview = models.Count == 0 ? "Connect to see models"
            : string.IsNullOrWhiteSpace(selected) ? "Choose a model"
            : available ? selected : "Saved model unavailable";
        if (models.Count == 0)
            ImGui.BeginDisabled();
        if (ImGui.BeginCombo(label, preview))
        {
            for (int i = 0; i < models.Count; i++)
            {
                SubscriptionModelInfo model = models[i];
                ImGui.PushID(model.Id);
                if (ImGui.Selectable(model.DisplayName, model.Id == selected))
                    SelectModel(brand, model.Id, shaderModelOverride);
                ImGui.PopID();
            }
            ImGui.EndCombo();
        }
        if (models.Count == 0)
            ImGui.EndDisabled();
        if (models.Count > 0 && !string.IsNullOrWhiteSpace(selected) && !available)
            ImGui.TextWrapped("The saved model is unavailable. Choose a model from this account.");
    }

    /// <summary>Draws account actions only where the user manages a connection.</summary>
    public static void DrawConnectionActions(SubscriptionAssistantBrand brand)
    {
        if (brand == SubscriptionAssistantBrand.Codex)
        {
            CodexEditorConnection.DrawConnectionActions(includeExecutable: false);
            return;
        }
        bool disabled = IsChecking(brand) || IsSigningIn(brand);
        if (disabled)
            ImGui.BeginDisabled();
        if (ImGui.Button("Check connection"))
            _ = ProbeAsync(brand);
        ImGui.SameLine();
        string accountAction = IsConnected(brand)
            ? brand == SubscriptionAssistantBrand.Gemini ? "Reconnect" : "Switch account"
            : "Sign in";
        if (ImGui.Button(accountAction))
            _ = SignInAsync(brand);
        if (disabled)
            ImGui.EndDisabled();
        if (IsSigningIn(brand))
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel sign-in"))
                CancelSignIn(brand);
        }
        ImGui.TextWrapped(Status(brand));
        if (brand == SubscriptionAssistantBrand.Gemini)
            ImGui.TextWrapped("Uses Antigravity with your Google plan. Each request starts a new chat. The client confirms your model selection, but does not report the model that generated the reply.");
    }

    /// <summary>Draws the installed client choice in Advanced without account actions.</summary>
    public static void DrawExecutableSettings(SubscriptionAssistantBrand brand)
    {
        if (brand == SubscriptionAssistantBrand.Codex)
        {
            CodexEditorConnection.DrawExecutableSettings();
            return;
        }
        if (ImGui.Button(brand == SubscriptionAssistantBrand.Gemini ? "Choose Antigravity app..." : brand == SubscriptionAssistantBrand.Claude ? "Choose Claude Code app..." : "Choose Copilot app..."))
            BrowseExecutable(brand);
        ImGui.SameLine();
        ImGui.TextDisabled(GetExecutable(brand));
    }

    private static bool SelectModel(SubscriptionAssistantBrand brand, string modelId, bool shaderModelOverride)
    {
        ConnectionState state = GetState(brand);
        lock (state.Sync)
        {
            bool available = false;
            for (int i = 0; i < state.Models.Count; i++)
                if (state.Models[i].Id == modelId)
                    available = true;
            if (!available)
                return false;
            EditorPreferences? preferences = Engine.EditorPreferences;
            if (preferences is null)
                return false;
            if (brand == SubscriptionAssistantBrand.Claude)
            {
                if (shaderModelOverride)
                    preferences.ShaderEditorClaudeModel = modelId;
                else
                    preferences.ClaudeCliModel = modelId;
            }
            else if (brand == SubscriptionAssistantBrand.Gemini)
            {
                if (shaderModelOverride)
                    preferences.ShaderEditorGeminiModel = modelId;
                else
                    preferences.GeminiCliModel = modelId;
            }
            else if (shaderModelOverride)
                preferences.ShaderEditorCopilotModel = modelId;
            else
                preferences.CopilotCliModel = modelId;
            state.Revision++;
            return true;
        }
    }

    private static void BrowseExecutable(SubscriptionAssistantBrand brand)
    {
        string executable = GetExecutable(brand);
        string? directory = Path.IsPathRooted(executable) && File.Exists(executable)
            ? Path.GetDirectoryName(executable) : null;
        string title = brand == SubscriptionAssistantBrand.Gemini ? "Choose Antigravity app" : brand == SubscriptionAssistantBrand.Claude ? "Choose Claude Code app" : "Choose Copilot app";
        ImGuiFileBrowser.OpenFile($"{brand}Executable", title, result =>
        {
            if (!result.Success || string.IsNullOrWhiteSpace(result.SelectedPath))
                return;
            bool supportedPath = string.Equals(Path.GetExtension(result.SelectedPath), ".exe", StringComparison.OrdinalIgnoreCase);
            if (!supportedPath)
            {
                ConnectionState state = GetState(brand);
                lock (state.Sync)
                    state.Status = "Choose the installed client executable or its supported launcher.";
                return;
            }
            EditorPreferences? preferences = Engine.EditorPreferences;
            if (preferences is null)
                return;
            if (brand == SubscriptionAssistantBrand.Claude)
                preferences.ClaudeExecutablePath = result.SelectedPath;
            else if (brand == SubscriptionAssistantBrand.Gemini)
                preferences.AntigravityExecutablePath = result.SelectedPath;
            else
                preferences.CopilotExecutablePath = result.SelectedPath;
            EnsureConnectionChecked(brand);
        }, "Applications|*.exe", directory);
    }

    private static void PublishConnection(SubscriptionAssistantBrand brand, ConnectionState state,
        SubscriptionConnectionInfo info, string executable, int revision)
    {
        lock (state.Sync)
        {
            if (!MatchesSelection(brand, state, executable, revision))
                return;
            if (string.IsNullOrWhiteSpace(info.AccountFingerprint)
                && (!info.IsAuthenticated || info.SupportsConversationReplay))
            {
                ClearAccount(state);
                state.Revision++;
                state.Status = "The client did not confirm a signed-in account.";
                return;
            }
            if (state.AccountFingerprint is not null && state.AccountFingerprint != info.AccountFingerprint)
                state.Revision++;
            state.AccountFingerprint = info.AccountFingerprint;
            state.Connected = true;
            state.SupportsConversationReplay = info.SupportsConversationReplay;
            state.Models = info.Models.ToArray();
            state.RuntimeVersion = info.Version;
            state.Status = info.SupportsConversationReplay
                ? $"Connected. {state.Models.Count} models available."
                : $"Connected. {state.Models.Count} models available. Each request starts a new chat.";
            EditorPreferences? preferences = Engine.EditorPreferences;
            if (preferences is null || state.Models.Count == 0 || !string.IsNullOrWhiteSpace(Model(brand)))
                return;
            if (brand == SubscriptionAssistantBrand.Claude)
                preferences.ClaudeCliModel = state.Models[0].Id;
            else if (brand == SubscriptionAssistantBrand.Gemini)
                preferences.GeminiCliModel = state.Models[0].Id;
            else
                preferences.CopilotCliModel = state.Models[0].Id;
            state.Revision++;
        }
    }

    private static bool MatchesSelection(SubscriptionAssistantBrand brand, ConnectionState state,
        string executable, int revision)
        => state.Revision == revision && string.Equals(executable, GetExecutable(brand), StringComparison.Ordinal);

    private static void ClearAccount(ConnectionState state)
    {
        state.Models = [];
        state.AccountFingerprint = null;
        state.Connected = false;
        state.SupportsConversationReplay = true;
        state.RuntimeVersion = string.Empty;
    }

    private static ConnectionState GetState(SubscriptionAssistantBrand brand) => brand switch
    {
        SubscriptionAssistantBrand.Gemini => Gemini,
        SubscriptionAssistantBrand.Claude => Claude,
        SubscriptionAssistantBrand.Copilot => Copilot,
        _ => throw new ArgumentOutOfRangeException(nameof(brand)),
    };

    private static string GetExecutable(SubscriptionAssistantBrand brand) => brand switch
    {
        SubscriptionAssistantBrand.Codex => Engine.EditorPreferences?.CodexExecutablePath ?? "codex",
        SubscriptionAssistantBrand.Gemini => Engine.EditorPreferences?.AntigravityExecutablePath ?? "agy",
        SubscriptionAssistantBrand.Claude => Engine.EditorPreferences?.ClaudeExecutablePath ?? "claude",
        SubscriptionAssistantBrand.Copilot => Engine.EditorPreferences?.CopilotExecutablePath ?? "copilot",
        _ => throw new ArgumentOutOfRangeException(nameof(brand)),
    };

    private static ISubscriptionAgentRuntime CreateNativeRuntime(SubscriptionAssistantBrand brand,
        string executable, string? fingerprint = null)
    {
        var options = new LocalCliRuntimeOptions
        {
            ExecutablePath = executable,
            ExpectedAccountFingerprint = fingerprint,
        };
        return brand switch
        {
            SubscriptionAssistantBrand.Gemini => new AntigravityRunExecutor(options),
            SubscriptionAssistantBrand.Claude => new ClaudeRunExecutor(options),
            SubscriptionAssistantBrand.Copilot => new CopilotRunExecutor(options),
            _ => throw new ArgumentOutOfRangeException(nameof(brand)),
        };
    }

    private static void OpenAuthorizationPage(SubscriptionAssistantBrand brand, Uri uri)
    {
        bool allowedHost = brand == SubscriptionAssistantBrand.Gemini
            ? uri.Host is "accounts.google.com" or "google.com"
            : brand == SubscriptionAssistantBrand.Claude ? uri.Host is "claude.ai" or "platform.claude.com" or "console.anthropic.com"
            : uri.Host is "github.com" or "login.microsoftonline.com" or "microsoft.com";
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !allowedHost)
            throw new InvalidOperationException("The client returned an unsupported sign-in address.");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private static string SafeConnectionError(SubscriptionAssistantBrand brand, Exception exception)
    {
        if (exception is Win32Exception)
            return $"{brand} could not start. Install its app or choose its executable in Advanced.";
        if (exception is OperationCanceledException)
            return "The connection check timed out. Try again.";
        return $"{brand} connection failed. Check its app and sign-in, then try again.";
    }
}
