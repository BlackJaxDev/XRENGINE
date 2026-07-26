using System.Numerics;
using System.Text.Json;

namespace XREngine.Editor.MaterialAuthoring;

public enum ETexturePackingNodeKind
{
    ImageSource,
    Constant,
    Gradient,
    ChannelSelect,
    Invert,
    Remap,
    Brightness,
    Hue,
    Saturation,
    Grayscale,
    Rotate,
    Scale,
    Offset,
    Edge,
    Kernel,
    Blend,
    Output,
}

public sealed record TexturePackingNode(
    Guid Id,
    ETexturePackingNodeKind Kind,
    string Name,
    Vector4 Parameters,
    string? AssetReference = null);

public sealed record TexturePackingEdge(
    Guid SourceNode,
    int SourceChannel,
    Guid DestinationNode,
    int DestinationInput);

/// <summary>
/// Versioned advanced texture-packer graph with reusable nodes and explicit
/// channel wiring. Validation rejects missing sockets and graph cycles before
/// any image work starts.
/// </summary>
public sealed class TexturePackingGraph
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public List<TexturePackingNode> Nodes { get; init; } = [];
    public List<TexturePackingEdge> Edges { get; init; } = [];
    public Guid? OutputNode { get; set; }

    public IReadOnlyList<string> Validate()
    {
        List<string> diagnostics = [];
        if (Version != CurrentVersion)
            diagnostics.Add($"Graph version {Version} is unsupported.");
        Dictionary<Guid, TexturePackingNode> nodes = new();
        foreach (TexturePackingNode node in Nodes)
        {
            if (!nodes.TryAdd(node.Id, node))
                diagnostics.Add($"Duplicate node ID '{node.Id}'.");
        }
        if (OutputNode is null || !nodes.TryGetValue(OutputNode.Value, out TexturePackingNode? output) ||
            output.Kind != ETexturePackingNodeKind.Output)
            diagnostics.Add("A valid output node is required.");

        HashSet<(Guid Node, int Input)> inputs = [];
        foreach (TexturePackingEdge edge in Edges)
        {
            if (!nodes.ContainsKey(edge.SourceNode))
                diagnostics.Add($"Edge source '{edge.SourceNode}' is missing.");
            if (!nodes.ContainsKey(edge.DestinationNode))
                diagnostics.Add($"Edge destination '{edge.DestinationNode}' is missing.");
            if (edge.SourceChannel is < 0 or > 3)
                diagnostics.Add($"Source channel {edge.SourceChannel} is invalid.");
            if (edge.DestinationInput is < 0 or > 7)
                diagnostics.Add($"Destination input {edge.DestinationInput} is invalid.");
            if (!inputs.Add((edge.DestinationNode, edge.DestinationInput)))
                diagnostics.Add($"Input {edge.DestinationInput} on '{edge.DestinationNode}' has multiple sources.");
        }
        DetectCycles(nodes, diagnostics);
        return diagnostics;
    }

    public TexturePackingRecipe Compile(
        int width,
        int height,
        EMaterialTextureColorSpace colorSpace,
        string outputFormat,
        int quality)
    {
        IReadOnlyList<string> diagnostics = Validate();
        if (diagnostics.Count > 0)
            throw new InvalidDataException(string.Join("; ", diagnostics));
        TexturePackingNode output = Nodes.First(node => node.Id == OutputNode);
        TexturePackingChannel[] channels = new TexturePackingChannel[4];
        List<TextureImageOperation> operations = [];
        for (int channel = 0; channel < 4; channel++)
        {
            TexturePackingEdge? edge = Edges.FirstOrDefault(candidate =>
                candidate.DestinationNode == output.Id &&
                candidate.DestinationInput == channel);
            channels[channel] = edge is null
                ? new() { Kind = ETextureChannelSourceKind.Constant, Constant = channel == 3 ? 1.0f : 0.0f }
                : CompileChannel(edge, operations);
        }
        return new()
        {
            Width = width,
            Height = height,
            LinearData = colorSpace == EMaterialTextureColorSpace.Linear,
            OutputFormat = outputFormat,
            Quality = quality,
            Channels = channels,
            Operations = operations,
        };
    }

    public string Serialize()
        => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    private TexturePackingChannel CompileChannel(
        TexturePackingEdge outputEdge,
        ICollection<TextureImageOperation> operations)
    {
        TexturePackingNode node = Nodes.First(candidate => candidate.Id == outputEdge.SourceNode);
        bool invert = false;
        Vector2 remap = new(0.0f, 1.0f);
        HashSet<Guid> visited = [];
        while (visited.Add(node.Id))
        {
            switch (node.Kind)
            {
                case ETexturePackingNodeKind.ImageSource:
                    return new()
                    {
                        Kind = ETextureChannelSourceKind.Image,
                        SourceAsset = node.AssetReference,
                        InputChannel = (ETextureChannel)Math.Clamp(outputEdge.SourceChannel, 0, 4),
                        Invert = invert,
                        Remap = remap,
                    };
                case ETexturePackingNodeKind.Constant:
                    return new()
                    {
                        Kind = ETextureChannelSourceKind.Constant,
                        Constant = node.Parameters.X,
                        Invert = invert,
                        Remap = remap,
                    };
                case ETexturePackingNodeKind.Invert:
                    invert = !invert;
                    break;
                case ETexturePackingNodeKind.Remap:
                    remap = new(node.Parameters.X, node.Parameters.Y);
                    break;
                default:
                    if (TryMapOperation(node, out TextureImageOperation? operation))
                        operations.Add(operation!);
                    break;
            }

            TexturePackingEdge? input = Edges.FirstOrDefault(candidate =>
                candidate.DestinationNode == node.Id &&
                candidate.DestinationInput == 0);
            if (input is null)
                break;
            node = Nodes.First(candidate => candidate.Id == input.SourceNode);
        }
        return new() { Kind = ETextureChannelSourceKind.Constant, Constant = 0.0f, Invert = invert, Remap = remap };
    }

    private static bool TryMapOperation(TexturePackingNode node, out TextureImageOperation? operation)
    {
        if (Enum.TryParse(node.Kind.ToString(), out ETextureImageOperationKind kind))
        {
            operation = new(kind, node.Parameters, node.AssetReference);
            return true;
        }
        operation = null;
        return false;
    }

    private void DetectCycles(
        IReadOnlyDictionary<Guid, TexturePackingNode> nodes,
        ICollection<string> diagnostics)
    {
        HashSet<Guid> visiting = [];
        HashSet<Guid> visited = [];
        foreach (Guid node in nodes.Keys)
            Visit(node);
        return;

        void Visit(Guid node)
        {
            if (visited.Contains(node))
                return;
            if (!visiting.Add(node))
            {
                diagnostics.Add($"Texture packing graph cycle detected at '{node}'.");
                return;
            }
            foreach (TexturePackingEdge edge in Edges)
                if (edge.SourceNode == node && nodes.ContainsKey(edge.DestinationNode))
                    Visit(edge.DestinationNode);
            visiting.Remove(node);
            visited.Add(node);
        }
    }
}
