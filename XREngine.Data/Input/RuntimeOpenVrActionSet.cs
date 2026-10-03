namespace XREngine.Input;

public sealed class RuntimeOpenVrActionSet<TCategory>
    where TCategory : struct, Enum
{
    public TCategory Name { get; set; }
    public RuntimeOpenVrActionSetType Type { get; set; }
    public Dictionary<string, string>? LocalizedNames { get; set; }

    internal RuntimeOpenVrActionSetDescriptor ToDescriptor()
        => new(Name, Type, LocalizedNames);
}
