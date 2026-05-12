using System.ComponentModel;
using YamlDotNet.Serialization;

namespace XREngine
{
    using XREngine.Settings;

    /// <summary>
    /// Secret persistence partial for <see cref="EditorPreferences"/>. Each secret-bearing
    /// preference is owned in three pieces:
    /// <list type="bullet">
    /// <item>A public, plaintext UI-facing string property (declared in
    /// <c>EditorPreferences.cs</c>) that the editor surfaces and that consumers read at runtime.
    /// These are marked <see cref="YamlIgnoreAttribute"/> so the YAML asset never sees their
    /// plaintext value.</item>
    /// <item>A private, <see cref="YamlMemberAttribute"/>-aliased shadow property declared here.
    /// On load the shadow accepts <c>dpapi:</c>, <c>env:</c>, or legacy plaintext and routes the
    /// resolved plaintext into the public field. On save it emits <c>dpapi:</c> ciphertext (or
    /// <c>env:</c> when the matching env-var-name field is set).</item>
    /// <item>An <c>*EnvVarName</c> companion property the user can populate to opt out of
    /// persisting the secret at all and instead resolve it from a process environment variable
    /// at runtime.</item>
    /// </list>
    /// </summary>
    public partial class EditorPreferences
    {
        // -------------------------------------------------------------------------
        // McpServerAuthToken
        // -------------------------------------------------------------------------

        private string _mcpServerAuthTokenEnvVar = string.Empty;

        [Category("MCP Server")]
        [DisplayName("MCP Auth Token (env var)")]
        [Description("If set, the MCP auth token is read from this process environment variable at runtime and the token is NOT persisted to disk.")]
        [YamlMember(Alias = "McpServerAuthTokenEnvVar")]
        public string McpServerAuthTokenEnvVar
        {
            get => _mcpServerAuthTokenEnvVar;
            set => SetField(ref _mcpServerAuthTokenEnvVar, value ?? string.Empty);
        }

        [YamlMember(Alias = "McpServerAuthToken")]
        private string McpServerAuthTokenSerialized
        {
            get => string.IsNullOrEmpty(_mcpServerAuthTokenEnvVar)
                ? SecretCipher.Protect(_mcpServerAuthToken)
                : SecretCipher.ReferenceEnv(_mcpServerAuthTokenEnvVar);
            set => AcceptSecretSerialized(value, ref _mcpServerAuthToken, ref _mcpServerAuthTokenEnvVar, "McpServerAuthToken");
        }

        // -------------------------------------------------------------------------
        // McpAssistantOpenAiApiKey
        // -------------------------------------------------------------------------

        private string _mcpAssistantOpenAiApiKeyEnvVar = string.Empty;

        [Category("MCP Assistant")]
        [DisplayName("OpenAI API Key (env var)")]
        [Description("If set, the OpenAI API key is read from this process environment variable at runtime and the key is NOT persisted to disk.")]
        [YamlMember(Alias = "McpAssistantOpenAiApiKeyEnvVar")]
        public string McpAssistantOpenAiApiKeyEnvVar
        {
            get => _mcpAssistantOpenAiApiKeyEnvVar;
            set => SetField(ref _mcpAssistantOpenAiApiKeyEnvVar, value ?? string.Empty);
        }

        [YamlMember(Alias = "McpAssistantOpenAiApiKey")]
        private string McpAssistantOpenAiApiKeySerialized
        {
            get => string.IsNullOrEmpty(_mcpAssistantOpenAiApiKeyEnvVar)
                ? SecretCipher.Protect(_mcpAssistantOpenAiApiKey)
                : SecretCipher.ReferenceEnv(_mcpAssistantOpenAiApiKeyEnvVar);
            set => AcceptSecretSerialized(value, ref _mcpAssistantOpenAiApiKey, ref _mcpAssistantOpenAiApiKeyEnvVar, "McpAssistantOpenAiApiKey");
        }

        // -------------------------------------------------------------------------
        // McpAssistantAnthropicApiKey
        // -------------------------------------------------------------------------

        private string _mcpAssistantAnthropicApiKeyEnvVar = string.Empty;

        [Category("MCP Assistant")]
        [DisplayName("Anthropic API Key (env var)")]
        [Description("If set, the Anthropic API key is read from this process environment variable at runtime and the key is NOT persisted to disk.")]
        [YamlMember(Alias = "McpAssistantAnthropicApiKeyEnvVar")]
        public string McpAssistantAnthropicApiKeyEnvVar
        {
            get => _mcpAssistantAnthropicApiKeyEnvVar;
            set => SetField(ref _mcpAssistantAnthropicApiKeyEnvVar, value ?? string.Empty);
        }

        [YamlMember(Alias = "McpAssistantAnthropicApiKey")]
        private string McpAssistantAnthropicApiKeySerialized
        {
            get => string.IsNullOrEmpty(_mcpAssistantAnthropicApiKeyEnvVar)
                ? SecretCipher.Protect(_mcpAssistantAnthropicApiKey)
                : SecretCipher.ReferenceEnv(_mcpAssistantAnthropicApiKeyEnvVar);
            set => AcceptSecretSerialized(value, ref _mcpAssistantAnthropicApiKey, ref _mcpAssistantAnthropicApiKeyEnvVar, "McpAssistantAnthropicApiKey");
        }

        // -------------------------------------------------------------------------
        // McpAssistantGeminiApiKey
        // -------------------------------------------------------------------------

        private string _mcpAssistantGeminiApiKeyEnvVar = string.Empty;

        [Category("MCP Assistant")]
        [DisplayName("Gemini API Key (env var)")]
        [Description("If set, the Gemini API key is read from this process environment variable at runtime and the key is NOT persisted to disk.")]
        [YamlMember(Alias = "McpAssistantGeminiApiKeyEnvVar")]
        public string McpAssistantGeminiApiKeyEnvVar
        {
            get => _mcpAssistantGeminiApiKeyEnvVar;
            set => SetField(ref _mcpAssistantGeminiApiKeyEnvVar, value ?? string.Empty);
        }

        [YamlMember(Alias = "McpAssistantGeminiApiKey")]
        private string McpAssistantGeminiApiKeySerialized
        {
            get => string.IsNullOrEmpty(_mcpAssistantGeminiApiKeyEnvVar)
                ? SecretCipher.Protect(_mcpAssistantGeminiApiKey)
                : SecretCipher.ReferenceEnv(_mcpAssistantGeminiApiKeyEnvVar);
            set => AcceptSecretSerialized(value, ref _mcpAssistantGeminiApiKey, ref _mcpAssistantGeminiApiKeyEnvVar, "McpAssistantGeminiApiKey");
        }

        // -------------------------------------------------------------------------
        // McpAssistantGitHubModelsToken
        // -------------------------------------------------------------------------

        private string _mcpAssistantGitHubModelsTokenEnvVar = string.Empty;

        [Category("MCP Assistant")]
        [DisplayName("GitHub Models Token (env var)")]
        [Description("If set, the GitHub Models token is read from this process environment variable at runtime and the token is NOT persisted to disk.")]
        [YamlMember(Alias = "McpAssistantGitHubModelsTokenEnvVar")]
        public string McpAssistantGitHubModelsTokenEnvVar
        {
            get => _mcpAssistantGitHubModelsTokenEnvVar;
            set => SetField(ref _mcpAssistantGitHubModelsTokenEnvVar, value ?? string.Empty);
        }

        [YamlMember(Alias = "McpAssistantGitHubModelsToken")]
        private string McpAssistantGitHubModelsTokenSerialized
        {
            get => string.IsNullOrEmpty(_mcpAssistantGitHubModelsTokenEnvVar)
                ? SecretCipher.Protect(_mcpAssistantGitHubModelsToken)
                : SecretCipher.ReferenceEnv(_mcpAssistantGitHubModelsTokenEnvVar);
            set => AcceptSecretSerialized(value, ref _mcpAssistantGitHubModelsToken, ref _mcpAssistantGitHubModelsTokenEnvVar, "McpAssistantGitHubModelsToken");
        }

        /// <summary>
        /// Shared deserializer for the shadow secret-serialized properties. Routes <c>env:NAME</c>
        /// into the env-var-name slot (and clears the in-memory plaintext), or resolves
        /// <c>dpapi:</c> ciphertext to plaintext. Legacy plaintext is accepted as-is and a
        /// migration notice is printed once per secret so the user knows the next save will
        /// upgrade the on-disk form.
        /// </summary>
        private static void AcceptSecretSerialized(string? incoming, ref string plaintextField, ref string envVarField, string preferenceName)
        {
            string? serialized = incoming;
            if (string.IsNullOrEmpty(serialized))
            {
                plaintextField = string.Empty;
                envVarField = string.Empty;
                return;
            }

            if (SecretCipher.IsEnvReference(serialized, out string envName))
            {
                envVarField = envName;
                plaintextField = string.Empty;
                return;
            }

            envVarField = string.Empty;

            if (SecretCipher.IsLegacyPlaintext(serialized))
            {
                LogSecretMigrationOnce(preferenceName);
                plaintextField = serialized;
                return;
            }

            plaintextField = SecretCipher.Resolve(serialized);
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _migrationNoticeLogged = new();
        private static void LogSecretMigrationOnce(string preferenceName)
        {
            if (_migrationNoticeLogged.TryAdd(preferenceName, true))
                System.Console.WriteLine($"[EditorPreferences] Detected legacy plaintext secret for '{preferenceName}'. The next save will rewrite it as DPAPI-encrypted ciphertext.");
        }
    }
}
