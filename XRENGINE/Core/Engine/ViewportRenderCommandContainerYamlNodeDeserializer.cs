using System;
using XREngine.Rendering.Pipelines.Commands;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine;

internal sealed class ViewportRenderCommandContainerYamlNodeDeserializer : INodeDeserializer
{
    public bool Deserialize(IParser reader,
                            Type expectedType,
                            Func<IParser, Type, object?> nestedObjectDeserializer,
                            out object? value,
                            ObjectDeserializer rootDeserializer)
    {
        if (!typeof(ViewportRenderCommandContainer).IsAssignableFrom(expectedType))
        {
            value = null;
            return false;
        }

        if (reader.Accept<Scalar>(out Scalar? scalar)
            && (scalar.Value is null
                || scalar.Value.Length == 0
                || string.Equals(scalar.Value, "~", StringComparison.Ordinal)
                || string.Equals(scalar.Value, "null", StringComparison.OrdinalIgnoreCase)))
        {
            reader.Consume<Scalar>();
            value = null;
            return true;
        }

        if (!reader.Accept<SequenceStart>(out _))
        {
            value = null;
            return false;
        }

        ViewportRenderCommandContainer container = Activator.CreateInstance(expectedType) as ViewportRenderCommandContainer
            ?? new ViewportRenderCommandContainer();

        ViewportRenderCommand[] commands = rootDeserializer(typeof(ViewportRenderCommand[])) as ViewportRenderCommand[] ?? [];
        foreach (ViewportRenderCommand command in commands)
            container.Add(command);

        value = container;
        return true;
    }
}