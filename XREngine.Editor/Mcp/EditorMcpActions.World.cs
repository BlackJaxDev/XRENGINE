using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using XREngine.Data.Core;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        /// <summary>
        /// Lists all active world instances and their associated scenes.
        /// </summary>
        /// <param name="context">The MCP tool execution context.</param>
        /// <returns>
        /// A response containing an array of worlds, each with:
        /// <list type="bullet">
        /// <item><description><c>id</c> - The world's unique GUID</description></item>
        /// <item><description><c>name</c> - The world's display name</description></item>
        /// <item><description><c>scenes</c> - Array of scenes with id and name</description></item>
        /// <item><description><c>playState</c> - Current play state (e.g., "Playing", "Stopped")</description></item>
        /// </list>
        /// </returns>
        [XRMcp]
        [McpName("list_worlds")]
        [Description("List active world instances and their scenes.")]
        public static Task<McpToolResponse> ListWorldsAsync(
            McpToolContext context)
        {
            var worlds = Engine.WorldInstances.Select(instance => new
            {
                id = instance.TargetWorld?.ID,
                name = instance.TargetWorld?.Name ?? "<unnamed>",
                scenes = instance.TargetWorld?.Scenes.Select(scene => new { id = scene.ID, name = scene.Name ?? "<unnamed>" }).ToArray()
                    ?? Array.Empty<object>(),
                playState = instance.PlayState.ToString()
            }).ToArray();

            return Task.FromResult(new McpToolResponse("Listed active world instances.", new { worlds }));
        }
    }
}
