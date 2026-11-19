namespace UnityEngine;

/// <summary>Marks a component type that must have at most one instance per game object.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class DisallowMultipleComponent : Attribute
{
}
