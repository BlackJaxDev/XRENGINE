using System.Globalization;
using System.Text;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.Pipelines.Overlays;

/// <summary>
/// Finds patch targets in a command tree. A target is an anchor id, a structural path, or <see cref="RootTarget"/>.
/// A structural path names each level as <c>Type[Label]#n</c>, where <c>n</c> counts the earlier commands of the same
/// type and label in that container, and <c>&gt;Child</c> selects a child container. Example:
/// <c>path:VPRC_IfElse[Bloom]#0&gt;True/VPRC_BloomPass#0</c>.
/// </summary>
public static class RenderCommandPaths
{
    /// <summary>The target that names the top container of the chain.</summary>
    public const string RootTarget = "root";

    /// <summary>The prefix of a structural path.</summary>
    public const string StructuralPrefix = "path:";

    private const int MaxAliasHops = 8;

    /// <summary>Returns true when <paramref name="target"/> is a structural path.</summary>
    public static bool IsStructuralPath(string? target)
        => target is not null && target.StartsWith(StructuralPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Finds the command that <paramref name="target"/> names. An anchor id that the base renamed resolves through
    /// <paramref name="aliases"/>, and <paramref name="viaAlias"/> is then true.
    /// </summary>
    public static bool TryResolve(
        ViewportRenderCommandContainer root,
        string target,
        IReadOnlyDictionary<string, string>? aliases,
        out ViewportRenderCommandContainer container,
        out int index,
        out bool viaAlias)
    {
        viaAlias = false;
        if (IsStructuralPath(target))
            return TryResolvePath(root, target[StructuralPrefix.Length..], out container, out index);

        if (TryFindAnchor(root, target, out container, out index))
            return true;

        string current = target;
        for (int hop = 0; hop < MaxAliasHops && aliases is not null && aliases.TryGetValue(current, out string? next); hop++)
        {
            current = next;
            if (TryFindAnchor(root, current, out container, out index))
            {
                viaAlias = true;
                return true;
            }
        }

        return false;
    }

    /// <summary>Finds the command whose <see cref="ViewportRenderCommand.AnchorId"/> is <paramref name="anchorId"/>.</summary>
    public static bool TryFindAnchor(ViewportRenderCommandContainer root, string anchorId, out ViewportRenderCommandContainer container, out int index)
    {
        List<RenderCommandChildContainer> children = [];
        return TryFindAnchor(root, anchorId, children, out container, out index);
    }

    /// <summary>Returns the target text for <paramref name="command"/>: its anchor id, or its structural path.</summary>
    public static string GetTarget(ViewportRenderCommandContainer root, ViewportRenderCommand command)
        => command.AnchorId ?? BuildPath(root, command) ?? string.Empty;

    /// <summary>Returns the structural path of <paramref name="command"/> inside <paramref name="root"/>, or null when the command is not in the tree.</summary>
    public static string? BuildPath(ViewportRenderCommandContainer root, ViewportRenderCommand command)
    {
        List<string> segments = [];
        if (!TryBuildPath(root, command, segments))
            return null;

        StringBuilder builder = new(StructuralPrefix);
        // The segments run from the command up to the root. Each parent segment ends with its child separator.
        for (int i = segments.Count - 1; i >= 0; i--)
            builder.Append(segments[i]);

        return builder.ToString();
    }

    /// <summary>Returns the label part of a path segment for <paramref name="command"/>, or null.</summary>
    public static string? GetLabel(ViewportRenderCommand command)
        => command switch
        {
            VPRC_IfElse branch => branch.Label,
            VPRC_Switch selector => selector.Label,
            _ => null,
        };

    private static bool TryFindAnchor(
        ViewportRenderCommandContainer container,
        string anchorId,
        List<RenderCommandChildContainer> children,
        out ViewportRenderCommandContainer found,
        out int index)
    {
        for (int i = 0; i < container.Count; i++)
        {
            if (string.Equals(container[i].AnchorId, anchorId, StringComparison.Ordinal))
            {
                found = container;
                index = i;
                return true;
            }
        }

        for (int i = 0; i < container.Count; i++)
        {
            children.Clear();
            container[i].CollectChildContainers(children);
            RenderCommandChildContainer[] snapshot = [.. children];
            for (int c = 0; c < snapshot.Length; c++)
            {
                if (TryFindAnchor(snapshot[c].Container, anchorId, children, out found, out index))
                    return true;
            }
        }

        found = null!;
        index = -1;
        return false;
    }

    private static bool TryBuildPath(ViewportRenderCommandContainer container, ViewportRenderCommand command, List<string> segments)
    {
        for (int i = 0; i < container.Count; i++)
        {
            if (ReferenceEquals(container[i], command))
            {
                segments.Add(FormatSegment(container, i));
                return true;
            }
        }

        List<RenderCommandChildContainer> children = [];
        for (int i = 0; i < container.Count; i++)
        {
            children.Clear();
            container[i].CollectChildContainers(children);
            for (int c = 0; c < children.Count; c++)
            {
                if (!TryBuildPath(children[c].Container, command, segments))
                    continue;

                segments.Add(string.Concat(FormatSegment(container, i), ">", children[c].Name, "/"));
                return true;
            }
        }

        return false;
    }

    private static string FormatSegment(ViewportRenderCommandContainer container, int index)
    {
        ViewportRenderCommand command = container[index];
        string typeName = command.GetType().Name;
        string? label = Normalize(GetLabel(command));
        int occurrence = 0;
        for (int i = 0; i < index; i++)
        {
            if (container[i].GetType() == command.GetType() && string.Equals(Normalize(GetLabel(container[i])), label, StringComparison.Ordinal))
                occurrence++;
        }

        string count = occurrence.ToString(CultureInfo.InvariantCulture);
        return label is null
            ? string.Concat(typeName, "#", count)
            : string.Concat(typeName, "[", label, "]#", count);
    }

    private static bool TryResolvePath(ViewportRenderCommandContainer root, string path, out ViewportRenderCommandContainer container, out int index)
    {
        container = root;
        index = -1;
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int p = 0; p < parts.Length; p++)
        {
            string part = parts[p];
            string? childName = null;
            int childSplit = part.LastIndexOf('>');
            if (childSplit >= 0)
            {
                childName = part[(childSplit + 1)..];
                part = part[..childSplit];
            }

            if (!TryParseSegment(part, out string typeName, out string? label, out int occurrence) ||
                !TryFindSegment(container, typeName, label, occurrence, out index))
            {
                return false;
            }

            if (childName is null)
                return p == parts.Length - 1;

            List<RenderCommandChildContainer> children = [];
            container[index].CollectChildContainers(children);
            ViewportRenderCommandContainer? next = null;
            for (int c = 0; c < children.Count; c++)
            {
                if (string.Equals(children[c].Name, childName, StringComparison.Ordinal))
                    next = children[c].Container;
            }

            if (next is null)
                return false;
            container = next;
            index = -1;
        }

        return false;
    }

    private static bool TryParseSegment(string segment, out string typeName, out string? label, out int occurrence)
    {
        typeName = string.Empty;
        label = null;
        occurrence = 0;
        int hash = segment.LastIndexOf('#');
        if (hash < 0 || !int.TryParse(segment.AsSpan(hash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out occurrence))
            return false;

        string head = segment[..hash];
        int open = head.IndexOf('[');
        if (open >= 0 && head.EndsWith(']'))
        {
            label = head[(open + 1)..^1];
            head = head[..open];
        }

        typeName = head;
        return typeName.Length > 0;
    }

    private static bool TryFindSegment(ViewportRenderCommandContainer container, string typeName, string? label, int occurrence, out int index)
    {
        int seen = 0;
        for (int i = 0; i < container.Count; i++)
        {
            ViewportRenderCommand command = container[i];
            if (!string.Equals(command.GetType().Name, typeName, StringComparison.Ordinal) ||
                !string.Equals(Normalize(GetLabel(command)), label, StringComparison.Ordinal))
            {
                continue;
            }

            if (seen++ == occurrence)
            {
                index = i;
                return true;
            }
        }

        index = -1;
        return false;
    }

    /// <summary>Returns the label with the path separators replaced, or null for an empty label.</summary>
    private static string? Normalize(string? label)
    {
        if (string.IsNullOrEmpty(label))
            return null;

        return label.Replace('/', '_').Replace('>', '_').Replace('[', '_').Replace(']', '_').Replace('#', '_');
    }
}
