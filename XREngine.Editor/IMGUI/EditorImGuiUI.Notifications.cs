using System.Globalization;
using System.Numerics;
using ImGuiNET;
using XREngine.Diagnostics;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static int _notificationMissingCount = -1;
    private static int _notificationRebasedCount = -1;
    private static string _notificationCountLabel = "0";
    private static string _missingAssetNotificationLabel = string.Empty;
    private static string _rebasedAssetNotificationLabel = string.Empty;
    private static bool _focusMissingAssets;

    /// <summary>
    /// Updates labels only when the diagnostic counts change.
    /// Repeated reports of the same reference do not increase the badge count.
    /// </summary>
    private static void UpdateNotificationLabels()
    {
        int missingCount = AssetDiagnostics.TrackedMissingAssetCount;
        int rebasedCount = AssetDiagnostics.TrackedRebasedAssetCount;
        if (missingCount == _notificationMissingCount && rebasedCount == _notificationRebasedCount)
            return;

        _notificationMissingCount = missingCount;
        _notificationRebasedCount = rebasedCount;
        _notificationCountLabel = ((long)missingCount + rebasedCount).ToString(CultureInfo.InvariantCulture);
        _missingAssetNotificationLabel = $"Missing or unreadable assets ({missingCount})###MissingAssetNotification";
        _rebasedAssetNotificationLabel = $"Repaired asset references ({rebasedCount})###RebasedAssetNotification";
    }

    private static float GetNotificationMenuWidth()
        => ImGui.GetFontSize() + ImGui.GetStyle().ItemInnerSpacing.X
            + ImGui.CalcTextSize(_notificationCountLabel).X + ImGui.GetStyle().FramePadding.X * 2f;

    /// <summary>
    /// Draws asset events without changing panel visibility until an event is selected.
    /// </summary>
    private static void DrawNotificationMenu(float cursorX)
    {
        var style = ImGui.GetStyle();
        float fontSize = ImGui.GetFontSize();
        ImGui.SetCursorPosX(cursorX);
        if (ImGui.Button("##EditorNotifications", new Vector2(GetNotificationMenuWidth(), ImGui.GetFrameHeight())))
            ImGui.OpenPopup("EditorNotificationsPopup");

        Vector2 min = ImGui.GetItemRectMin();
        Vector2 max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        uint color = ImGui.GetColorU32(ImGuiCol.Text);
        Vector2 center = new(min.X + style.FramePadding.X + fontSize * 0.5f, (min.Y + max.Y) * 0.5f);
        float radius = fontSize * 0.3f;

        // Draw the bell with lines so it does not require an icon font or a texture.
        drawList.PathArcTo(center - new Vector2(0f, radius * 0.3f), radius, MathF.PI, MathF.Tau, 12);
        drawList.PathLineTo(center + new Vector2(radius * 1.2f, radius));
        drawList.PathLineTo(center + new Vector2(-radius * 1.2f, radius));
        drawList.PathStroke(color, ImDrawFlags.Closed, 1.5f);
        drawList.AddCircleFilled(center + new Vector2(0f, radius * 1.45f), radius * 0.22f, color, 8);
        drawList.AddLine(center - new Vector2(0f, radius * 1.3f), center - new Vector2(0f, radius * 1.65f), color, 1.5f);
        drawList.AddText(new Vector2(min.X + style.FramePadding.X + fontSize + style.ItemInnerSpacing.X,
            center.Y - fontSize * 0.5f), color, _notificationCountLabel);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Notifications: tracked asset references");

        ImGui.SetNextWindowPos(new Vector2(max.X, max.Y), ImGuiCond.Appearing, new Vector2(1f, 0f));
        if (!ImGui.BeginPopup("EditorNotificationsPopup"))
            return;

        ImGui.TextUnformatted("Notifications");
        ImGui.Separator();
        if (_notificationMissingCount == 0 && _notificationRebasedCount == 0)
            ImGui.TextDisabled("No notifications.");
        if (_notificationMissingCount > 0 && ImGui.MenuItem(_missingAssetNotificationLabel))
            OpenMissingAssetsFromNotification();
        if (_notificationRebasedCount > 0 && ImGui.MenuItem(_rebasedAssetNotificationLabel))
            OpenMissingAssetsFromNotification();
        ImGui.EndPopup();
    }

    private static void OpenMissingAssetsFromNotification()
    {
        _showMissingAssets = true;
        _focusMissingAssets = true;
    }
}
