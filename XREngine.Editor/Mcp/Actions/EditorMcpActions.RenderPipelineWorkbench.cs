using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using XREngine.Data.Core;
using XREngine.Core;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Pipelines.Overlays;
using XREngine.Rendering.Pipelines.Validation;
using XREngine.Rendering.RenderGraph.Diagnostics;
using XREngine.Rendering.Resources.Diagnostics;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        private const int DefaultStructureCommandLimit = 2000;

        /// <summary>
        /// Returns the command tree of the selected pipeline with anchor ids, patch targets, and overlay markers, plus the
        /// overlays and the post-process stages.
        /// </summary>
        [XRMcp(Name = "get_render_pipeline_structure", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Get the command tree of the selected viewport's render pipeline: command types, labels, anchor ids, patch targets, overlay markers, derived overlays, and post-process stages.")]
        public static Task<McpToolResponse> GetRenderPipelineStructureAsync(
            McpToolContext context,
            [McpName("camera_node_id"), Description("Optional camera node ID to target.")] string? cameraNodeId = null,
            [McpName("vr_eye"), Description("Optional runtime VR viewport: left, right, or stereo.")] string? vrEye = null,
            [McpName("window_index"), Description("Optional window index to target.")] int windowIndex = 0,
            [McpName("viewport_index"), Description("Optional viewport index to target.")] int viewportIndex = 0,
            [McpName("capture_owner_id"), Description("Optional scene/probe/standalone capture component GUID to inspect its offscreen pipeline.")] string? captureOwnerId = null,
            [McpName("max_commands"), Description("Maximum number of commands to return. The default is 2000.")] int maxCommands = DefaultStructureCommandLimit)
        {
            if (!TryResolveWorkbenchPipeline(context, cameraNodeId, vrEye, windowIndex, viewportIndex, captureOwnerId, out RenderPipeline? pipeline, out _, out string? error))
                return Task.FromResult(new McpToolResponse(error!, isError: true));

            int limit = maxCommands > 0 ? maxCommands : DefaultStructureCommandLimit;
            List<object> commands = [];
            bool truncated = CollectStructureCommands(pipeline!, pipeline!.CommandChain, "root", 0, commands, limit);

            object[] stages = pipeline.PostProcessSchema.StagesByKey.Values
                .Select(static stage => (object)new
                {
                    key = stage.Key,
                    display_name = stage.DisplayName,
                    parameter_count = stage.Parameters.Count,
                })
                .ToArray();

            return Task.FromResult(new McpToolResponse(
                $"Listed {commands.Count} command(s){(truncated ? " (truncated)" : string.Empty)} of {pipeline.DebugName}.",
                new
                {
                    pipeline = DescribeWorkbenchPipeline(pipeline),
                    truncated,
                    commands,
                    stages,
                }));
        }

        /// <summary>Returns the latest backend execution plan of the selected pipeline instance.</summary>
        [XRMcp(Name = "get_render_graph_plan", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Get the latest backend render-graph plan of the selected viewport's pipeline instance: pass order, batches, barriers, resource lifetimes, alias groups, and reorders.")]
        public static Task<McpToolResponse> GetRenderGraphPlanAsync(
            McpToolContext context,
            [McpName("camera_node_id"), Description("Optional camera node ID to target.")] string? cameraNodeId = null,
            [McpName("vr_eye"), Description("Optional runtime VR viewport: left, right, or stereo.")] string? vrEye = null,
            [McpName("window_index"), Description("Optional window index to target.")] int windowIndex = 0,
            [McpName("viewport_index"), Description("Optional viewport index to target.")] int viewportIndex = 0,
            [McpName("capture_owner_id"), Description("Optional scene/probe/standalone capture component GUID to inspect its offscreen pipeline.")] string? captureOwnerId = null,
            [McpName("include_barriers"), Description("Include the barrier list. Barrier lists can be long.")] bool includeBarriers = false)
        {
            if (!TryResolveWorkbenchPipeline(context, cameraNodeId, vrEye, windowIndex, viewportIndex, captureOwnerId, out RenderPipeline? pipeline, out XRRenderPipelineInstance? instance, out string? error))
                return Task.FromResult(new McpToolResponse(error!, isError: true));

            AbstractRenderer? renderer = instance?.LastWindowViewport?.Window?.Renderer ?? AbstractRenderer.Current;
            RenderGraphPlanSnapshot? plan = renderer?.PlanDiagnostics?.GetPlanSnapshot(instance);
            if (plan is null)
                return Task.FromResult(new McpToolResponse("The renderer has no plan snapshot for this instance yet. Render a frame, then try again.", isError: true));

            return Task.FromResult(new McpToolResponse(
                $"The plan has {plan.Passes.Count} pass(es) in {plan.Batches.Count} batch(es).",
                new
                {
                    pipeline = DescribeWorkbenchPipeline(pipeline!),
                    origin = plan.Origin.ToString(),
                    revision = plan.Revision,
                    passes = plan.Passes.Select(static pass => new
                    {
                        pass_index = pass.PassIndex,
                        name = pass.Name,
                        stage = pass.Stage.ToString(),
                        execution_order = pass.ExecutionOrder,
                        authored_order = pass.AuthoredOrder,
                        batch_index = pass.BatchIndex,
                        executed = pass.Executed,
                    }).ToArray(),
                    batches = plan.Batches.Select(static batch => new
                    {
                        batch_index = batch.BatchIndex,
                        stage = batch.Stage.ToString(),
                        attachment_signature = batch.AttachmentSignature,
                        pass_indices = batch.PassIndices,
                        submission_index = batch.SubmissionIndex,
                    }).ToArray(),
                    lifetimes = plan.Lifetimes.Select(static lifetime => new
                    {
                        resource = lifetime.ResourceName,
                        first_order = lifetime.FirstOrder,
                        last_order = lifetime.LastOrder,
                        alias_group = lifetime.AliasGroup,
                        transient = lifetime.IsTransient,
                    }).ToArray(),
                    alias_groups = plan.AliasGroups.Select(static group => new
                    {
                        group_index = group.GroupIndex,
                        resources = group.ResourceNames,
                        description = group.Description,
                    }).ToArray(),
                    reorders = plan.Reorders.Select(static reorder => new
                    {
                        pass_index = reorder.PassIndex,
                        authored_position = reorder.AuthoredPosition,
                        execution_position = reorder.ExecutionPosition,
                        cause = reorder.Cause,
                    }).ToArray(),
                    barriers = includeBarriers
                        ? plan.Barriers.Select(static barrier => (object)new
                        {
                            pass_index = barrier.PassIndex,
                            resource = barrier.ResourceName,
                            kind = barrier.Kind,
                            range = barrier.Range,
                            old_layout = barrier.OldLayout,
                            new_layout = barrier.NewLayout,
                            source_stages = barrier.SourceStages,
                            destination_stages = barrier.DestinationStages,
                            source_access = barrier.SourceAccess,
                            destination_access = barrier.DestinationAccess,
                        }).ToArray()
                        : null,
                    barrier_count = plan.Barriers.Count,
                    notes = plan.Notes,
                }));
        }

        /// <summary>Runs the pipeline validator on the selected pipeline, or on an <c>.xrs</c> script file.</summary>
        [XRMcp(Name = "validate_render_pipeline", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Run the render pipeline validator on the selected viewport's pipeline, or load and validate an .xrs script file without installing it. Returns script load problems and validator problems.")]
        public static Task<McpToolResponse> ValidateRenderPipelineAsync(
            McpToolContext context,
            [McpName("script_path"), Description("Optional .xrs script path. When set, the tool validates the script instead of the live pipeline.")] string? scriptPath = null,
            [McpName("backend"), Description("Optional backend: opengl or vulkan. The default is the current renderer, or opengl.")] string? backend = null,
            [McpName("camera_node_id"), Description("Optional camera node ID to target.")] string? cameraNodeId = null,
            [McpName("vr_eye"), Description("Optional runtime VR viewport: left, right, or stereo.")] string? vrEye = null,
            [McpName("window_index"), Description("Optional window index to target.")] int windowIndex = 0,
            [McpName("viewport_index"), Description("Optional viewport index to target.")] int viewportIndex = 0,
            [McpName("capture_owner_id"), Description("Optional scene/probe/standalone capture component GUID to inspect its offscreen pipeline.")] string? captureOwnerId = null)
        {
            RendererBackendId backendId = string.IsNullOrWhiteSpace(backend)
                ? AbstractRenderer.Current?.BackendId ?? RendererBackendId.OpenGL
                : new RendererBackendId(backend.Trim().ToLowerInvariant());

            RenderPipeline? pipeline;
            XRRenderPipelineInstance? instance = null;
            object[] scriptProblems = [];
            if (!string.IsNullOrWhiteSpace(scriptPath))
            {
                if (!System.IO.File.Exists(scriptPath))
                    return Task.FromResult(new McpToolResponse($"The script '{scriptPath}' does not exist.", isError: true));

                RenderPipelineScriptLoadResult loaded = RenderPipelineScriptLoader.LoadFile(scriptPath);
                scriptProblems = loaded.Problems.Select(static problem => (object)new
                {
                    severity = problem.Severity.ToString(),
                    line = problem.Line,
                    column = problem.Column,
                    message = problem.Message,
                }).ToArray();
                pipeline = loaded.Pipeline;
                if (pipeline is null)
                {
                    return Task.FromResult(new McpToolResponse(
                        $"The script did not load: {loaded.Problems.Count} problem(s).",
                        new { script_problems = scriptProblems },
                        isError: true));
                }
            }
            else if (!TryResolveWorkbenchPipeline(context, cameraNodeId, vrEye, windowIndex, viewportIndex, captureOwnerId, out pipeline, out instance, out string? error))
            {
                return Task.FromResult(new McpToolResponse(error!, isError: true));
            }

            RenderPipelineValidationReport report = RenderPipelineValidator.Validate(pipeline!, instance, backendId);
            return Task.FromResult(new McpToolResponse(
                $"{report.ErrorCount} error(s), {report.WarningCount} warning(s), {report.InfoCount} info item(s).",
                new
                {
                    pipeline = DescribeWorkbenchPipeline(pipeline!),
                    backend = backendId.Value,
                    error_count = report.ErrorCount,
                    warning_count = report.WarningCount,
                    info_count = report.InfoCount,
                    script_problems = scriptProblems,
                    problems = report.Problems.Select(static problem => new
                    {
                        rule = problem.RuleId,
                        severity = problem.Severity.ToString(),
                        target = problem.Target.ToString(),
                        command = problem.Command is null ? null : RenderCommandInfo.Get(problem.Command).DisplayName,
                        anchor_id = problem.Command?.AnchorId,
                        pass_index = problem.PassIndex >= 0 ? problem.PassIndex : (int?)null,
                        name = problem.Name,
                        message = problem.Message,
                    }).ToArray(),
                }));
        }

        /// <summary>Describes the logical resources of a pipeline instance: lifetime, producers, consumers, and estimated size.</summary>
        private static object[] DescribeLogicalResources(RenderPipeline pipeline, XRRenderPipelineInstance instance)
        {
            RendererBackendId backend = AbstractRenderer.Current?.BackendId ?? RendererBackendId.OpenGL;
            RenderPipelineResourceSnapshot snapshot = RenderPipelineValidationContext.FromPipeline(pipeline, instance, backend).Resources;
            return snapshot.Entries
                .Select(static entry => (object)new
                {
                    name = entry.Name,
                    kind = entry.Kind?.ToString(),
                    flags = entry.Flags.ToString(),
                    format = entry.FormatText,
                    width = entry.Width,
                    height = entry.Height,
                    layers = entry.Layers,
                    samples = entry.Samples,
                    estimated_bytes = entry.EstimatedBytes,
                    first_use_order = entry.FirstUseOrder,
                    last_use_order = entry.LastUseOrder,
                    producers = entry.Producers,
                    consumers = entry.Consumers,
                    declared_but_unused = entry.IsDeclaredButUnused,
                    used_but_undeclared = entry.IsUsedButUndeclared,
                })
                .ToArray();
        }

        private static bool TryResolveWorkbenchPipeline(
            McpToolContext context,
            string? cameraNodeId,
            string? vrEye,
            int windowIndex,
            int viewportIndex,
            string? captureOwnerId,
            out RenderPipeline? pipeline,
            out XRRenderPipelineInstance? instance,
            out string? error)
        {
            pipeline = null;
            instance = null;
            XRViewport? viewport;
            if (!string.IsNullOrWhiteSpace(captureOwnerId))
            {
                if (!string.IsNullOrWhiteSpace(cameraNodeId) || !string.IsNullOrWhiteSpace(vrEye) || windowIndex != 0 || viewportIndex != 0)
                {
                    error = "capture_owner_id cannot be combined with other viewport selectors.";
                    return false;
                }

                if (!Guid.TryParse(captureOwnerId, out Guid ownerId) || !XRObjectBase.ObjectsCache.TryGetValue(ownerId, out var owner))
                {
                    error = "capture_owner_id must identify a live capture component.";
                    return false;
                }

                viewport = owner switch
                {
                    XREngine.Components.Lights.SceneCaptureComponent capture => capture.CaptureViewport,
                    XREngine.Components.Lights.AdvancedOffscreenTextureCaptureComponent capture => capture.CaptureViewport,
                    XREngine.Components.Lights.MirrorCaptureComponent capture => capture.Viewport,
                    _ => null,
                };
                if (viewport is null)
                {
                    error = "The capture owner has no assigned offscreen viewport.";
                    return false;
                }
            }
            else
            {
                viewport = ResolveViewport(context.World, cameraNodeId, vrEye, windowIndex, viewportIndex, out string? viewportError);
                if (viewport is null)
                {
                    error = viewportError ?? "No viewport found.";
                    return false;
                }
            }

            instance = ResolveSelectedPipelineInstance(viewport, vrEye);
            pipeline = instance.Pipeline;
            if (pipeline is null)
            {
                error = "The selected viewport has no render pipeline.";
                return false;
            }

            error = null;
            return true;
        }

        private static object DescribeWorkbenchPipeline(RenderPipeline pipeline)
            => new
            {
                debug_name = pipeline.DebugName,
                type = pipeline.GetType().FullName,
                is_derived = pipeline.IsDerived,
                overlay_source_path = pipeline.OverlaySourcePath,
                overlays = pipeline.Overlays.Select(static overlay => new
                {
                    name = overlay.Name,
                    parent_path = overlay.ParentPath,
                    base_type = overlay.BaseTypeName,
                    base_hash = overlay.BaseStructureHash is { } hash ? RenderPipelineStructureHash.Format(hash) : null,
                    command_patches = overlay.CommandPatches.Count,
                    resource_patches = overlay.ResourcePatches.Count,
                    schema_patches = overlay.SchemaPatches.Count,
                    settings = overlay.SettingOverrides,
                }).ToArray(),
                overlay_results = pipeline.OverlayReport.CommandResults
                    .Concat(pipeline.OverlayReport.ResourceResults)
                    .Concat(pipeline.OverlayReport.SchemaResults)
                    .Select(static result => new
                    {
                        overlay_index = result.OverlayIndex,
                        patch = result.PatchText,
                        status = result.Status.ToString(),
                        message = result.Message,
                    }).ToArray(),
            };

        /// <summary>Adds each command of <paramref name="container"/> and its children in order.</summary>
        /// <returns>True when the limit stopped the walk.</returns>
        private static bool CollectStructureCommands(RenderPipeline pipeline, ViewportRenderCommandContainer container, string containerName, int depth, List<object> results, int limit)
        {
            List<RenderCommandChildContainer> children = [];
            for (int i = 0; i < container.Count; i++)
            {
                if (results.Count >= limit)
                    return true;

                ViewportRenderCommand command = container[i];
                pipeline.OverlayReport.Markers.TryGetValue(command, out ERenderCommandPatchKind marker);
                results.Add(new
                {
                    depth,
                    container = containerName,
                    type = command.GetType().Name,
                    display_name = RenderCommandInfo.Get(command).DisplayName,
                    label = RenderCommandPaths.GetLabel(command),
                    anchor_id = command.AnchorId,
                    target = RenderCommandPaths.GetTarget(pipeline.CommandChain, command),
                    overlay_patch = pipeline.OverlayReport.Markers.ContainsKey(command) ? marker.ToString() : null,
                });

                children.Clear();
                command.CollectChildContainers(children);
                for (int c = 0; c < children.Count; c++)
                {
                    if (CollectStructureCommands(pipeline, children[c].Container, children[c].Name, depth + 1, results, limit))
                        return true;
                }
            }

            return false;
        }
    }
}
