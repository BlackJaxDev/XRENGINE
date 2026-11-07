using System.Globalization;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Resources.Diagnostics;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Builds the rows of the Resources view from a <see cref="RenderPipelineResourceSnapshot"/> and keeps the list of
/// visible rows for the current search, filters, and grouping. Both lists rebuild only when an input changes.
/// </summary>
public sealed class RenderPipelineWorkbenchResourceTable
{
    private readonly List<RenderPipelineWorkbenchResourceRow> _rows = new(128);
    private readonly List<int> _visible = new(128);
    private RenderPipelineResourceSnapshot? _snapshot;
    private string _search = string.Empty;
    private string _searchLower = string.Empty;
    private ERenderPipelineResourceFilter _filter;
    private ERenderPipelineResourceGrouping _grouping;
    private bool _visibleDirty = true;

    /// <summary>Gets every row of the snapshot in layout order.</summary>
    public IReadOnlyList<RenderPipelineWorkbenchResourceRow> Rows => _rows;

    /// <summary>Gets the indices of the visible rows in display order.</summary>
    public IReadOnlyList<int> Visible => _visible;

    /// <summary>Gets the summary text with the byte totals of the snapshot.</summary>
    public string SummaryText { get; private set; } = string.Empty;

    /// <summary>Gets the problem summary text, such as the count of declared but unused resources.</summary>
    public string ProblemText { get; private set; } = string.Empty;

    /// <summary>Gets how many times the rows were rebuilt.</summary>
    public int RowBuildCount { get; private set; }

    /// <summary>Updates the rows and the visible list when an input changed.</summary>
    public void Update(RenderPipelineResourceSnapshot snapshot, string search, ERenderPipelineResourceFilter filter, ERenderPipelineResourceGrouping grouping)
    {
        if (!ReferenceEquals(snapshot, _snapshot))
        {
            _snapshot = snapshot;
            RebuildRows(snapshot);
            _visibleDirty = true;
        }

        if (!ReferenceEquals(search, _search))
        {
            _search = search;
            _searchLower = search.Trim().ToLowerInvariant();
            _visibleDirty = true;
        }

        if (filter != _filter || grouping != _grouping)
        {
            _filter = filter;
            _grouping = grouping;
            _visibleDirty = true;
        }

        if (_visibleDirty)
            RebuildVisible();
    }

    private void RebuildRows(RenderPipelineResourceSnapshot snapshot)
    {
        _rows.Clear();
        RowBuildCount++;
        for (int i = 0; i < snapshot.Entries.Count; i++)
        {
            RenderPipelineResourceEntry entry = snapshot.Entries[i];
            string kind = entry.Kind?.ToString() ?? "undeclared";
            string lifetime = entry.Spec?.Lifetime.ToString() ?? (entry.Has(ERenderPipelineResourceFlags.External) ? "External" : "-");
            _rows.Add(new RenderPipelineWorkbenchResourceRow
            {
                Entry = entry,
                KindText = kind,
                LifetimeText = lifetime,
                SizeText = FormatSize(entry),
                BytesText = entry.EstimatedBytes > 0 ? RenderPipelineWorkbenchText.FormatBytes((long)entry.EstimatedBytes) : "-",
                UsesText = string.Concat(
                    entry.Producers.Count.ToString(CultureInfo.InvariantCulture), " / ",
                    entry.Consumers.Count.ToString(CultureInfo.InvariantCulture)),
                SpanText = entry.FirstUseOrder < 0
                    ? "-"
                    : string.Concat(
                        entry.FirstUseOrder.ToString(CultureInfo.InvariantCulture), "-",
                        entry.LastUseOrder.ToString(CultureInfo.InvariantCulture)),
                FlagsText = FormatFlags(entry),
                SearchText = string.Concat(entry.Name, " ", kind, " ", lifetime, " ", entry.FormatText, " ", entry.Spec?.DebugLabel).ToLowerInvariant(),
            });
        }

        SummaryText = string.Concat(
            "Total ", RenderPipelineWorkbenchText.FormatBytes((long)snapshot.TotalBytes),
            "   Persistent ", RenderPipelineWorkbenchText.FormatBytes((long)snapshot.PersistentBytes),
            "   Transient ", RenderPipelineWorkbenchText.FormatBytes((long)snapshot.TransientBytes),
            "   History ", RenderPipelineWorkbenchText.FormatBytes((long)snapshot.HistoryBytes),
            "   Alias candidates ", RenderPipelineWorkbenchText.FormatBytes((long)snapshot.AliasCandidateBytes),
            "   External ", snapshot.ExternalCount.ToString(CultureInfo.InvariantCulture));
        ProblemText = snapshot.DeclaredUnusedCount == 0 && snapshot.UsedUndeclaredCount == 0
            ? string.Empty
            : string.Concat(
                snapshot.DeclaredUnusedCount.ToString(CultureInfo.InvariantCulture), " declared but unused, ",
                snapshot.UsedUndeclaredCount.ToString(CultureInfo.InvariantCulture), " used but undeclared");
    }

    private void RebuildVisible()
    {
        _visibleDirty = false;
        _visible.Clear();
        for (int i = 0; i < _rows.Count; i++)
        {
            RenderPipelineWorkbenchResourceRow row = _rows[i];
            row.GroupText = _grouping switch
            {
                ERenderPipelineResourceGrouping.Kind => row.KindText,
                ERenderPipelineResourceGrouping.Lifetime => row.LifetimeText,
                _ => string.Empty,
            };

            if (_searchLower.Length > 0 && !row.SearchText.Contains(_searchLower, StringComparison.Ordinal))
                continue;
            if (!MatchesFilter(row.Entry, _filter))
                continue;

            _visible.Add(i);
        }

        if (_grouping != ERenderPipelineResourceGrouping.None)
        {
            // A stable sort keeps the layout order inside each group.
            _visible.Sort((a, b) =>
            {
                int group = string.CompareOrdinal(_rows[a].GroupText, _rows[b].GroupText);
                return group != 0 ? group : a.CompareTo(b);
            });
        }
    }

    /// <summary>Returns true when <paramref name="entry"/> matches every filter in <paramref name="filter"/>.</summary>
    public static bool MatchesFilter(RenderPipelineResourceEntry entry, ERenderPipelineResourceFilter filter)
    {
        if ((filter & ERenderPipelineResourceFilter.DeclaredButUnused) != 0 && !entry.IsDeclaredButUnused)
            return false;
        if ((filter & ERenderPipelineResourceFilter.UsedButUndeclared) != 0 && !entry.IsUsedButUndeclared)
            return false;
        if ((filter & ERenderPipelineResourceFilter.History) != 0 && !entry.Has(ERenderPipelineResourceFlags.History))
            return false;
        if ((filter & ERenderPipelineResourceFilter.External) != 0 && !entry.Has(ERenderPipelineResourceFlags.External))
            return false;
        if ((filter & ERenderPipelineResourceFilter.Aliased) != 0 && !entry.Has(ERenderPipelineResourceFlags.AliasCandidate))
            return false;
        return true;
    }

    private static string FormatSize(RenderPipelineResourceEntry entry)
    {
        if (entry.Width == 0 || entry.Height == 0)
            return entry.Spec?.SizePolicy.SizeClass == RenderResourceSizeClass.Custom ? "custom" : "-";

        string size = string.Concat(
            entry.Width.ToString(CultureInfo.InvariantCulture), "x",
            entry.Height.ToString(CultureInfo.InvariantCulture));
        if (entry.Layers > 1)
            size = string.Concat(size, " x", entry.Layers.ToString(CultureInfo.InvariantCulture));
        if (entry.Samples > 1)
            size = string.Concat(size, " ", entry.Samples.ToString(CultureInfo.InvariantCulture), "spp");
        return size;
    }

    /// <summary>
    /// Returns short flag codes: H history, X external, T transient, A alias candidate, M materialized, R required,
    /// D disabled, U declared but unused, ? used but undeclared.
    /// </summary>
    private static string FormatFlags(RenderPipelineResourceEntry entry)
    {
        Span<char> codes = stackalloc char[9];
        int count = 0;
        if (entry.Has(ERenderPipelineResourceFlags.History))
            codes[count++] = 'H';
        if (entry.Has(ERenderPipelineResourceFlags.External))
            codes[count++] = 'X';
        if (entry.Has(ERenderPipelineResourceFlags.Transient))
            codes[count++] = 'T';
        if (entry.Has(ERenderPipelineResourceFlags.AliasCandidate))
            codes[count++] = 'A';
        if (entry.Has(ERenderPipelineResourceFlags.Materialized))
            codes[count++] = 'M';
        if (entry.Has(ERenderPipelineResourceFlags.Required))
            codes[count++] = 'R';
        if (entry.Has(ERenderPipelineResourceFlags.Disabled))
            codes[count++] = 'D';
        if (entry.IsDeclaredButUnused)
            codes[count++] = 'U';
        if (entry.IsUsedButUndeclared)
            codes[count++] = '?';
        return new string(codes[..count]);
    }
}
