using System.Numerics;
using ImGuiNET;

namespace XREngine.Editor;

internal static class EditorPlayModeConfirmationPrompt
{
    private const string ModalId = "Play Mode Confirmation###EditorPlayModeConfirmation";

    private static PendingAction? _pendingAction;
    private static bool _popupRequested;
    private static bool _popupOpen;

    private enum PendingAction
    {
        EnterPlayMode,
        ExitPlayMode,
    }

    public static void RequestEnterPlayMode()
        => Request(PendingAction.EnterPlayMode);

    public static void RequestExitPlayMode()
        => Request(PendingAction.ExitPlayMode);

    public static void Render()
    {
        if (_pendingAction is null)
            return;

        if (_popupRequested)
        {
            ImGui.OpenPopup(ModalId);
            _popupRequested = false;
            _popupOpen = true;
        }

        ImGui.SetNextWindowSize(new Vector2(420, 0), ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(
            ImGui.GetMainViewport().GetCenter(),
            ImGuiCond.Appearing,
            new Vector2(0.5f, 0.5f));

        bool keepOpen = _popupOpen;
        if (!ImGui.BeginPopupModal(ModalId, ref keepOpen, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove))
        {
            if (!keepOpen)
                Clear();
            return;
        }

        DialogContent content = GetDialogContent(_pendingAction.Value);

        ImGui.TextUnformatted(content.Title);
        ImGui.Spacing();
        ImGui.TextWrapped(content.Message);
        ImGui.Spacing();
        ImGui.TextDisabled(content.PreferenceHint);
        ImGui.Separator();

        if (ImGui.Button(content.ConfirmLabel, new Vector2(170, 0)))
        {
            PendingAction action = _pendingAction.Value;
            Clear();
            ImGui.CloseCurrentPopup();

            if (action == PendingAction.EnterPlayMode)
                EditorState.EnterPlayMode();
            else
                EditorState.ExitPlayMode();
        }

        ImGui.SameLine();

        if (ImGui.Button("Cancel", new Vector2(120, 0)))
        {
            Clear();
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();

        if (!keepOpen)
            Clear();
    }

    private static void Request(PendingAction action)
    {
        _pendingAction = action;
        _popupRequested = true;
        _popupOpen = true;
    }

    private static DialogContent GetDialogContent(PendingAction action)
        => action switch
        {
            PendingAction.EnterPlayMode => new DialogContent(
                "Enter play mode?",
                "This will snapshot the current editor state and start simulation.",
                "You can disable this prompt in Global Editor Preferences under Play Mode.",
                "Enter Play Mode"),
            PendingAction.ExitPlayMode => new DialogContent(
                "Exit play mode?",
                "This will stop simulation and restore the editor state according to the active play-mode configuration.",
                "You can disable this prompt in Global Editor Preferences under Play Mode.",
                "Exit Play Mode"),
            _ => throw new System.ArgumentOutOfRangeException(nameof(action), action, null),
        };

    private static void Clear()
    {
        _pendingAction = null;
        _popupRequested = false;
        _popupOpen = false;
    }

    private readonly record struct DialogContent(string Title, string Message, string PreferenceHint, string ConfirmLabel);
}