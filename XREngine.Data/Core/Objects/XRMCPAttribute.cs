using System;

namespace XREngine.Data.Core
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class XRMcpAttribute : Attribute
    {
        public string? Name { get; set; }
        public McpPermissionLevel Permission { get; set; } = McpPermissionLevel.Unspecified;
        public string? PermissionReason { get; set; }

        public XRMcpAttribute()
        {
        }

        public XRMcpAttribute(string name)
        {
            Name = name;
        }
    }
}
