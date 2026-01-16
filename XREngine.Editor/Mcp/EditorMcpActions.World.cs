using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using XREngine.Data.Core;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        [XRMCP]
        [DisplayName("list_worlds")]
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
