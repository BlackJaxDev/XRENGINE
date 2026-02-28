using ImGuiNET;

namespace XREngine.Rendering.UI;

/// <summary>
/// Reusable ImGui helpers for text input fields: right-click context menus
/// with Copy / Paste / Clear, and related clipboard convenience methods.
/// Call <see cref="DrawTextFieldContextMenu"/> immediately after an
/// <c>ImGui.InputText</c> or <c>ImGui.InputTextMultiline</c> call.
/// </summary>
public static class ImGuiTextFieldHelper
{
    /// <summary>
    /// Draws a right-click context menu on the last ImGui item (typically an InputText).
    /// Provides Copy All, Paste (replace), and Clear operations.
    /// Returns <c>true</c> if <paramref name="value"/> was modified.
    /// </summary>
    /// <param name="id">Unique popup ID, e.g. <c>"ctx_MyField"</c>.</param>
    /// <param name="value">Reference to the text field's backing string.</param>
    public static bool DrawTextFieldContextMenu(string id, ref string value)
    {
        bool changed = false;

        if (!ImGui.BeginPopupContextItem(id))
            return false;

        if (ImGui.MenuItem("Copy All", "Ctrl+C", false, !string.IsNullOrEmpty(value)))
            ImGui.SetClipboardText(value);

        string? clip = ImGui.GetClipboardText();
        bool hasClip = !string.IsNullOrEmpty(clip);

        if (ImGui.MenuItem("Paste", "Ctrl+V", false, hasClip))
        {
            value = clip!;
            changed = true;
        }

        ImGui.Separator();

        if (ImGui.MenuItem("Clear", null, false, !string.IsNullOrEmpty(value)))
        {
            value = string.Empty;
            changed = true;
        }

        ImGui.EndPopup();
        return changed;
    }

    /// <summary>
    /// Draws a right-click context menu tailored for password / secret fields.
    /// Provides Paste, Clear, and Copy (plain text) operations.
    /// Returns <c>true</c> if <paramref name="value"/> was modified.
    /// </summary>
    /// <param name="id">Unique popup ID.</param>
    /// <param name="value">Reference to the secret field's backing string.</param>
    public static bool DrawSecretFieldContextMenu(string id, ref string value)
    {
        bool changed = false;

        if (!ImGui.BeginPopupContextItem(id))
            return false;

        string? clip = ImGui.GetClipboardText();
        bool hasClip = !string.IsNullOrEmpty(clip);

        if (ImGui.MenuItem("Paste", "Ctrl+V", false, hasClip))
        {
            value = clip!;
            changed = true;
        }

        if (ImGui.MenuItem("Clear"))
        {
            value = string.Empty;
            changed = true;
        }

        if (ImGui.MenuItem("Copy (plain text)", null, false, !string.IsNullOrEmpty(value)))
            ImGui.SetClipboardText(value);

        ImGui.EndPopup();
        return changed;
    }
}
