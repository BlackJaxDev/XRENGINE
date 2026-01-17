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
        /// <summary>
        /// Sets a scene node's transform properties (translation, rotation, and/or scale).
        /// Only the specified properties are modified; others retain their current values.
        /// </summary>
        /// <param name="context">The MCP tool execution context.</param>
        /// <param name="nodeId">The GUID of the scene node to update.</param>
        /// <param name="translationX">X component of translation.</param>
        /// <param name="translationY">Y component of translation.</param>
        /// <param name="translationZ">Z component of translation.</param>
        /// <param name="pitch">Rotation pitch in degrees (rotation around X axis).</param>
        /// <param name="yaw">Rotation yaw in degrees (rotation around Y axis).</param>
        /// <param name="roll">Rotation roll in degrees (rotation around Z axis).</param>
        /// <param name="scaleX">X component of scale.</param>
        /// <param name="scaleY">Y component of scale.</param>
        /// <param name="scaleZ">Z component of scale.</param>
        /// <param name="space">Transform space: "local" (default) or "world".</param>
        /// <returns>A confirmation message indicating the transform was updated.</returns>
        [XRMcp]
        [McpName("set_transform")]
        [Description("Set a scene node transform (translation, rotation, scale).")]
        public static Task<McpToolResponse> SetTransformAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID to update.")] string nodeId,
            [McpName("translation_x"), Description("Local/world translation X.")] float? translationX = null,
            [McpName("translation_y"), Description("Local/world translation Y.")] float? translationY = null,
            [McpName("translation_z"), Description("Local/world translation Z.")] float? translationZ = null,
            [McpName("pitch"), Description("Rotation pitch in degrees.")] float? pitch = null,
            [McpName("yaw"), Description("Rotation yaw in degrees.")] float? yaw = null,
            [McpName("roll"), Description("Rotation roll in degrees.")] float? roll = null,
            [McpName("scale_x"), Description("Local scale X.")] float? scaleX = null,
            [McpName("scale_y"), Description("Local scale Y.")] float? scaleY = null,
            [McpName("scale_z"), Description("Local scale Z.")] float? scaleZ = null,
            [McpName("space"), Description("Transform space: local or world.")] string space = "local")
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

        /// <summary>
        /// Applies an incremental rotation to a scene node's transform.
        /// The rotation is applied in local space, combining with the existing rotation.
        /// </summary>
        /// <param name="context">The MCP tool execution context.</param>
        /// <param name="nodeId">The GUID of the scene node to rotate.</param>
        /// <param name="pitch">Pitch rotation in degrees (rotation around X axis).</param>
        /// <param name="yaw">Yaw rotation in degrees (rotation around Y axis).</param>
        /// <param name="roll">Roll rotation in degrees (rotation around Z axis).</param>
        /// <returns>
        /// A response containing the applied rotation values (pitch, yaw, roll).
        /// </returns>
        [XRMcp]
        [McpName("rotate_transform")]
        [Description("Apply a local rotation to a scene node's transform (degrees).")]
        public static Task<McpToolResponse> RotateTransformAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID to rotate.")] string nodeId,
            [McpName("pitch"), Description("Pitch in degrees.")] float pitch,
            [McpName("yaw"), Description("Yaw in degrees.")] float yaw,
            [McpName("roll"), Description("Roll in degrees.")] float roll)
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
