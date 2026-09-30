namespace XREngine.Input;

public readonly record struct RuntimeOpenVrActionDescriptor(
    Enum Name,
    Enum Category,
    RuntimeOpenVrActionRequirement Requirement,
    RuntimeOpenVrActionType Type,
    Dictionary<string, string>? LocalizedNames)
{
    public string Path => $"/actions/{Category}/{(Type == RuntimeOpenVrActionType.Vibration ? "out" : "in")}/{Name}";
}
