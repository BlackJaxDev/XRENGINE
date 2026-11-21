namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    /// <summary>The override tag suffix that marks an animated source property.</summary>
    private const string AnimatedPropertyTagSuffix = "Animated";

    /// <summary>The override tag that stores the explicit rename suffix of a locked material.</summary>
    private const string LockedRenameSuffixTag = "thry_rename_suffix";

    /// <summary>
    /// Copies the values of animated renamed properties onto their original property names, so the
    /// conversion reads the values that the locked source shader reads.
    /// </summary>
    /// <remarks>
    /// When a source material is locked, an animated property with the tag mode "2" is renamed to
    /// <c>name_suffix</c>, and the locked shader declares and reads only the renamed property. The material
    /// keeps both entries, and the original entry can hold an old value. For example, a shoe material can
    /// keep <c>_UDIMDiscardRow0_1 = 1</c> while its locked shader reads <c>_UDIMDiscardRow0_1_Vans = 0</c>.
    /// The returned document is a conversion copy; the source document does not change.
    /// </remarks>
    /// <param name="document">The parsed material of a locked source shader.</param>
    /// <returns>A conversion copy with the renamed values applied, or <paramref name="document"/> when no renamed property exists.</returns>
    private static SerializedMaterialDocument ApplyLockedRenamedPropertyValues(SerializedMaterialDocument document)
    {
        string? suffix = ResolveLockedRenameSuffix(document);
        if (string.IsNullOrEmpty(suffix))
            return document;

        SerializedMaterialDocument? copy = null;
        foreach ((string tag, string mode) in document.OverrideTags)
        {
            if (!IsRenamedAnimatedTag(tag, mode, out string property))
                continue;

            string renamed = property + "_" + suffix;
            if (!document.HasAnyProperty(renamed))
                continue;

            copy ??= document.CreateConversionCopy();
            CopyRenamedValue(copy.Floats, renamed, property);
            CopyRenamedValue(copy.Ints, renamed, property);
            CopyRenamedValue(copy.Vectors, renamed, property);
            CopyRenamedValue(copy.Strings, renamed, property);
            CopyRenamedValue(copy.Textures, renamed, property);
        }

        return copy ?? document;
    }

    /// <summary>
    /// Finds the rename suffix of a locked material. The explicit suffix tag comes first. Otherwise the
    /// suffix is the property name ending that follows the most renamed animated properties.
    /// </summary>
    /// <remarks>
    /// The suffix comes from the material name, but the locking tool encodes some characters (for example,
    /// "." becomes "2E"). The serialized property names show the exact suffix, so they are the source of truth.
    /// </remarks>
    private static string? ResolveLockedRenameSuffix(SerializedMaterialDocument document)
    {
        if (document.OverrideTags.TryGetValue(LockedRenameSuffixTag, out string? configured) &&
            !string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        IReadOnlySet<string> names = document.GetPropertyNames();
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach ((string tag, string mode) in document.OverrideTags)
        {
            if (!IsRenamedAnimatedTag(tag, mode, out string property))
                continue;

            string prefix = property + "_";
            foreach (string name in names)
            {
                if (name.Length <= prefix.Length || !name.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                string candidate = name[prefix.Length..];
                counts[candidate] = counts.TryGetValue(candidate, out int count) ? count + 1 : 1;
            }
        }

        string? best = null;
        int bestCount = 0;
        foreach ((string candidate, int count) in counts)
        {
            if (count > bestCount ||
                count == bestCount && best is not null && string.CompareOrdinal(candidate, best) < 0)
            {
                best = candidate;
                bestCount = count;
            }
        }

        return best;
    }

    /// <summary>True when <paramref name="tag"/> marks an animated property that a locked shader renames.</summary>
    private static bool IsRenamedAnimatedTag(string tag, string mode, out string property)
    {
        property = string.Empty;
        if (!string.Equals(mode, "2", StringComparison.Ordinal) ||
            tag.Length <= AnimatedPropertyTagSuffix.Length ||
            !tag.EndsWith(AnimatedPropertyTagSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        property = tag[..^AnimatedPropertyTagSuffix.Length];
        return true;
    }

    private static void CopyRenamedValue<T>(Dictionary<string, T> values, string renamed, string property)
    {
        if (values.TryGetValue(renamed, out T? value))
            values[property] = value;
    }
}
