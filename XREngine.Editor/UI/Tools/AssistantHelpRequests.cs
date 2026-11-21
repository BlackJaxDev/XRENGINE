using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Editor.UI.Tools;

/// <summary>
/// Passes an editor help selection to the Assistant on the UI thread.
/// The Assistant fills the draft and waits for the user to send it.
/// </summary>
public static class AssistantHelpRequests
{
    private static AssistantHelpRequest? _pending;

    public static void Publish(AssistantHelpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _pending = request with
        {
            Kind = Bound(request.Kind, 64) ?? string.Empty,
            Label = Bound(request.Label, 160) ?? string.Empty,
            UiLocation = Bound(request.UiLocation, 160) ?? string.Empty,
            SymbolName = Bound(request.SymbolName, 512),
            TargetId = Bound(request.TargetId, 64),
            TargetName = Bound(request.TargetName, 160),
            CurrentValue = Bound(request.CurrentValue, 256),
            Description = Bound(request.Description, 512),
            Units = Bound(request.Units, 64),
            Diagnostic = Bound(request.Diagnostic, 1024)
        };
        McpAssistantWindow.Instance.Open();
    }

    private static string? Bound(string? value, int maxLength)
        => value is null || value.Length <= maxLength ? value : value[..maxLength] + "…";

    public static bool TryTake(out AssistantHelpRequest? request)
    {
        request = _pending;
        _pending = null;
        return request is not null;
    }

    /// <summary>Selects the live scene node named by a help request.</summary>
    public static bool ShowTarget(AssistantHelpRequest request)
    {
        if (!Guid.TryParse(request.TargetId, out Guid id)
            || !XRObjectBase.ObjectsCache.TryGetValue(id, out XRObjectBase? target)
            || target.IsDestroyed)
            return false;

        SceneNode? node = target switch
        {
            SceneNode sceneNode => sceneNode,
            XRComponent component => component.SceneNode,
            _ => null
        };
        if (node is null || node.IsDestroyed)
            return false;

        Selection.SceneNodes = [node];
        return true;
    }
}
