using System;
using System.ComponentModel;
using System.Numerics;
using System.Threading.Tasks;
using XREngine.Data.Core;
using XREngine.Data.Transforms.Rotations;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        [XRMCP]
        [DisplayName("set_transform")]
        [Description("Set a scene node transform (translation, rotation, scale).")]
        public static Task<McpToolResponse> SetTransformAsync(
            McpToolContext context,
            [DisplayName("node_id"), Description("Scene node ID to update.")] string nodeId,
            [DisplayName("translation_x"), Description("Local/world translation X.")] float? translationX = null,
            [DisplayName("translation_y"), Description("Local/world translation Y.")] float? translationY = null,
            [DisplayName("translation_z"), Description("Local/world translation Z.")] float? translationZ = null,
            [DisplayName("pitch"), Description("Rotation pitch in degrees.")] float? pitch = null,
            [DisplayName("yaw"), Description("Rotation yaw in degrees.")] float? yaw = null,
            [DisplayName("roll"), Description("Rotation roll in degrees.")] float? roll = null,
            [DisplayName("scale_x"), Description("Local scale X.")] float? scaleX = null,
            [DisplayName("scale_y"), Description("Local scale Y.")] float? scaleY = null,
            [DisplayName("scale_z"), Description("Local scale Z.")] float? scaleZ = null,
            [DisplayName("space"), Description("Transform space: local or world.")] string space = "local")
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error))
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            if (node!.Transform is not Transform transform)
                return Task.FromResult(new McpToolResponse($"Scene node '{nodeId}' does not use a standard Transform.", isError: true));

            var currentRotator = Rotator.FromQuaternion(transform.Rotation);

            var translation = new Vector3(
                translationX ?? transform.Translation.X,
                translationY ?? transform.Translation.Y,
                translationZ ?? transform.Translation.Z);

            var scale = new Vector3(
                scaleX ?? transform.Scale.X,
                scaleY ?? transform.Scale.Y,
                scaleZ ?? transform.Scale.Z);

            var rotator = new Rotator(
                pitch ?? currentRotator.Pitch,
                yaw ?? currentRotator.Yaw,
                roll ?? currentRotator.Roll);

            bool useWorld = string.Equals(space, "world", StringComparison.OrdinalIgnoreCase);

            if (translationX.HasValue || translationY.HasValue || translationZ.HasValue)
            {
                if (useWorld)
                    transform.SetWorldTranslation(translation);
                else
                    transform.Translation = translation;
            }

            if (pitch.HasValue || yaw.HasValue || roll.HasValue)
            {
                var rotation = rotator.ToQuaternion();
                if (useWorld)
                    transform.SetWorldRotation(rotation);
                else
                    transform.Rotation = rotation;
            }

            if (scaleX.HasValue || scaleY.HasValue || scaleZ.HasValue)
                transform.Scale = scale;

            return Task.FromResult(new McpToolResponse($"Updated transform for '{nodeId}'."));
        }

        [XRMCP]
        [DisplayName("rotate_transform")]
        [Description("Apply a local rotation to a scene node's transform (degrees).")]
        public static Task<McpToolResponse> RotateTransformAsync(
            McpToolContext context,
            [DisplayName("node_id"), Description("Scene node ID to rotate.")] string nodeId,
            [DisplayName("pitch"), Description("Pitch in degrees.")] float pitch,
            [DisplayName("yaw"), Description("Yaw in degrees.")] float yaw,
            [DisplayName("roll"), Description("Roll in degrees.")] float roll)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error))
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            if (node!.Transform is not Transform transform)
                return Task.FromResult(new McpToolResponse($"Scene node '{nodeId}' does not use a standard Transform.", isError: true));

            var rotator = new Rotator(pitch, yaw, roll);
            transform.ApplyRotation(rotator.ToQuaternion());

            return Task.FromResult(new McpToolResponse($"Applied rotation to '{nodeId}'.", new
            {
                rotation = new { pitch, yaw, roll }
            }));
        }
    }
}
