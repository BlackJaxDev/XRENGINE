using XREngine.Data.Core;

namespace XREngine.Editor.Mcp
{
    /// <summary>
    /// Contains all MCP (Model Context Protocol) tool implementations for the XREngine Editor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This partial class is split across multiple files, organized by functionality:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>EditorMcpActions.Scene.cs</c> - Scene node operations (create, delete, list, reparent)</description></item>
    /// <item><description><c>EditorMcpActions.Components.cs</c> - Component operations (add, list, set properties)</description></item>
    /// <item><description><c>EditorMcpActions.Transform.cs</c> - Transform operations (set, rotate)</description></item>
    /// <item><description><c>EditorMcpActions.World.cs</c> - World operations (list worlds/scenes)</description></item>
    /// <item><description><c>EditorMcpActions.Viewport.cs</c> - Viewport operations (screenshot capture)</description></item>
    /// <item><description><c>EditorMcpActions.Introspection.cs</c> - Engine state, asset, and debugging introspection</description></item>
    /// <item><description><c>EditorMcpActions.TypeSystem.cs</c> - .NET type system navigation and reflection queries</description></item>
    /// <item><description><c>EditorMcpActions.Scripting.cs</c> - Code/script management, compilation, hot-reload, and scaffolding</description></item>
    /// <item><description><c>EditorMcpActions.GameAssets.cs</c> - Game project asset management (file-system and engine-aware operations)</description></item>
    /// <item><description><c>EditorMcpActions.Helpers.cs</c> - Internal helper methods</description></item>
    /// </list>
    /// <para>
    /// Each public method decorated with <c>[XRMcp]</c> is automatically registered as an MCP tool.
    /// </para>
    /// </remarks>
    public sealed partial class EditorMcpActions : XRBase
    {
    }
}
