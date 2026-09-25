using System.ComponentModel;
using System.Threading.Tasks;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    /// <summary>Reads the editor's OpenXR toggle and local pawn possession state.</summary>
    [XRMcp(Name = "get_editor_openxr_toggle_status", Permission = McpPermissionLevel.ReadOnly)]
    [Description("Read whether the editor OpenXR toggle is available, its requested and runtime states, and the local player's controlled pawn.")]
    public static Task<McpToolResponse> GetEditorOpenXrToggleStatusAsync(McpToolContext context)
        => Task.FromResult(new McpToolResponse("Read editor OpenXR toggle status.", CreateEditorOpenXrToggleStatus()));

    /// <summary>Uses the editor toolbar's runtime and pawn transition path.</summary>
    [XRMcp(Name = "set_editor_openxr_enabled", Permission = McpPermissionLevel.Mutate,
        PermissionReason = "Starts or stops the selected OpenXR runtime and switches local pawn possession when the session changes state.")]
    [McpThreadAffinity(McpThreadAffinity.Main)]
    [Description("Set the editor OpenXR toggle to enabled or disabled. Runtime startup and teardown are asynchronous; inspect toggle status afterward.")]
    public static Task<McpToolResponse> SetEditorOpenXrEnabledAsync(
        McpToolContext context,
        [McpName("enabled"), Description("True to start the selected OpenXR runtime; false to return to desktop mode.")] bool enabled,
        [McpName("runtime"), Description("Required when enabling: Monado for testing or SteamVR for a headset.")] string? runtime = null)
    {
        EditorOpenXrRuntimeChoice? choice = null;
        if (enabled && runtime is not null)
        {
            if (!Enum.TryParse(runtime, true, out EditorOpenXrRuntimeChoice parsed) || !Enum.IsDefined(parsed))
                return Task.FromResult(new McpToolResponse("Runtime must be Monado or SteamVR.", isError: true));
            choice = parsed;
        }
        bool accepted = EditorOpenXrPawnSwitcher.TrySetEnabled(enabled, choice);
        return Task.FromResult(new McpToolResponse(
            accepted ? "Requested editor OpenXR toggle transition." : EditorOpenXrPawnSwitcher.LastError ?? "OpenXR toggle request failed.",
            CreateEditorOpenXrToggleStatus(),
            isError: !accepted));
    }

    private static object CreateEditorOpenXrToggleStatus()
    {
        var pawn = Engine.State.MainPlayer.ControlledPawnComponent;
        var api = RuntimeEngine.VRState.OpenXRApi;
        return new
        {
            available = EditorOpenXrPawnSwitcher.CanToggle,
            requested = EditorOpenXrPawnSwitcher.IsRequested,
            status = EditorOpenXrPawnSwitcher.Status,
            error = EditorOpenXrPawnSwitcher.LastError,
            runtimeState = api?.RuntimeState.ToString(),
            sessionRunning = api?.IsSessionRunning ?? false,
            controlledPawnType = pawn?.GetType().Name,
            controlledPawnNode = pawn?.SceneNode?.Name,
            controlledPawnNodeId = pawn?.SceneNode?.ID,
            ownedVrRigNodeId = EditorOpenXrPawnSwitcher.OwnedVrRigNodeId,
            ownedVrRigIsInEditorScene = EditorOpenXrPawnSwitcher.OwnedVrRigIsInEditorScene
        };
    }

    /// <summary>Reads current session, submission and retirement evidence without forcing completion.</summary>
    [XRMcp(Name = "get_openxr_runtime_diagnostics", Permission = McpPermissionLevel.ReadOnly)]
    [Description("Read the current OpenXR session summary, exact submission ownership ledger and deferred swapchain retirement counters. Does not wait for GPU completion.")]
    public static Task<McpToolResponse> GetOpenXrRuntimeDiagnosticsAsync(McpToolContext context)
    {
        OpenXRAPI? api = RuntimeEngine.VRState.OpenXRApi;
        if (api is null)
            return Task.FromResult(new McpToolResponse("No OpenXR API has been created.", isError: true));

        OpenXrSmokeSummary summary = api.CreateSmokeSummary();
        if (api.Window?.Renderer?.BackendId == RendererBackendId.Vulkan &&
            EditorRendererCapabilityResolver.TryGetRegistered(
                RendererBackendId.Vulkan, out IOpenXrSmokeDiagnosticsBackendCapability diagnostics))
            summary.SubmissionValidation = diagnostics.CaptureOpenXrSubmissionValidation();

        return Task.FromResult(new McpToolResponse("Read current OpenXR runtime diagnostics.", summary));
    }

    /// <summary>Requests the real runtime STOPPING transition for the active session.</summary>
    [XRMcp(Name = "request_openxr_session_exit", Permission = McpPermissionLevel.Mutate,
        PermissionReason = "Requests an orderly exit of the active OpenXR session through xrRequestExitSession.")]
    [McpThreadAffinity(McpThreadAffinity.Main)]
    [Description("Request orderly exit of the active OpenXR session through the runtime. Inspect runtime diagnostics to observe completion.")]
    public static Task<McpToolResponse> RequestOpenXrSessionExitAsync(McpToolContext context)
    {
        OpenXRAPI? api = RuntimeEngine.VRState.OpenXRApi;
        if (api is null || !RuntimeEngine.VRState.IsOpenXRActive || !api.IsSessionRunning)
            return Task.FromResult(new McpToolResponse("No active OpenXR session is available.", isError: true));

        api.RequestSmokeSessionExit();
        return Task.FromResult(new McpToolResponse("Requested OpenXR session exit; completion remains asynchronous."));
    }

    /// <summary>Resumes runtime monitoring on the already configured OpenXR window.</summary>
    [XRMcp(Name = "request_openxr_session_start", Permission = McpPermissionLevel.Mutate,
        PermissionReason = "Resumes OpenXR runtime monitoring and session creation on the configured editor window.")]
    [McpThreadAffinity(McpThreadAffinity.Main)]
    [Description("Request OpenXR session startup on the configured window after an orderly exit. Inspect runtime diagnostics to observe asynchronous creation or an explicit recovery failure.")]
    public static Task<McpToolResponse> RequestOpenXrSessionStartAsync(McpToolContext context)
    {
        OpenXRAPI? api = RuntimeEngine.VRState.OpenXRApi;
        if (api?.Window is null)
            return Task.FromResult(new McpToolResponse("No configured OpenXR window is available.", isError: true));
        if (RuntimeEngine.VRState.IsOpenVRActive)
            return Task.FromResult(new McpToolResponse("Stop active OpenVR presentation before starting OpenXR.", isError: true));
        if (api.IsSessionRunning)
            return Task.FromResult(new McpToolResponse("The OpenXR session is already running."));

        bool requested = RuntimeEngine.VRState.InitializeOpenXR(api.Window);
        return Task.FromResult(new McpToolResponse(
            requested
                ? "Requested OpenXR session startup; completion remains asynchronous."
                : "OpenXR runtime monitoring could not be started.",
            isError: !requested));
    }
}
