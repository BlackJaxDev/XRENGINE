namespace XREngine.Input;

public sealed class RuntimeOpenVrActionManifest<TCategory, TAction> : IRuntimeOpenVrActionManifest
    where TCategory : struct, Enum
    where TAction : struct, Enum
{
    public List<RuntimeOpenVrActionSet<TCategory>>? ActionSets { get; set; }
    public List<RuntimeOpenVrAction<TCategory, TAction>>? Actions { get; set; }
    public List<RuntimeOpenVrDefaultBinding>? DefaultBindings { get; set; }
    public uint? Version { get; set; }
    public uint? RequiredVersion { get; set; }
    public bool? SupportsDominantHandSetting { get; set; }

    public bool HasActionSets => ActionSets is not null;
    public bool HasActions => Actions is not null;

    IReadOnlyList<RuntimeOpenVrDefaultBinding>? IRuntimeOpenVrActionManifest.DefaultBindings => DefaultBindings;

    public IEnumerable<RuntimeOpenVrActionSetDescriptor> EnumerateActionSets()
    {
        if (ActionSets is null)
            yield break;
        foreach (RuntimeOpenVrActionSet<TCategory> set in ActionSets)
            yield return set.ToDescriptor();
    }

    public IEnumerable<RuntimeOpenVrActionDescriptor> EnumerateActions()
    {
        if (Actions is null)
            yield break;
        foreach (RuntimeOpenVrAction<TCategory, TAction> action in Actions)
            yield return action.ToDescriptor();
    }
}
