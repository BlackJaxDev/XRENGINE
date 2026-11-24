namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// One command type that tools can add to a pipeline: its tool metadata, its script method name, and its search text.
    /// </summary>
    public sealed class RenderCommandCatalogEntry
    {
        internal RenderCommandCatalogEntry(Type type, RenderCommandInfo info, string scriptMethodName)
        {
            Type = type;
            Info = info;
            ScriptMethodName = scriptMethodName;
            SearchText = $"{info.DisplayName} {type.Name} {scriptMethodName} {info.Category} {info.Description}".ToLowerInvariant();
        }

        /// <summary>Gets the concrete command type.</summary>
        public Type Type { get; }

        /// <summary>Gets the tool metadata of the command type.</summary>
        public RenderCommandInfo Info { get; }

        /// <summary>Gets the name of the command in <c>.xrs</c> render pipeline scripts.</summary>
        public string ScriptMethodName { get; }

        /// <summary>Gets lowercase text that tools match against a search string.</summary>
        public string SearchText { get; }
    }
}
