namespace XREngine.Data.Core
{
    /// <summary>
    /// Configures the auto-approve threshold for MCP tool permission requests.
    /// Tools at or below this level execute without prompting the user;
    /// tools above the threshold show a modal permission dialog.
    /// </summary>
    public enum McpPermissionPolicy
    {
        /// <summary>Every tool call requires explicit user approval.</summary>
        AlwaysAsk = 0,

        /// <summary>ReadOnly tools auto-approve; Mutate and above prompt.</summary>
        AllowReadOnly = 1,

        /// <summary>ReadOnly and Mutate auto-approve; Destructive and above prompt.</summary>
        AllowMutate = 2,

        /// <summary>ReadOnly, Mutate, and Destructive auto-approve; only Arbitrary prompts.</summary>
        AllowDestructive = 3,

        /// <summary>All tools auto-approve (no prompts). Use with caution.</summary>
        AllowAll = 4,
    }
}
