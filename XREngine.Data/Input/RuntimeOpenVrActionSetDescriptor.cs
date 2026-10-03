namespace XREngine.Input;

public readonly record struct RuntimeOpenVrActionSetDescriptor(
    Enum Name,
    RuntimeOpenVrActionSetType Type,
    Dictionary<string, string>? LocalizedNames)
{
    public string Path => $"/actions/{Name}";
}
