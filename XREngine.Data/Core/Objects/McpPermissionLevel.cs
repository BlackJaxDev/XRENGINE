using System;

namespace XREngine.Data.Core
{
    /// <summary>
    /// Classifies the risk level of an MCP tool for the permission-grant UI.
    /// Tools at or below the user's configured auto-approve threshold execute
    /// without prompting; tools above the threshold require explicit approval.
    /// </summary>
    public enum McpPermissionLevel
    {
        /// <summary>
        /// Pure read / query / list operations. No side effects.
        /// Examples: list_scene_nodes, get_type_info, get_engine_state.
        /// </summary>
        ReadOnly = 0,

        /// <summary>
        /// Mutating operations that modify scene state but are undoable and non-destructive.
        /// Examples: set_transform, add_component, rename_scene_node.
        /// </summary>
        Mutate = 1,

        /// <summary>
        /// Destructive operations that delete data or cannot be trivially undone.
        /// Examples: delete_scene_node, delete_game_asset, delete_game_script.
        /// </summary>
        Destructive = 2,

        /// <summary>
        /// Arbitrary code execution, method invocation, or unrestricted file I/O.
        /// Examples: invoke_method, compile_game_scripts, write_game_script.
        /// </summary>
        Arbitrary = 3,
    }
}
