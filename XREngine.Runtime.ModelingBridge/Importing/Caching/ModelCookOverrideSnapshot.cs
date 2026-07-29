using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Deterministic pre-lookup snapshot of project-authored submesh cook overrides.
/// </summary>
public sealed class ModelCookOverrideSnapshot
{
    private readonly ReadOnlyCollection<ModelCookOverrideEntry> _entries;
    private readonly byte[] _canonicalBytes;

    public ModelCookOverrideSnapshot(IEnumerable<ModelCookOverrideEntry>? entries = null)
    {
        ModelCookOverrideEntry[] ordered = (entries ?? [])
            .OrderBy(static entry => entry.EntityKey.Value, StringComparer.Ordinal)
            .ToArray();

        for (int index = 1; index < ordered.Length; index++)
        {
            if (string.Equals(
                ordered[index - 1].EntityKey.Value,
                ordered[index].EntityKey.Value,
                StringComparison.Ordinal))
                throw new ArgumentException(
                    $"Duplicate model cook override key '{ordered[index].EntityKey.Value}'.",
                    nameof(entries));
        }

        _entries = Array.AsReadOnly(ordered);
        _canonicalBytes = Serialize(ordered);
        byte[] digest = SHA256.HashData(_canonicalBytes);
        Hash = Convert.ToHexString(digest).ToLowerInvariant();
    }

    public static ModelCookOverrideSnapshot Empty { get; } = new();

    public IReadOnlyList<ModelCookOverrideEntry> Entries => _entries;
    public string Hash { get; }
    public ReadOnlyMemory<byte> CanonicalBytes => _canonicalBytes;

    private static byte[] Serialize(IReadOnlyList<ModelCookOverrideEntry> entries)
    {
        using ModelCacheCanonicalWriter writer = new();
        writer.WriteString(1, "xrengine.model-cook-overrides");
        writer.WriteUInt32(2, ModelBinaryCacheVersions.CookPolicy);
        writer.WriteInt32(3, entries.Count);

        for (int index = 0; index < entries.Count; index++)
        {
            ModelCookOverrideEntry entry = entries[index];
            using ModelCacheCanonicalWriter entryWriter = new();
            entryWriter.WriteString(1, entry.EntityKey.Value);
            entryWriter.WriteBoolean(2, entry.EntityKey.IsStable);
            entryWriter.WriteBytes(3, entry.CanonicalSettingsSpan);
            writer.WriteBytes((uint)(index + 10), entryWriter.ToArray());
        }

        return writer.ToArray();
    }
}
