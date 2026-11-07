using System.ComponentModel;
using System.IO;
using XREngine.Cutscenes;
using XREngine.Data.Core;
using XREngine.Editor.Cutscenes;
using XREngine.Scene;
using XREngine.Scene.Prefabs;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    /// <summary>Opens a cutscene in the editor session, or closes the open cutscene.</summary>
    [XRMcp(Name = "cutscene_open", Permission = McpPermissionLevel.Mutate, PermissionReason = "Changes live scene values until the cutscene closes.")]
    [McpThreadAffinity(McpThreadAffinity.Update)]
    [Description("Open a cutscene. In edit mode this starts a preview that restores the scene when it closes. In play mode this opens a director. Uses asset_path (edit mode only), node_id of a node with a CutsceneDirectorComponent, the selected node, or the only director in the world. Set close to true to end the open cutscene.")]
    public static Task<McpToolResponse> CutsceneOpenAsync(
        McpToolContext context,
        [McpName("node_id"), Description("Optional ID of the node that holds the CutsceneDirectorComponent.")] string? nodeId = null,
        [McpName("asset_path"), Description("Optional path of a cutscene asset, absolute or relative to the game assets folder. Edit mode only.")] string? assetPath = null,
        [McpName("close"), Description("True to end the open cutscene and restore the scene. The other arguments are ignored.")] bool close = false)
    {
        if (close)
        {
            CutscenePreviewSession.Close();
            return Task.FromResult(CutsceneStatusResponse("Closed the cutscene."));
        }

        RuntimeWorld world = context.World;
        bool editing = EditorState.InEditMode;

        if (!string.IsNullOrWhiteSpace(assetPath))
        {
            if (!editing)
                return Task.FromResult(new McpToolResponse("asset_path works only in edit mode. In play mode use a director.", isError: true));

            string fullPath = Path.IsPathRooted(assetPath) ? assetPath : Path.Combine(Engine.Assets.GameAssetsPath, assetPath);
            if (!File.Exists(fullPath))
                return Task.FromResult(new McpToolResponse($"The cutscene asset file was not found: {assetPath}", isError: true));

            CutsceneAsset? loaded = Engine.Assets.Load<CutsceneAsset>(fullPath);
            if (loaded is null)
                return Task.FromResult(new McpToolResponse($"The file is not a cutscene asset: {assetPath}", isError: true));

            CutscenePreviewSession.OpenPreview(loaded, world);
            return Task.FromResult(CutsceneStatusResponse("Opened the cutscene preview."));
        }

        if (!TryFindDirector(world, nodeId, out CutsceneDirectorComponent? director, out string? error))
            return Task.FromResult(new McpToolResponse(error ?? "No director was found.", isError: true));

        if (editing)
        {
            if (director!.Asset is not { } asset)
                return Task.FromResult(new McpToolResponse("The director has no cutscene asset.", isError: true));

            CutscenePreviewSession.OpenPreview(asset, world);
            return Task.FromResult(CutsceneStatusResponse("Opened the cutscene preview."));
        }

        return Task.FromResult(CutscenePreviewSession.OpenDirector(director!)
            ? CutsceneStatusResponse("Opened the director.")
            : CutsceneStatusResponse("The director did not open. Read the diagnostics.", isError: true));
    }

    /// <summary>Moves the open cutscene to a time and evaluates it before the call returns.</summary>
    [XRMcp(Name = "cutscene_seek", Permission = McpPermissionLevel.Mutate, PermissionReason = "Changes live scene values until the cutscene closes.")]
    [McpThreadAffinity(McpThreadAffinity.Update)]
    [Description("Move the open cutscene to a time in seconds. The tool evaluates the tracks before it returns, and the result is limited to the cutscene range. The transforms flush later in the same frame, so a capture after this call shows the new time.")]
    public static Task<McpToolResponse> CutsceneSeekAsync(
        McpToolContext context,
        [McpName("seconds"), Description("The target time in seconds.")] double seconds,
        [McpName("intent"), Description("jump or scrub. Use scrub when more seeks follow.")] string intent = "jump")
    {
        if (!CutscenePreviewSession.IsOpen)
            return Task.FromResult(new McpToolResponse("No cutscene is open. Call cutscene_open first.", isError: true));
        if (!double.IsFinite(seconds))
            return Task.FromResult(new McpToolResponse("seconds must be a finite number.", isError: true));

        ESeekIntent seekIntent;
        if (string.Equals(intent, "jump", StringComparison.OrdinalIgnoreCase))
            seekIntent = ESeekIntent.Jump;
        else if (string.Equals(intent, "scrub", StringComparison.OrdinalIgnoreCase))
            seekIntent = ESeekIntent.Scrub;
        else
            return Task.FromResult(new McpToolResponse($"Unknown intent '{intent}'. Use jump or scrub.", isError: true));

        CutscenePreviewSession.SeekNow(CutsceneTime.FromSeconds(seconds), seekIntent);
        return Task.FromResult(CutsceneStatusResponse("Moved the cutscene."));
    }

    /// <summary>Controls the transport of the open cutscene.</summary>
    [XRMcp(Name = "cutscene_play", Permission = McpPermissionLevel.Mutate, PermissionReason = "Changes live scene values until the cutscene closes.")]
    [McpThreadAffinity(McpThreadAffinity.Update)]
    [Description("Play or pause the open cutscene. A negative rate plays backward. At the end of the range the state becomes Paused.")]
    public static Task<McpToolResponse> CutscenePlayAsync(
        McpToolContext context,
        [McpName("rate"), Description("The playback rate. A negative value plays backward.")] double rate = 1.0,
        [McpName("pause"), Description("True to hold the position. The rate is ignored.")] bool pause = false)
    {
        if (!CutscenePreviewSession.IsOpen)
            return Task.FromResult(new McpToolResponse("No cutscene is open. Call cutscene_open first.", isError: true));
        if (!double.IsFinite(rate))
            return Task.FromResult(new McpToolResponse("rate must be a finite number.", isError: true));

        if (pause)
        {
            CutscenePreviewSession.Pause();
            return Task.FromResult(CutsceneStatusResponse("Paused the cutscene."));
        }

        CutscenePreviewSession.Play(rate);
        return Task.FromResult(CutsceneStatusResponse("Started the cutscene."));
    }

    /// <summary>Reads the state of the open cutscene.</summary>
    [XRMcp(Name = "cutscene_status", Permission = McpPermissionLevel.ReadOnly)]
    [McpThreadAffinity(McpThreadAffinity.Update)]
    [Description("Read the open cutscene: position and duration in seconds, transport state, rate, mode (preview, director, or closed), diagnostics, the open readiness (readyForInstantSeek, pending T0 loads, pending and failed proxy programs, and whether the open sequence finished), and the audio clock (clock mode, fallback, mute, last heard-minus-clock difference in ms, read count, hard resync count, and output latency in ms).")]
    public static Task<McpToolResponse> CutsceneStatusAsync(McpToolContext context)
        => Task.FromResult(CutsceneStatusResponse("Read the cutscene status."));

    private static McpToolResponse CutsceneStatusResponse(string message, bool isError = false)
    {
        List<CutsceneCompileDiagnostic> diagnostics = [];
        CutscenePreviewSession.CopyDiagnostics(diagnostics);

        object[] diagnosticItems = new object[diagnostics.Count];
        for (int i = 0; i < diagnostics.Count; i++)
        {
            CutsceneCompileDiagnostic item = diagnostics[i];
            diagnosticItems[i] = new { track = item.TrackIndex, clip = item.ClipIndex, message = item.Message };
        }

        CutsceneAsset? asset = CutscenePreviewSession.Asset;
        bool open = CutscenePreviewSession.IsOpen;
        string mode = !open ? "closed" : CutscenePreviewSession.Host is not null ? "preview" : "director";
        CutscenePlayer? player = open ? CutscenePreviewSession.Player : null;
        CutsceneOpenSequence? sequence = open ? CutscenePreviewSession.OpenSequence : null;
        CutsceneResidencySession? residency = open ? CutscenePreviewSession.Residency : null;
        return new McpToolResponse(message, new
        {
            open,
            mode,
            asset = asset?.Name,
            position = CutscenePreviewSession.Position.ToSeconds(),
            state = open ? CutscenePreviewSession.State.ToString() : "Closed",
            duration = asset?.Duration.ToSeconds() ?? 0.0,
            rate = CutscenePreviewSession.Rate,
            compileCount = CutscenePreviewSession.Host?.CompileCount ?? 0,
            diagnostics = diagnosticItems,
            readyForInstantSeek = player?.ReadyForInstantSeek ?? false,
            openReadiness = !open ? null : new
            {
                residency = residency is not null,
                pendingT0Loads = residency?.PendingProxyCount ?? 0,
                pendingPrograms = sequence?.PendingProgramCount ?? 0,
                failedPrograms = sequence?.FailedProgramCount ?? 0,
                proxyMaterials = sequence?.ProxyMaterials.Count ?? 0,
                sequenceFinished = sequence?.IsFinished ?? true,
            },
            audioClock = player is null ? null : new
            {
                mode = player.Clock.Mode.ToString(),
                fallback = player.IsAudioClockFallback,
                muted = player.IsAudioMuted,
                differenceMs = player.AudioClockDifference.ToSeconds() * 1000.0,
                reads = player.AudioClockReadCount,
                hardResyncs = player.AudioHardResyncCount,
                latencyMs = player.AudioClockLatencyNanoseconds / 1e6,
            },
        }, isError);
    }

    private static bool TryFindDirector(RuntimeWorld world, string? nodeId, out CutsceneDirectorComponent? director, out string? error)
    {
        director = null;
        if (!string.IsNullOrWhiteSpace(nodeId))
        {
            if (!TryGetNodeById(world, nodeId, out SceneNode? node, out error))
                return false;

            director = node?.GetComponent<CutsceneDirectorComponent>();
            error = director is null ? $"The node '{nodeId}' has no CutsceneDirectorComponent." : null;
            return director is not null;
        }

        error = null;
        director = Selection.LastSceneNode?.GetComponent<CutsceneDirectorComponent>();
        if (director is not null)
            return true;

        int count = 0;
        foreach (SceneNode root in world.RootNodes)
        {
            foreach (SceneNode node in SceneNodePrefabUtility.EnumerateHierarchy(root))
            {
                if (node.GetComponent<CutsceneDirectorComponent>() is not { } found)
                    continue;

                director = found;
                count++;
            }
        }

        if (count == 1)
            return true;

        director = null;
        error = count == 0
            ? "The world has no CutsceneDirectorComponent. Use asset_path, or add a director."
            : $"The world has {count} directors. Use node_id to select one.";
        return false;
    }
}
