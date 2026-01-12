using System;

namespace XREngine.Core.Attributes
{
    /// <summary>
    /// Declares that this type can replace one or more legacy type names during deserialization.
    /// This is used for backward compatibility when types are renamed, moved, or made abstract.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
    public sealed class XRTypeRedirectAttribute(params string[] legacyTypeNames) : Attribute
    {
        /// <summary>
        /// Legacy type names to redirect. Prefer <c>FullName</c> (e.g. <c>XREngine.GameMode</c>).
        /// </summary>
        public string[] LegacyTypeNames { get; } = legacyTypeNames ?? [];
    }
}
