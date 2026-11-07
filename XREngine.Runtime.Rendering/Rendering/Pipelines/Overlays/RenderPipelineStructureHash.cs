using System.Globalization;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Pipelines.Overlays;

/// <summary>
/// Computes the structure hash of a base pipeline: its anchors, command types, resource names, and stage keys. A
/// derived script stores the hash in its <c>// base-hash:</c> line, and a different hash on load starts a rebase report.
/// </summary>
public static class RenderPipelineStructureHash
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    /// <summary>Computes the hash from a command chain, a resource layout, and a schema.</summary>
    public static ulong Compute(ViewportRenderCommandContainer chain, RenderPipelineResourceLayout? layout, RenderPipelinePostProcessSchema? schema)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ulong hash = OffsetBasis;
        hash = MixContainer(hash, chain, []);

        if (layout is not null)
        {
            List<string> names = new(layout.OrderedSpecs.Count);
            for (int i = 0; i < layout.OrderedSpecs.Count; i++)
                names.Add(layout.OrderedSpecs[i].Name);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < names.Count; i++)
                hash = Mix(hash, string.Concat("r:", names[i].ToLowerInvariant()));
        }

        if (schema is not null)
        {
            List<string> keys = [.. schema.StagesByKey.Keys];
            keys.Sort(StringComparer.Ordinal);
            for (int i = 0; i < keys.Count; i++)
                hash = Mix(hash, string.Concat("s:", keys[i]));
        }

        return hash;
    }

    /// <summary>Formats <paramref name="hash"/> as the 16-digit hexadecimal text of the <c>// base-hash:</c> line.</summary>
    public static string Format(ulong hash)
        => hash.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>Parses the text of a <c>// base-hash:</c> line.</summary>
    public static bool TryParse(string? text, out ulong hash)
        => ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash);

    /// <summary>Mixes <paramref name="text"/> into <paramref name="hash"/> with FNV-1a.</summary>
    public static ulong Mix(ulong hash, string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            hash ^= text[i];
            hash *= Prime;
        }

        hash ^= 0xFF;
        hash *= Prime;
        return hash;
    }

    private static ulong MixContainer(ulong hash, ViewportRenderCommandContainer container, List<RenderCommandChildContainer> children)
    {
        for (int i = 0; i < container.Count; i++)
        {
            ViewportRenderCommand command = container[i];
            hash = Mix(hash, command.GetType().Name);
            if (command.AnchorId is { } anchor)
                hash = Mix(hash, string.Concat("@", anchor));

            children.Clear();
            command.CollectChildContainers(children);
            RenderCommandChildContainer[] snapshot = [.. children];
            for (int c = 0; c < snapshot.Length; c++)
            {
                hash = Mix(hash, string.Concat(">", snapshot[c].Name));
                hash = MixContainer(hash, snapshot[c].Container, children);
                hash = Mix(hash, "<");
            }
        }

        return hash;
    }
}
