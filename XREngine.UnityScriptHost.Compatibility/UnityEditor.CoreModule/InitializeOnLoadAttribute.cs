namespace UnityEditor;

/// <summary>Declares editor initialization metadata. The host scheduler must interpret it.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class InitializeOnLoadAttribute : Attribute;
