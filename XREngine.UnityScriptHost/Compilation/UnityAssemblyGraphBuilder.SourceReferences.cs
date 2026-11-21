using System.Text.RegularExpressions;

namespace XREngine.UnityScriptHost.Compilation;

public sealed partial class UnityAssemblyGraphBuilder
{
    private Dictionary<string, string> ValidateSourceReferences(UnityCompilationProfile profile, Dictionary<string, NodeState> selected)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in profile.SourceReferenceAssemblies)
        {
            if (name is null || !Regex.IsMatch(name, @"\A[A-Za-z0-9_][A-Za-z0-9_. -]{0,199}\z", RegexOptions.CultureInvariant) ||
                name.EndsWith('.') || name.EndsWith(' ') || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                !names.TryAdd(name, name))
                throw new InvalidDataException("Source reference selections need unique bounded assembly names without a DLL suffix.");

            if (!selected.TryGetValue(name, out NodeState? provider) || provider.Definition is null || provider.Sources.Count == 0)
            {
                Add("UNITYHOST2020", name, provider?.DefinitionPath ?? string.Empty,
                    "The selected source reference provider is absent, excluded, or has no C# source. A matching imported assembly definition and source are required; no binary fallback is permitted.");
                continue;
            }
            if (profile.References.Any(reference => string.Equals(reference.AssemblyName, name, StringComparison.OrdinalIgnoreCase)))
                Add("UNITYHOST2020", name, provider.DefinitionPath,
                    "A source reference provider conflicts with an explicit metadata reference identity.");
        }
        return names;
    }

    private void AddPrecompiledReference(NodeState consumer, string reference, Dictionary<string, string> sourceReferences,
        Dictionary<string, NodeState> selected)
    {
        string requestedName = reference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? reference[..^4] : reference;
        if (!sourceReferences.TryGetValue(requestedName, out string? name))
        {
            consumer.External.Add(reference);
            return;
        }

        if (reference != name + ".dll")
        {
            Add("UNITYHOST2022", consumer.Name, consumer.DefinitionPath,
                $"Source reference '{reference}' must exactly match the selected assembly filename '{name}.dll', including case.");
            return;
        }

        // An explicit source selection must never fall back to a packaged DLL.
        if (!selected.TryGetValue(name, out NodeState? provider) || provider.Definition is null || provider.Sources.Count == 0)
            return;
        consumer.Dependencies.Add(name);
        Add("UNITYHOST2021", consumer.Name, provider.DefinitionPath,
            $"Required reference '{reference}' uses the explicitly selected imported source assembly '{name}'.", false);
    }
}
