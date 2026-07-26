namespace XREngine.Rendering;

/// <summary>
/// Computes the transitive dependency closure for an enabled uber feature set.
/// </summary>
public static class UberFeatureDependencyResolver
{
    public static List<string> Resolve(ShaderUiManifest manifest, IEnumerable<string> enabledFeatures)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(enabledFeatures);

        HashSet<string> resolved = new(enabledFeatures, StringComparer.Ordinal);
        Stack<string> pending = new(resolved);
        while (pending.TryPop(out string? featureId))
        {
            if (!manifest.FeatureLookup.TryGetValue(featureId, out ShaderUiFeature? feature))
                continue;

            foreach (string dependency in feature.Dependencies)
            {
                if (resolved.Add(dependency))
                    pending.Push(dependency);
            }
        }

        List<string> result = [.. resolved];
        result.Sort(StringComparer.Ordinal);
        return result;
    }
}
