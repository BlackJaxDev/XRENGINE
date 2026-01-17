using System;

namespace XREngine.Editor.Mcp
{
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
    public sealed class McpNameAttribute(string name) : Attribute
    {
        public string Name { get; } = name;
    }
}