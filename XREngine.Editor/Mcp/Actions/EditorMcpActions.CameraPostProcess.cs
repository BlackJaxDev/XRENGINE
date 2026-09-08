using System;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.PostProcessing;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    private static readonly JsonSerializerOptions s_postProcessValueOptions = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
    };

    private static object? SerializePostProcessValue(object? value)
        => value switch
        {
            Vector2 vector => new { x = vector.X, y = vector.Y },
            Vector3 vector => new { x = vector.X, y = vector.Y, z = vector.Z },
            Vector4 vector => new { x = vector.X, y = vector.Y, z = vector.Z, w = vector.W },
            _ => value,
        };

    /// <summary>Reads the selected output camera's actual pipeline stage settings.</summary>
    [XRMcp(Name = "get_camera_post_process", Permission = McpPermissionLevel.ReadOnly)]
    [McpThreadAffinity(McpThreadAffinity.Main)]
    [Description("Read current post-process stage keys, parameter values, ranges and enum options for a desktop or VR camera.")]
    public static Task<McpToolResponse> GetCameraPostProcessAsync(
        McpToolContext context,
        [McpName("camera_node_id")] string? cameraNodeId = null,
        [McpName("vr_eye")] string? vrEye = null,
        [McpName("window_index")] int windowIndex = 0,
        [McpName("viewport_index")] int viewportIndex = 0)
    {
        XRViewport? viewport = ResolveViewport(context.World, cameraNodeId, vrEye, windowIndex, viewportIndex, out string? error);
        PipelinePostProcessState? state = viewport?.ActiveCamera?.GetPostProcessState(viewport.RenderPipeline);
        if (state is null)
            return Task.FromResult(new McpToolResponse(error ?? "The selected output has no camera post-process state.", isError: true));

        return Task.FromResult(new McpToolResponse("Read camera post-process settings.", new
        {
            pipeline = state.PipelineName,
            tsrRenderScaleOverride = viewport!.ActiveCamera!.TsrRenderScaleOverride,
            stages = state.Stages.Values.Select(stage => new
            {
                key = stage.StageKey,
                parameters = stage.Descriptor?.Parameters.Select(parameter => new
                {
                    name = parameter.Name,
                    kind = parameter.Kind.ToString(),
                    value = SerializePostProcessValue(stage.GetValue(parameter.Name)),
                    parameter.Min,
                    parameter.Max,
                    parameter.EnumOptions,
                }).ToArray(),
            }).ToArray(),
        }));
    }

    /// <summary>Sets or clears a selected camera's live TSR render-scale override.</summary>
    [XRMcp(Name = "set_camera_tsr_render_scale", Permission = McpPermissionLevel.Mutate,
        PermissionReason = "Changes a selected camera's live TSR internal render scale.")]
    [McpThreadAffinity(McpThreadAffinity.Main)]
    [Description("Set the selected camera's TSR render scale from 0.5 to 1.0; omit scale to restore the global setting. Takes effect when TSR is active. Resource and history changes are applied by the next frame publication.")]
    public static Task<McpToolResponse> SetCameraTsrRenderScaleAsync(
        McpToolContext context,
        [McpName("scale")] float? scale = null,
        [McpName("camera_node_id")] string? cameraNodeId = null,
        [McpName("vr_eye")] string? vrEye = null,
        [McpName("window_index")] int windowIndex = 0,
        [McpName("viewport_index")] int viewportIndex = 0)
    {
        if (scale is float value && (!float.IsFinite(value) || value < 0.5f || value > 1.0f))
            return Task.FromResult(new McpToolResponse("TSR scale must be finite and between 0.5 and 1.0.", isError: true));

        XRViewport? viewport = ResolveViewport(context.World, cameraNodeId, vrEye, windowIndex, viewportIndex, out string? error);
        XRCamera? camera = viewport?.ActiveCamera;
        if (camera is null)
            return Task.FromResult(new McpToolResponse(error ?? "The selected output has no camera.", isError: true));

        camera.TsrRenderScaleOverride = scale;
        return Task.FromResult(new McpToolResponse("Updated camera TSR render scale.", new
        {
            cameraNodeId = camera.Transform.SceneNode?.ID,
            tsrRenderScaleOverride = camera.TsrRenderScaleOverride,
        }));
    }

    /// <summary>Changes one declared parameter through its existing backing-settings notification path.</summary>
    [XRMcp(Name = "set_camera_post_process_parameter", Permission = McpPermissionLevel.Mutate,
        PermissionReason = "Changes a selected camera's live post-process parameter.")]
    [McpThreadAffinity(McpThreadAffinity.Main)]
    [Description("Set one declared camera post-process parameter. Read get_camera_post_process first for exact keys, types, ranges and enum values. Applies only to the selected camera.")]
    public static Task<McpToolResponse> SetCameraPostProcessParameterAsync(
        McpToolContext context,
        [McpName("stage_key")] string stageKey,
        [McpName("parameter_name")] string parameterName,
        [McpName("value")] object value,
        [McpName("camera_node_id")] string? cameraNodeId = null,
        [McpName("vr_eye")] string? vrEye = null,
        [McpName("window_index")] int windowIndex = 0,
        [McpName("viewport_index")] int viewportIndex = 0)
    {
        XRViewport? viewport = ResolveViewport(context.World, cameraNodeId, vrEye, windowIndex, viewportIndex, out string? error);
        PostProcessStageState? stage = viewport?.ActiveCamera?.GetPostProcessState(viewport.RenderPipeline)?.GetStage(stageKey);
        PostProcessParameterDescriptor? parameter = stage?.Descriptor?.Parameters.FirstOrDefault(
            item => string.Equals(item.Name, parameterName, StringComparison.OrdinalIgnoreCase));
        Type? valueType = parameter?.DefaultValue?.GetType();
        if (stage is null || parameter is null || valueType is null)
            return Task.FromResult(new McpToolResponse(error ?? "The selected camera has no declared parameter with that stage/key and value type.", isError: true));
        object? converted;
        if (valueType == typeof(Vector2) || valueType == typeof(Vector3) || valueType == typeof(Vector4))
        {
            try
            {
                JsonElement element = JsonSerializer.SerializeToElement(value, s_postProcessValueOptions);
                if (element.ValueKind != JsonValueKind.Object)
                    return Task.FromResult(new McpToolResponse("Vector values require an object with x/y/z/w components.", isError: true));
                string[] fields = valueType == typeof(Vector2) ? ["x", "y"] : valueType == typeof(Vector3) ? ["x", "y", "z"] : ["x", "y", "z", "w"];
                foreach (string field in fields)
                {
                    if (!element.TryGetProperty(field, out JsonElement component) || component.ValueKind != JsonValueKind.Number || !component.TryGetDouble(out double number) ||
                        !double.IsFinite(number) || Math.Abs(number) > float.MaxValue ||
                        parameter.Min is float min && number < min || parameter.Max is float max && number > max)
                        return Task.FromResult(new McpToolResponse("Vector values require finite x/y/z/w components within the declared range.", isError: true));
                }
                converted = element.Deserialize(valueType, s_postProcessValueOptions);
            }
            catch (JsonException ex)
            {
                return Task.FromResult(new McpToolResponse($"Invalid vector value: {ex.Message}", isError: true));
            }
        }
        else if (!McpToolRegistry.TryConvertValue(value, valueType, out converted, out error))
            return Task.FromResult(new McpToolResponse(error ?? "Parameter conversion failed.", isError: true));
        if (converted is float or double or int)
        {
            double numeric = Convert.ToDouble(converted);
            if (!double.IsFinite(numeric) || parameter.Min is float min && numeric < min || parameter.Max is float max && numeric > max ||
                parameter.EnumOptions.Count > 0 && !parameter.EnumOptions.Any(option => option.Value == numeric))
                return Task.FromResult(new McpToolResponse("Value is outside the parameter's declared range or enum options.", isError: true));
        }

        stage.SetValue(parameter.Name, converted);
        return Task.FromResult(new McpToolResponse("Updated camera post-process parameter.", new
        {
            stage = stage.StageKey,
            parameter = parameter.Name,
            value = SerializePostProcessValue(stage.GetValue(parameter.Name)),
        }));
    }
}
