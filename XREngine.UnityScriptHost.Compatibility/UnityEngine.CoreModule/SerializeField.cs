namespace UnityEngine;

/// <summary>Marks a field for Unity serialization metadata.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class SerializeField : Attribute
{
}
