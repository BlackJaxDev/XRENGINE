namespace UnityEngine.Scripting;

/// <summary>Marks source metadata for preservation without changing compilation or execution.</summary>
[AttributeUsage(
    AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum |
    AttributeTargets.Constructor | AttributeTargets.Method | AttributeTargets.Property |
    AttributeTargets.Field | AttributeTargets.Event | AttributeTargets.Interface | AttributeTargets.Delegate,
    Inherited = false)]
public class PreserveAttribute : Attribute
{
}
