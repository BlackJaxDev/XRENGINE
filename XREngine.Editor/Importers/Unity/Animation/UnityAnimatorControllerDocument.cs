using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace XREngine.Scene.Importers;

/// <summary>
/// Parsed multi-document representation of a Unity YAML <c>.controller</c> asset, indexed by
/// local <c>fileID</c> for states, state machines, transitions, blend trees, and behaviours.
/// </summary>
internal sealed class UnityAnimatorControllerDocument
{
    private static readonly Regex DocumentHeaderRegex = new(
        @"^---\s*!u!(?<classId>-?\d+)\s*&(?<fileId>-?\d+)(?<stripped>\s+stripped)?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public required string FilePath { get; init; }
    public string? SourceGuid { get; init; }
    public YamlMappingNode? ControllerRoot { get; private set; }
    public Dictionary<long, YamlMappingNode> StateMachines { get; } = [];
    public Dictionary<long, YamlMappingNode> States { get; } = [];
    public Dictionary<long, YamlMappingNode> Transitions { get; } = [];
    public Dictionary<long, YamlMappingNode> BlendTrees { get; } = [];
    public Dictionary<long, YamlMappingNode> Behaviours { get; } = [];

    /// <summary>
    /// Reads a Unity <c>.controller</c> YAML file and indexes its embedded animation objects by <c>fileID</c>.
    /// </summary>
    public static UnityAnimatorControllerDocument Load(string filePath, string? sourceGuid = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string normalizedPath = Path.GetFullPath(filePath);
        if (!File.Exists(normalizedPath))
            throw new FileNotFoundException("Unity AnimatorController file was not found.", normalizedPath);

        var document = new UnityAnimatorControllerDocument
        {
            FilePath = normalizedPath,
            SourceGuid = sourceGuid,
        };

        int currentClassId = 0;
        long currentFileId = 0;
        bool currentStripped = false;
        var currentBody = new StringBuilder(1024);

        foreach (string line in File.ReadLines(normalizedPath))
        {
            if (line.StartsWith("%YAML", StringComparison.Ordinal) ||
                line.StartsWith("%TAG", StringComparison.Ordinal))
            {
                continue;
            }

            Match header = DocumentHeaderRegex.Match(line);
            if (header.Success)
            {
                FlushSection(document, currentClassId, currentFileId, currentStripped, currentBody);
                currentClassId = int.Parse(header.Groups["classId"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                currentFileId = long.Parse(header.Groups["fileId"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                currentStripped = header.Groups["stripped"].Success;
                currentBody.Clear();
                continue;
            }

            if (currentClassId != 0)
            {
                currentBody.AppendLine(line);
            }
        }

        FlushSection(document, currentClassId, currentFileId, currentStripped, currentBody);
        return document;
    }

    private static void FlushSection(
        UnityAnimatorControllerDocument document,
        int classId,
        long fileId,
        bool stripped,
        StringBuilder body)
    {
        if (classId == 0 || stripped || body.Length == 0)
            return;

        if (classId is not (91 or 114 or 206 or 1101 or 1102 or 1107))
            return;

        YamlMappingNode? mapping = ParseSingleObjectMapping(body.ToString());
        if (mapping is null)
            return;

        switch (classId)
        {
            case 91:
                document.ControllerRoot ??= mapping;
                break;
            case 114:
                document.Behaviours[fileId] = mapping;
                break;
            case 206:
                document.BlendTrees[fileId] = mapping;
                break;
            case 1101:
                document.Transitions[fileId] = mapping;
                break;
            case 1102:
                document.States[fileId] = mapping;
                break;
            case 1107:
                document.StateMachines[fileId] = mapping;
                break;
        }
    }

    private static YamlMappingNode? ParseSingleObjectMapping(string sectionYaml)
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(sectionYaml);
        yaml.Load(reader);
        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            return null;

        foreach ((_, YamlNode valueNode) in root.Children)
        {
            if (valueNode is YamlMappingNode objectMapping)
                return objectMapping;
        }

        return null;
    }
}
