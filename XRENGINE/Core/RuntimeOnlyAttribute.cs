using System;

namespace XREngine;

/// <summary>
/// Marks a member as runtime-only so it will be excluded from cooked/snapshot reflection serialization.
/// Use this for transient handles (GPU resources, audio contexts, unmanaged pointers, etc.).
/// </summary>
[AttributeUsage(
	AttributeTargets.Class |
	AttributeTargets.Struct |
	AttributeTargets.Interface |
	AttributeTargets.Property |
	AttributeTargets.Field,
	Inherited = true,
	AllowMultiple = false)]
public sealed class RuntimeOnlyAttribute : Attribute
{
}
