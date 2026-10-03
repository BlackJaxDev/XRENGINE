namespace XREngine.Input;

public interface IRuntimeOpenVrActionManifest
{
    IEnumerable<RuntimeOpenVrActionSetDescriptor> EnumerateActionSets();
    IEnumerable<RuntimeOpenVrActionDescriptor> EnumerateActions();
    bool HasActionSets { get; }
    bool HasActions { get; }
    IReadOnlyList<RuntimeOpenVrDefaultBinding>? DefaultBindings { get; }
    uint? Version { get; }
    uint? RequiredVersion { get; }
    bool? SupportsDominantHandSetting { get; }
}
