using ImGuiNET;

namespace XREngine.Editor.UI.Tools;

/// <summary>
/// Reports subscription (signed-in local client) access for the Assistant and the shader editor.
/// This build uses the standalone broker package for API access only. The local client runtimes
/// are not included, so every subscription client reports that it is unavailable and is never
/// ready to send a request.
/// </summary>
internal static class SubscriptionEditorConnection
{
    /// <summary>The status text that the Assistant and the shader editor show for subscription access.</summary>
    public const string UnavailableMessage =
        "Subscription access is not available in this build. Use API key access.";

    /// <summary>Returns no model, because no subscription client can connect.</summary>
    public static string Model(SubscriptionAssistantBrand brand, bool shaderModelOverride = false)
        => string.Empty;

    /// <summary>Returns false: no subscription client can connect in this build.</summary>
    public static bool IsConnected(SubscriptionAssistantBrand brand)
        => false;

    /// <summary>Returns false, so the send buttons stay disabled for subscription access.</summary>
    public static bool IsModelReady(SubscriptionAssistantBrand brand, bool shaderModelOverride = false)
        => false;

    /// <summary>Returns false: no connection check runs.</summary>
    public static bool IsChecking(SubscriptionAssistantBrand brand)
        => false;

    /// <summary>Returns no effort names, because no subscription model is available.</summary>
    public static IReadOnlyList<string> SupportedReasoningEfforts(SubscriptionAssistantBrand brand)
        => [];

    /// <summary>Returns false: no subscription conversation exists to replay.</summary>
    public static bool SupportsConversationReplay(SubscriptionAssistantBrand brand)
        => false;

    /// <summary>Returns false: no sign-in runs.</summary>
    public static bool IsSigningIn(SubscriptionAssistantBrand brand)
        => false;

    /// <summary>Returns <see cref="UnavailableMessage"/>.</summary>
    public static string Status(SubscriptionAssistantBrand brand)
        => UnavailableMessage;

    /// <summary>Returns an empty version, because no local client runtime is loaded.</summary>
    public static string RuntimeVersion(SubscriptionAssistantBrand brand)
        => string.Empty;

    /// <summary>Returns 0: the connection state never changes.</summary>
    public static int ConnectionRevision(SubscriptionAssistantBrand brand)
        => 0;

    /// <summary>Does nothing: no connection check is available.</summary>
    public static void EnsureConnectionChecked(SubscriptionAssistantBrand brand)
    {
    }

    /// <summary>Completes at once: no connection check is available.</summary>
    public static Task ProbeAsync(SubscriptionAssistantBrand brand)
        => Task.CompletedTask;

    /// <summary>Does nothing: no account context exists.</summary>
    public static void RecordAccountFingerprint(SubscriptionAssistantBrand brand, string? fingerprint)
    {
    }

    /// <summary>Does nothing: no account context exists.</summary>
    public static void ResetAccountContext(SubscriptionAssistantBrand brand)
    {
    }

    /// <summary>Shows <see cref="UnavailableMessage"/> where the model picker would be.</summary>
    public static void DrawModelPicker(SubscriptionAssistantBrand brand, string label, bool shaderModelOverride = false)
        => ImGui.TextDisabled(UnavailableMessage);

    /// <summary>Shows nothing: no connection actions are available.</summary>
    public static void DrawConnectionActions(SubscriptionAssistantBrand brand)
    {
    }

    /// <summary>Shows nothing: no local client executable is used.</summary>
    public static void DrawExecutableSettings(SubscriptionAssistantBrand brand)
    {
    }
}
