using System.ComponentModel;

namespace XREngine;

public partial class EditorPreferences
{
    private bool _mcpAssistantUseGeminiCli;
    private bool _mcpAssistantUseClaudeCli;
    private string _claudeExecutablePath = "claude";
    private string _claudeCliModel = string.Empty;
    private string _shaderEditorClaudeModel = string.Empty;
    private string _geminiExecutablePath = "gemini";
    private string _antigravityExecutablePath = "agy";
    private string _geminiCliModel = string.Empty;
    private string _copilotExecutablePath = "copilot";
    private string _copilotCliModel = string.Empty;
    private int _shaderEditorSubscriptionBrand;
    private bool? _shaderEditorUseSubscription;
    private string _shaderEditorGeminiModel = string.Empty;
    private string _shaderEditorCopilotModel = string.Empty;

    /// <summary>Selects the installed Claude Code client instead of API-key access.</summary>
    [Category("Assistant")]
    public bool McpAssistantUseClaudeCli
    {
        get => _mcpAssistantUseClaudeCli;
        set => SetField(ref _mcpAssistantUseClaudeCli, value);
    }

    /// <summary>Names the unmodified Claude Code executable used for local sessions.</summary>
    [Category("Assistant")]
    public string ClaudeExecutablePath
    {
        get => _claudeExecutablePath;
        set => SetField(ref _claudeExecutablePath, string.IsNullOrWhiteSpace(value) ? "claude" : value.Trim());
    }

    /// <summary>Selects an exact model reported by the Claude Code client.</summary>
    [Category("Assistant")]
    public string ClaudeCliModel
    {
        get => _claudeCliModel;
        set => SetField(ref _claudeCliModel, value?.Trim() ?? string.Empty);
    }

    /// <summary>Overrides the Claude Code model for shader completion.</summary>
    [Category("Shader Editor")]
    public string ShaderEditorClaudeModel
    {
        get => _shaderEditorClaudeModel;
        set => SetField(ref _shaderEditorClaudeModel, value?.Trim() ?? string.Empty);
    }

    /// <summary>Selects Gemini through Antigravity instead of API-key access.</summary>
    [Category("Assistant")]
    public bool McpAssistantUseGeminiCli
    {
        get => _mcpAssistantUseGeminiCli;
        set => SetField(ref _mcpAssistantUseGeminiCli, value);
    }

    /// <summary>Names the installed Gemini executable used for local client sessions.</summary>
    [Category("Assistant")]
    public string GeminiExecutablePath
    {
        get => _geminiExecutablePath;
        set => SetField(ref _geminiExecutablePath, string.IsNullOrWhiteSpace(value) ? "gemini" : value.Trim());
    }

    /// <summary>Names the Antigravity client used with the signed-in Google plan.</summary>
    [Category("Assistant")]
    public string AntigravityExecutablePath
    {
        get => _antigravityExecutablePath;
        set => SetField(ref _antigravityExecutablePath, string.IsNullOrWhiteSpace(value) ? "agy" : value.Trim());
    }

    /// <summary>Selects an exact model from the signed-in Gemini account.</summary>
    [Category("Assistant")]
    public string GeminiCliModel
    {
        get => _geminiCliModel;
        set => SetField(ref _geminiCliModel, value?.Trim() ?? string.Empty);
    }

    /// <summary>Names the installed Copilot executable used for local client sessions.</summary>
    [Category("Assistant")]
    public string CopilotExecutablePath
    {
        get => _copilotExecutablePath;
        set => SetField(ref _copilotExecutablePath, string.IsNullOrWhiteSpace(value) ? "copilot" : value.Trim());
    }

    /// <summary>Selects an exact model from the signed-in Copilot account.</summary>
    [Category("Assistant")]
    public string CopilotCliModel
    {
        get => _copilotCliModel;
        set => SetField(ref _copilotCliModel, value?.Trim() ?? string.Empty);
    }

    /// <summary>Selects the shader completion client: Codex 0, Claude 1, Gemini 2, or Copilot 4.</summary>
    [Category("Shader Editor")]
    public int ShaderEditorSubscriptionBrand
    {
        get => _shaderEditorSubscriptionBrand;
        set => SetField(ref _shaderEditorSubscriptionBrand, value is 1 or 2 or 4 ? value : 0);
    }

    /// <summary>Selects subscription completion, preserving the prior shared setting until changed.</summary>
    [Category("Shader Editor")]
    public bool ShaderEditorUseSubscription
    {
        get => _shaderEditorUseSubscription ?? McpAssistantUseCodexAppServer;
        set => SetField(ref _shaderEditorUseSubscription, (bool?)value);
    }

    /// <summary>Overrides the signed-in Gemini model for shader completion.</summary>
    [Category("Shader Editor")]
    public string ShaderEditorGeminiModel
    {
        get => _shaderEditorGeminiModel;
        set => SetField(ref _shaderEditorGeminiModel, value?.Trim() ?? string.Empty);
    }

    /// <summary>Overrides the signed-in Copilot model for shader completion.</summary>
    [Category("Shader Editor")]
    public string ShaderEditorCopilotModel
    {
        get => _shaderEditorCopilotModel;
        set => SetField(ref _shaderEditorCopilotModel, value?.Trim() ?? string.Empty);
    }
}
