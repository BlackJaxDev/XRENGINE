namespace XREngine.Core.Reflection.Attributes;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class InspectorFooterLabelAttribute(string text) : Attribute
{
    public string Text { get; } = text;
}