using XREngine.Rendering.Pipelines.Commands;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine;

/// <summary>Preserves legacy command sequences while representing authored branch resource lifetime.</summary>
internal sealed class ViewportRenderCommandContainerYamlTypeConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(ViewportRenderCommandContainer);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.Accept<Scalar>(out Scalar? scalar) && (string.IsNullOrEmpty(scalar.Value) || scalar.Value == "~" || scalar.Value.Equals("null", StringComparison.OrdinalIgnoreCase)))
        {
            parser.Consume<Scalar>();
            return null;
        }

        ViewportRenderCommandContainer container = new();
        if (parser.Accept<SequenceStart>(out _))
        {
            container.SerializedCommands = (rootDeserializer(typeof(List<ViewportRenderCommand>)) as List<ViewportRenderCommand>) ?? [];
            return container;
        }

        parser.Consume<MappingStart>();
        while (!parser.Accept<MappingEnd>(out _))
        {
            string key = parser.Consume<Scalar>().Value;
            switch (key)
            {
                case nameof(ViewportRenderCommandContainer.BranchResources):
                    container.BranchResources = (ViewportRenderCommandContainer.BranchResourceBehavior)rootDeserializer(typeof(ViewportRenderCommandContainer.BranchResourceBehavior))!;
                    break;
                case nameof(ViewportRenderCommandContainer.Commands):
                    container.SerializedCommands = (rootDeserializer(typeof(List<ViewportRenderCommand>)) as List<ViewportRenderCommand>) ?? [];
                    break;
                default:
                    throw new YamlException($"Unknown authored command container property '{key}'.");
            }
        }
        parser.Consume<MappingEnd>();
        return container;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is null)
        {
            emitter.Emit(new Scalar("~"));
            return;
        }
        ViewportRenderCommandContainer container = (ViewportRenderCommandContainer)value;
        bool mapping = container.BranchResources != ViewportRenderCommandContainer.BranchResourceBehavior.PreserveResources;
        if (mapping)
        {
            emitter.Emit(new MappingStart());
            emitter.Emit(new Scalar(nameof(ViewportRenderCommandContainer.BranchResources)));
            serializer(container.BranchResources);
            emitter.Emit(new Scalar(nameof(ViewportRenderCommandContainer.Commands)));
        }
        emitter.Emit(new SequenceStart(null, null, true, SequenceStyle.Block));
        foreach (ViewportRenderCommand command in container)
            serializer(command);
        emitter.Emit(new SequenceEnd());
        if (mapping)
            emitter.Emit(new MappingEnd());
    }
}
