namespace UnityEditor;

/// <summary>Declares an editor initialization method without invoking it.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class InitializeOnLoadMethodAttribute : Attribute;
