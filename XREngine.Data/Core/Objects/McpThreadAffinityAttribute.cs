using System;

namespace XREngine.Data.Core
{
    /// <summary>
    /// Declares which engine thread an MCP tool method should execute on.
    /// When absent, the global <c>McpDispatchMode</c> editor preference determines the default.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class McpThreadAffinityAttribute(McpThreadAffinity affinity) : Attribute
    {
        /// <summary>
        /// The thread affinity for this tool.
        /// </summary>
        public McpThreadAffinity Affinity { get; } = affinity;
    }
}
