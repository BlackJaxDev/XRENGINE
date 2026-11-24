namespace XREngine.Tools.UnityShaderTranslator;

/// <summary>
/// Holds the command-line options of the translator tool.
/// </summary>
internal sealed class Options
{
    public List<string> Shaders { get; } = [];
    public List<string> ScanFolders { get; } = [];
    public string OutputDirectory { get; private set; } = string.Empty;
    public string? ProjectRoot { get; private set; }
    public string? CompatDirectory { get; private set; }
    public HashSet<string> Keywords { get; } = new(StringComparer.Ordinal);
    public bool Keep { get; private set; }

    /// <summary>Parses the arguments, or returns null when they are not valid.</summary>
    public static Options? Parse(string[] args)
    {
        Options options = new();
        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            if (argument == "--keep")
            {
                options.Keep = true;
                continue;
            }

            if (i + 1 >= args.Length)
                return null;

            string value = args[++i];
            switch (argument)
            {
                case "--shader":
                    options.Shaders.Add(Path.GetFullPath(value));
                    break;
                case "--scan":
                    options.ScanFolders.Add(Path.GetFullPath(value));
                    break;
                case "--out":
                    options.OutputDirectory = Path.GetFullPath(value);
                    break;
                case "--project":
                    options.ProjectRoot = Path.GetFullPath(value);
                    break;
                case "--compat":
                    options.CompatDirectory = Path.GetFullPath(value);
                    break;
                case "--keywords":
                    foreach (string keyword in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        options.Keywords.Add(keyword);
                    break;
                default:
                    return null;
            }
        }

        if (options.OutputDirectory.Length == 0 || (options.Shaders.Count == 0 && options.ScanFolders.Count == 0))
            return null;
        return options;
    }

    /// <summary>Returns the explicit shaders and the scanned shaders, sorted and without duplicates.</summary>
    public List<string> CollectShaders()
    {
        SortedSet<string> shaders = new(Shaders, StringComparer.OrdinalIgnoreCase);
        foreach (string folder in ScanFolders)
            foreach (string file in Directory.EnumerateFiles(folder, "*.shader", SearchOption.AllDirectories))
                shaders.Add(file);
        return [.. shaders];
    }
}
