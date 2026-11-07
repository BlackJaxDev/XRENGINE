namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Lists the concrete command types that tools can add to a pipeline, sorted by category and display name.
    /// The catalog reads the registered command factories, so it includes command types from game and plugin assemblies.
    /// </summary>
    /// <remarks>
    /// The catalog rebuilds when the number of registered command types changes. Draw code can read
    /// <see cref="Entries"/> each frame without allocating.
    /// </remarks>
    public static class RenderCommandCatalog
    {
        private static readonly Lock s_lock = new();
        private static RenderCommandCatalogEntry[] s_entries = [];
        private static int s_registeredCount = -1;
        private static int s_version;

        /// <summary>Gets a number that changes each time the catalog rebuilds.</summary>
        public static int Version
        {
            get
            {
                Refresh();
                return Volatile.Read(ref s_version);
            }
        }

        /// <summary>Gets all catalog entries, sorted by category and display name.</summary>
        public static IReadOnlyList<RenderCommandCatalogEntry> Entries
        {
            get
            {
                Refresh();
                return Volatile.Read(ref s_entries);
            }
        }

        /// <summary>Finds the entry of <paramref name="type"/>. Returns false when the type is not a registered concrete command.</summary>
        public static bool TryGet(Type type, out RenderCommandCatalogEntry? entry)
        {
            IReadOnlyList<RenderCommandCatalogEntry> entries = Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Type == type)
                {
                    entry = entries[i];
                    return true;
                }
            }

            entry = null;
            return false;
        }

        private static void Refresh()
        {
            int count = ViewportRenderCommandContainer.RegisteredCommandTypeCount;
            if (count == Volatile.Read(ref s_registeredCount))
                return;

            lock (s_lock)
            {
                if (count == s_registeredCount)
                    return;

                Type baseType = typeof(ViewportRenderCommand);
                List<RenderCommandCatalogEntry> entries = new(count);
                foreach (Type type in ViewportRenderCommandContainer.RegisteredCommandTypes)
                {
                    if (type.IsAbstract || type.ContainsGenericParameters || !baseType.IsAssignableFrom(type))
                        continue;

                    entries.Add(new RenderCommandCatalogEntry(type, RenderCommandInfo.Get(type), RenderPipelineScript.GetCommandMethodName(type)));
                }

                entries.Sort(static (a, b) =>
                {
                    int category = a.Info.Category.CompareTo(b.Info.Category);
                    return category != 0 ? category : string.Compare(a.Info.DisplayName, b.Info.DisplayName, StringComparison.OrdinalIgnoreCase);
                });

                Volatile.Write(ref s_entries, [.. entries]);
                Volatile.Write(ref s_registeredCount, count);
                s_version++;
            }
        }
    }
}
