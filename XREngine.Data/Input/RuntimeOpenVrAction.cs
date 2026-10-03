namespace XREngine.Input;

public sealed class RuntimeOpenVrAction<TCategory, TAction>
    where TCategory : struct, Enum
    where TAction : struct, Enum
{
    public TAction Name { get; set; }
    public TCategory Category { get; set; }
    public RuntimeOpenVrActionRequirement Requirement { get; set; } = RuntimeOpenVrActionRequirement.Suggested;
    public RuntimeOpenVrActionType Type { get; set; }
    public Dictionary<string, string>? LocalizedNames { get; set; }

    internal RuntimeOpenVrActionDescriptor ToDescriptor()
        => new(Name, Category, Requirement, Type, LocalizedNames);
}
