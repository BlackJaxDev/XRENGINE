namespace XREngine.Core.Reflection.Attributes
{
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class InspectorPathAttribute(InspectorPathKind pathKind, InspectorPathFormat format = InspectorPathFormat.Both) : Attribute
    {
        public InspectorPathKind PathKind { get; } = pathKind;
        public InspectorPathFormat Format { get; } = format;
        public InspectorPathDialogMode DialogMode { get; init; } = InspectorPathDialogMode.Open;
        public string? Filter { get; init; }
        public string? Title { get; init; }
    }

    public enum InspectorPathKind
    {
        File,
        Folder,
    }

    public enum InspectorPathFormat
    {
        Absolute,
        Relative,
        Both,
    }

    public enum InspectorPathDialogMode
    {
        Open,
        Save,
    }
}