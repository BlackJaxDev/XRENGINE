using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Scene.Components.Editing;
using XREngine.Scene.Transforms;
using XREngine.Components.Scene.Transforms;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    /// <summary>Reads the hidden editor gizmo's actual transform and reference camera.</summary>
    [XRMcp(Name = "get_transform_tool_state", Permission = McpPermissionLevel.ReadOnly)]
    [McpThreadAffinity(McpThreadAffinity.Main)]
    [Description("Inspect the active transform gizmo's target, display matrices, scale and reference camera without changing selection.")]
    public static Task<McpToolResponse> GetTransformToolStateAsync(McpToolContext context)
    {
        if (!TransformTool3D.GetActiveInstance(out var tool) || tool is null || tool.World != context.World)
            return Task.FromResult(new McpToolResponse("No transform tool is active in this world.", new { active = false }));

        var billboard = tool.Transform.GetChild(0) as BillboardTransform;
        return Task.FromResult(new McpToolResponse("Read transform tool state.", new
        {
            active = true,
            nodeId = tool.SceneNode.ID,
            componentId = tool.ID,
            targetNodeId = tool.TargetSocket?.SceneNode?.ID,
            mode = TransformTool3D.TransformMode.ToString(),
            toolScale = tool.ToolScale,
            root = DescribeToolTransform(tool.Transform),
            billboard = DescribeToolTransform(billboard),
            referenceCamera = DescribeToolTransform(billboard?.ReferenceCamera?.Transform),
        }));
    }

    private static object? DescribeToolTransform(TransformBase? transform)
    {
        if (transform is null)
            return null;
        Matrix4x4 world = transform.WorldMatrix;
        Matrix4x4.Decompose(world, out var scale, out _, out var position);
        Matrix4x4 render = transform.RenderMatrix;
        return new
        {
            position = new { x = position.X, y = position.Y, z = position.Z },
            scale = new { x = scale.X, y = scale.Y, z = scale.Z },
            renderMatrix = new[] { render.M11, render.M12, render.M13, render.M14, render.M21, render.M22, render.M23, render.M24,
                render.M31, render.M32, render.M33, render.M34, render.M41, render.M42, render.M43, render.M44 },
        };
    }
}
