using System;

namespace XREngine.Data.Core
{
    /// <summary>
    /// Declares the permission level required to execute an MCP tool.
    /// Applied alongside <see cref="XRMcpAttribute"/> on tool methods.
    /// When absent, the tool is classified automatically:
    /// tools whose name starts with get_/list_/find_/validate_ default to
    /// <see cref="McpPermissionLevel.ReadOnly"/>; all others default to
    /// <see cref="McpPermissionLevel.Mutate"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class McpPermissionAttribute : Attribute
    {
        public McpPermissionLevel Level { get; }

        /// <summary>
        /// Optional human-readable reason shown in the permission prompt
        /// explaining why this tool requires elevated permission.
        /// </summary>
        public string? Reason { get; set; }

        public McpPermissionAttribute(McpPermissionLevel level)
        {
            Level = level;
        }
    }
}
