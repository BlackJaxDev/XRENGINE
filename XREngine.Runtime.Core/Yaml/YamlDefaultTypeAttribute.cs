using System;

namespace XREngine;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class YamlDefaultTypeAttribute(Type defaultType) : Attribute
{
    public Type DefaultType { get; } = defaultType ?? throw new ArgumentNullException(nameof(defaultType));
}