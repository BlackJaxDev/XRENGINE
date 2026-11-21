using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// The state of the Workbench command palette: the search text and the filtered rows. The rows rebuild only when
/// the search text, the code filter, or the command catalog changes.
/// </summary>
public sealed class RenderPipelineCommandPalette
{
    private readonly List<RenderPipelinePaletteItem> _items = new(256);
    private string _builtSearch = "\0";
    private bool _builtHideCode;
    private int _builtCatalogVersion = -1;

    /// <summary>Gets or sets a value that is true while the palette popup is open.</summary>
    public bool IsOpen { get; set; }

    /// <summary>Gets or sets a value that asks the UI to open the popup in the next frame.</summary>
    public bool OpenRequested { get; set; }

    /// <summary>Gets or sets the search text.</summary>
    public string Search { get; set; } = string.Empty;

    /// <summary>Gets or sets the index of the highlighted row.</summary>
    public int Highlighted { get; set; }

    /// <summary>Gets the visible rows: snippets first, then commands in catalog order.</summary>
    public IReadOnlyList<RenderPipelinePaletteItem> Items => _items;

    /// <summary>Rebuilds the visible rows when an input changed.</summary>
    /// <param name="hideCodeCommands">True for authored pipelines, which cannot save code delegates.</param>
    public void Refresh(bool hideCodeCommands)
    {
        int catalogVersion = RenderCommandCatalog.Version;
        if (string.Equals(_builtSearch, Search, StringComparison.Ordinal) &&
            _builtHideCode == hideCodeCommands &&
            _builtCatalogVersion == catalogVersion)
        {
            return;
        }

        _builtSearch = Search;
        _builtHideCode = hideCodeCommands;
        _builtCatalogVersion = catalogVersion;
        _items.Clear();

        string search = Search.Trim();
        IReadOnlyList<RenderPipelineSnippet> snippets = RenderPipelineSnippets.All;
        for (int i = 0; i < snippets.Count; i++)
        {
            RenderPipelineSnippet snippet = snippets[i];
            if (Matches(snippet.Name, search) || Matches(snippet.Description, search))
                _items.Add(RenderPipelinePaletteItem.ForSnippet(snippet));
        }

        IReadOnlyList<RenderCommandCatalogEntry> entries = RenderCommandCatalog.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            RenderCommandCatalogEntry entry = entries[i];
            if (hideCodeCommands && entry.Info.RequiresCode)
                continue;
            if (!Matches(entry.SearchText, search))
                continue;

            _items.Add(RenderPipelinePaletteItem.ForCommand(entry));
        }

        Highlighted = Math.Clamp(Highlighted, 0, Math.Max(0, _items.Count - 1));
    }

    private static bool Matches(string text, string search)
        => search.Length == 0 || text.Contains(search, StringComparison.OrdinalIgnoreCase);
}
