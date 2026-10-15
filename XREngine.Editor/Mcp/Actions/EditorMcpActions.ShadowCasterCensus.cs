using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Shadows;
using XREngine.Scene;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    private const int ShadowCasterCensusDefaultCandidates = 32;
    private const int ShadowCasterCensusMaxCandidates = 256;
    private const int ShadowCasterCensusDefaultTimeoutMs = 2000;
    private const int ShadowCasterCensusMaxTimeoutMs = 60000;
    private const int ShadowCasterCensusPollIntervalMs = 10;

    private const string ShadowCasterCaptureCompleted = "completed";
    private const string ShadowCasterCaptureTimedOut = "timed_out";
    private const string ShadowCasterCaptureNotGpuDispatch = "not_gpu_dispatch";
    private const string ShadowCasterCaptureNoMainViewCollection = "no_main_view_collection";

    /// <summary>
    /// Reads the shadow-only caster census of each world, and optionally captures the candidates that the next
    /// main-view GPU collection counts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The census decides whether the primary directional shadow views must still collect under a zero-readback
    /// strategy. A capture shows which renderables keep that decision true. The collect threads only write references,
    /// enum values and value copies into a buffer that this tool allocates.
    /// </para>
    /// <para>
    /// The tool requests a capture only for a world that can complete one: a world with GPU dispatch and a main-view
    /// GPU collection in the previous or current collect pass. It reads the completion of each capture one time, and
    /// uses that value for the message and the payload.
    /// </para>
    /// <para>
    /// The candidate reason, layer, flags and callback are values that the collect thread copied. The scene node name,
    /// path and ID and the command types are live values that the tool reads when it formats the result. When the host
    /// dispatches the tool on the app thread, the tool formats the result on the app thread. Under other dispatch modes
    /// the tool reads these values from its own thread, as other read-only scene tools do.
    /// </para>
    /// </remarks>
    [XRMcp(Name = "get_shadow_caster_census", Permission = McpPermissionLevel.ReadOnly)]
    [Description("Read the shadow-only caster census that gates the primary directional shadow views under a zero-readback strategy: GPU dispatch state, previous and current candidate counts, previous and current main-view GPU collection counts, the shadow caster membership revision against its pass-start value, MayHaveShadowOnlyCasters, and the last atlas planning decision. With capture_candidates, also capture the candidates that the next completed main-view GPU collection counts, with reason, owner, scene node, layer, flags, pre-collection callback method and command types. A world without GPU dispatch or without a recent main-view GPU collection gets no capture.")]
    public static async Task<McpToolResponse> GetShadowCasterCensusAsync(
        McpToolContext context,
        [McpName("capture_candidates"), Description("Capture the candidates of the next completed main-view GPU collection in each world that can complete a capture.")]
        bool captureCandidates = true,
        [McpName("max_candidates"), Description("Maximum candidates to record for each world, 1 through 256.")]
        int maxCandidates = ShadowCasterCensusDefaultCandidates,
        [McpName("timeout_ms"), Description("Maximum time to wait for the captures, 1 through 60000 milliseconds.")]
        int timeoutMs = ShadowCasterCensusDefaultTimeoutMs,
        CancellationToken token = default)
    {
        if (maxCandidates is < 1 or > ShadowCasterCensusMaxCandidates)
            return new McpToolResponse($"max_candidates must be between 1 and {ShadowCasterCensusMaxCandidates}.", isError: true);
        if (timeoutMs is < 1 or > ShadowCasterCensusMaxTimeoutMs)
            return new McpToolResponse($"timeout_ms must be between 1 and {ShadowCasterCensusMaxTimeoutMs}.", isError: true);

        List<(RuntimeWorld World, IRuntimeRenderWorld RenderWorld)> worlds = [];
        foreach (RuntimeWorld world in Engine.WorldInstances)
            if (world.TryGetCapability<IRuntimeRenderWorld>(out IRuntimeRenderWorld? renderWorld) && renderWorld is not null)
                worlds.Add((world, renderWorld));

        if (worlds.Count == 0)
            return new McpToolResponse("No world with a rendering capability is active.", isError: true);

        // The result reads scene nodes. When the host dispatched this tool on the app thread, format the result there.
        bool formatOnAppThread = Engine.IsAppThread;

        ShadowOnlyCasterCensusCapture?[] captures = new ShadowOnlyCasterCensusCapture?[worlds.Count];
        string?[] captureStates = new string?[worlds.Count];
        int requestedCaptures = 0;
        long waitStart = Environment.TickCount64;
        if (captureCandidates)
        {
            // Read each census before the request. A world that cannot complete a capture gets no request, so it does
            // not hold the wait until the timeout. Request all other captures before the wait, so that the worlds
            // capture in the same frames.
            for (int i = 0; i < worlds.Count; i++)
            {
                VisualScene3D scene = worlds[i].RenderWorld.VisualScene;
                ShadowOnlyCasterCensusSnapshot census = scene.GetShadowOnlyCasterCensusSnapshot();
                if (!census.IsGpuDispatchActive)
                {
                    captureStates[i] = ShadowCasterCaptureNotGpuDispatch;
                    continue;
                }
                if (census.PreviousMainViewGpuCollections == 0 && census.CurrentMainViewGpuCollections == 0)
                {
                    captureStates[i] = ShadowCasterCaptureNoMainViewCollection;
                    continue;
                }

                captures[i] = scene.RequestShadowOnlyCasterCensusCapture(maxCandidates);
                requestedCaptures++;
            }

            if (requestedCaptures > 0)
            {
                try
                {
                    long deadline = waitStart + timeoutMs;
                    while (!AllShadowCasterCapturesCompleted(captures) && Environment.TickCount64 < deadline)
                        await Task.Delay(ShadowCasterCensusPollIntervalMs, token).ConfigureAwait(false);
                }
                finally
                {
                    // A capture that no collection claimed must not stay installed after the wait.
                    for (int i = 0; i < worlds.Count; i++)
                        if (captures[i] is { IsCompleted: false } pending)
                            worlds[i].RenderWorld.VisualScene.CancelShadowOnlyCasterCensusCapture(pending);
                }
            }
        }
        long waitMs = Environment.TickCount64 - waitStart;

        // A claimed capture can complete after the cancel. Read the completion one time, and use that value below.
        int completedCaptures = 0;
        for (int i = 0; i < worlds.Count; i++)
        {
            if (captures[i] is not { } capture)
                continue;
            bool completed = capture.IsCompleted;
            captureStates[i] = completed ? ShadowCasterCaptureCompleted : ShadowCasterCaptureTimedOut;
            if (completed)
                completedCaptures++;
        }

        object[] worldPayloads;
        if (formatOnAppThread && !Engine.IsAppThread)
        {
            TaskCompletionSource<object[]> formatted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Engine.EnqueueAppThreadTask(() =>
            {
                try
                {
                    formatted.TrySetResult(BuildShadowCasterCensusWorldPayloads(worlds, captures, captureStates));
                }
                catch (Exception ex)
                {
                    formatted.TrySetException(ex);
                }
            }, "MCP: Format shadow caster census");

            try
            {
                worldPayloads = await formatted.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new McpToolResponse("Timed out waiting for the app thread to format the shadow caster census.", isError: true);
            }
        }
        else
        {
            worldPayloads = BuildShadowCasterCensusWorldPayloads(worlds, captures, captureStates);
        }

        int skippedCaptures = captureCandidates ? worlds.Count - requestedCaptures : 0;
        string message = !captureCandidates
            ? "Read the shadow-only caster census."
            : $"Read the shadow-only caster census. Captures: {completedCaptures} completed, {requestedCaptures - completedCaptures} timed out, {skippedCaptures} not requested (no GPU dispatch, or no main-view GPU collection in the previous or current pass).";
        return new McpToolResponse(message, new
        {
            capture_requested = captureCandidates,
            capture_wait_ms = captureCandidates ? waitMs : 0,
            worlds = worldPayloads,
        });
    }

    private static bool AllShadowCasterCapturesCompleted(ShadowOnlyCasterCensusCapture?[] captures)
    {
        for (int i = 0; i < captures.Length; i++)
            if (captures[i] is { IsCompleted: false })
                return false;
        return true;
    }

    private static object[] BuildShadowCasterCensusWorldPayloads(
        List<(RuntimeWorld World, IRuntimeRenderWorld RenderWorld)> worlds,
        ShadowOnlyCasterCensusCapture?[] captures,
        string?[] captureStates)
    {
        object[] payloads = new object[worlds.Count];
        for (int i = 0; i < worlds.Count; i++)
            payloads[i] = BuildShadowCasterCensusWorldPayload(worlds[i].World, worlds[i].RenderWorld, captures[i], captureStates[i]);
        return payloads;
    }

    private static object BuildShadowCasterCensusWorldPayload(
        RuntimeWorld world,
        IRuntimeRenderWorld renderWorld,
        ShadowOnlyCasterCensusCapture? capture,
        string? captureState)
    {
        ShadowOnlyCasterCensusSnapshot census = renderWorld.VisualScene.GetShadowOnlyCasterCensusSnapshot();
        ShadowOnlyCasterConsumerDecision decision = renderWorld.Lights.GetLastShadowOnlyCasterConsumerDecision();
        return new
        {
            world_name = world.TargetWorld?.Name,
            world_id = world.TargetWorld?.ID,
            play_state = world.PlayState.ToString(),
            census = new
            {
                is_gpu_dispatch_active = census.IsGpuDispatchActive,
                previous_candidates = census.PreviousCandidates,
                current_candidates = census.CurrentCandidates,
                previous_main_view_gpu_collections = census.PreviousMainViewGpuCollections,
                current_main_view_gpu_collections = census.CurrentMainViewGpuCollections,
                shadow_caster_membership_revision = census.ShadowCasterMembershipRevision,
                membership_revision_at_pass_start = census.MembershipRevisionAtPassStart,
                membership_changed_this_pass = census.ShadowCasterMembershipRevision != census.MembershipRevisionAtPassStart,
                may_have_shadow_only_casters = census.MayHaveShadowOnlyCasters,
                capture_pending = census.CapturePending,
            },
            planner = decision.HasDecision
                ? new
                {
                    plan_frame_id = decision.PlanFrameId,
                    shadow_only_casters = decision.ShadowOnlyCasters,
                    camera_set_changed = decision.CameraSetChanged,
                    zero_readback_strategy = decision.ZeroReadbackStrategy,
                    scene_may_have_shadow_only_casters = decision.SceneMayHaveShadowOnlyCasters,
                }
                : null,
            capture = BuildShadowCasterCapturePayload(capture, captureState),
        };
    }

    private static object? BuildShadowCasterCapturePayload(ShadowOnlyCasterCensusCapture? capture, string? captureState)
    {
        if (captureState is null)
            return null;

        // The state is completed only when the tool saw the completion, which publishes the records.
        if (capture is null || captureState != ShadowCasterCaptureCompleted)
            return new { state = captureState, capacity = capture?.Capacity };

        int recorded = capture.RecordedCount;
        object[] candidates = new object[recorded];
        for (int i = 0; i < recorded; i++)
            candidates[i] = BuildShadowCasterCandidatePayload(capture.GetRecord(i));

        return new
        {
            state = captureState,
            capacity = capture.Capacity,
            candidate_count = capture.CandidateCount,
            recorded_count = recorded,
            truncated = capture.CandidateCount > recorded,
            collect_mirrors = capture.CollectMirrors,
            camera = DescribeShadowCasterCaptureCamera(capture.Camera),
            candidates,
        };
    }

    private static object? DescribeShadowCasterCaptureCamera(IRuntimeCullingCamera? camera)
    {
        if (camera is null)
            return null;

        SceneNode? node = (camera as XRCamera)?.Transform?.SceneNode;
        return new
        {
            type = camera.GetType().Name,
            identity = RuntimeHelpers.GetHashCode(camera),
            culling_layer_mask = camera.CullingLayerMask,
            node_name = node?.Name,
            node_id = node?.ID,
            node_path = node is null ? null : BuildNodePath(node),
        };
    }

    private static object BuildShadowCasterCandidatePayload(ShadowOnlyCasterCandidateRecord record)
    {
        SceneNode? node = (record.Owner as XRComponent)?.SceneNode;
        return new
        {
            reason = record.Reason.ToString(),
            owner_type = record.Owner?.GetType().FullName,
            render_info_type = record.RenderInfo.GetType().Name,
            node_name = node?.Name,
            node_path = node is null ? null : BuildNodePath(node),
            node_id = node?.ID,
            layer = record.Layer,
            casts_shadows = record.CastsShadows,
            is_visible = record.IsVisible,
            disposed = record.DisposalRequested,
            retired = record.RetirementRequested,
            callback = DescribeShadowCasterCallback(record),
            command_types = DescribeShadowCasterCommandTypes(record.RenderInfo),
        };
    }

    private static object? DescribeShadowCasterCallback(ShadowOnlyCasterCandidateRecord record)
    {
        if (record.PreCollectCommandsCallback is not { } callback)
            return null;

        Delegate[] invocations = callback.GetInvocationList();
        string[] methods = new string[invocations.Length];
        for (int i = 0; i < invocations.Length; i++)
            methods[i] = $"{invocations[i].Method.DeclaringType?.FullName}.{invocations[i].Method.Name}";

        return new
        {
            declaring_type = callback.Method.DeclaringType?.FullName,
            method = callback.Method.Name,
            declared_camera_independent = record.CallbackDeclaredCameraIndependent,
            invocation_methods = methods,
        };
    }

    private static string[] DescribeShadowCasterCommandTypes(RenderInfo3D info)
    {
        // The command list is live. Its indexer returns null for an index that a concurrent removal made invalid, so
        // the loop skips that index.
        List<string> types = [];
        var commands = info.RenderCommands;
        for (int i = 0; i < commands.Count; i++)
            if (commands[i] is RenderCommand command)
                types.Add(command.GetType().Name);
        return [.. types];
    }
}
