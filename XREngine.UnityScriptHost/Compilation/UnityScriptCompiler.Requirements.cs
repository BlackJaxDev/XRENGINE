using XREngine.UnityScriptHost.Admission;

namespace XREngine.UnityScriptHost.Compilation;

public sealed partial class UnityScriptCompiler
{
    /// <summary>Lists bounded inventory evidence for a missing reference without inspecting package binaries.</summary>
    private static string CreateMissingReferenceMessage(string required, UnitySourceInventory inventory)
    {
        const int maximumCandidates = 3;
        string assemblyName = ReferenceName(required);
        string fileName = assemblyName + ".dll";
        var candidates = new List<string>(maximumCandidates);
        int omitted = 0;

        foreach (UnityAssemblyDefinition definition in inventory.AssemblyDefinitions
            .Where(definition => string.Equals(definition.Name, assemblyName, StringComparison.Ordinal))
            .OrderBy(static definition => definition.Path, StringComparer.Ordinal))
            AddCandidate($"Imported asmdef candidate: '{definition.Path}' (declares '{definition.Name}').");

        foreach (UnitySourceEntry entry in inventory.Entries
            .Where(entry => entry.Kind == "Executable" &&
                string.Equals(FileName(entry.Path), fileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static entry => entry.Path, StringComparer.Ordinal))
            AddCandidate($"Inventoried filename candidate: '{entry.Path}' (assembly identity not verified).");

        string evidence = candidates.Count == 0
            ? " No matching asmdef or binary filename was found in the source inventory."
            : " " + string.Join(" ", candidates);
        if (omitted > 0)
            evidence += $" {omitted} more candidate file(s) omitted.";
        return $"Required reference '{required}' has no approved metadata assembly.{evidence} " +
            "Package binaries are not admitted implicitly. Select an exact inventoried managed DLL with the Imported metadata role, or supply complete matching source and its dependencies. Metadata capture does not grant execution.";

        void AddCandidate(string candidate)
        {
            if (candidates.Count < maximumCandidates)
                candidates.Add(candidate);
            else
                omitted++;
        }
    }

    private static string FileName(string path)
        => path[(path.LastIndexOf('/') + 1)..];
}
