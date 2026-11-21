using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace XREngine;

/// <summary>
/// Wraps a YAML serializer and opens a <see cref="DocumentUniqueAnchorAssigner"/> document around each
/// serialize call. The anchor names then start at <c>o0</c> for each top-level document and stay
/// unique through the nested serialize calls of that document.
/// </summary>
/// <param name="inner">A serializer built with <see cref="DocumentUniqueAnchorAssigner.Register"/>.</param>
public sealed class DocumentScopedYamlSerializer(ISerializer inner) : ISerializer
{
    private readonly ISerializer _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public string Serialize(object? graph)
    {
        DocumentUniqueAnchorAssigner.BeginDocument();
        try
        {
            return _inner.Serialize(graph);
        }
        finally
        {
            DocumentUniqueAnchorAssigner.EndDocument();
        }
    }

    public string Serialize(object? graph, Type type)
    {
        DocumentUniqueAnchorAssigner.BeginDocument();
        try
        {
            return _inner.Serialize(graph, type);
        }
        finally
        {
            DocumentUniqueAnchorAssigner.EndDocument();
        }
    }

    public void Serialize(TextWriter writer, object? graph)
    {
        DocumentUniqueAnchorAssigner.BeginDocument();
        try
        {
            _inner.Serialize(writer, graph);
        }
        finally
        {
            DocumentUniqueAnchorAssigner.EndDocument();
        }
    }

    public void Serialize(TextWriter writer, object? graph, Type type)
    {
        DocumentUniqueAnchorAssigner.BeginDocument();
        try
        {
            _inner.Serialize(writer, graph, type);
        }
        finally
        {
            DocumentUniqueAnchorAssigner.EndDocument();
        }
    }

    public void Serialize(IEmitter emitter, object? graph)
    {
        DocumentUniqueAnchorAssigner.BeginDocument();
        try
        {
            _inner.Serialize(emitter, graph);
        }
        finally
        {
            DocumentUniqueAnchorAssigner.EndDocument();
        }
    }

    public void Serialize(IEmitter emitter, object? graph, Type type)
    {
        DocumentUniqueAnchorAssigner.BeginDocument();
        try
        {
            _inner.Serialize(emitter, graph, type);
        }
        finally
        {
            DocumentUniqueAnchorAssigner.EndDocument();
        }
    }
}
