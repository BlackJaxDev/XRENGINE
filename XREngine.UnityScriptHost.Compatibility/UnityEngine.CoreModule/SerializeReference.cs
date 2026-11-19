namespace UnityEngine;

/// <summary>Marks a field for managed-reference serialization metadata.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class SerializeReference : Attribute
{
}
