using System;
using System.Collections;
using System.Collections.Generic;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine;

internal sealed class InterfaceCollectionYamlNodeDeserializer : INodeDeserializer
{
    public bool Deserialize(
        IParser reader,
        Type expectedType,
        Func<IParser, Type, object?> nestedObjectDeserializer,
        out object? value,
        ObjectDeserializer rootDeserializer)
    {
        if (!TryResolveConcreteCollectionType(reader, expectedType, out Type? concreteType))
        {
            value = null;
            return false;
        }

        value = nestedObjectDeserializer(reader, concreteType);
        return true;
    }

    private static bool TryResolveConcreteCollectionType(IParser reader, Type expectedType, out Type? concreteType)
    {
        concreteType = null;

        if (!expectedType.IsInterface && !expectedType.IsAbstract)
            return false;

        if (reader.Accept<SequenceStart>(out _))
            return TryResolveSequenceCollectionType(expectedType, out concreteType);

        if (reader.Accept<MappingStart>(out _))
            return TryResolveMappingCollectionType(expectedType, out concreteType);

        return false;
    }

    private static bool TryResolveSequenceCollectionType(Type expectedType, out Type? concreteType)
    {
        concreteType = null;

        if (TryGetGenericInterface(expectedType, typeof(ISet<>), out Type[]? setArguments)
            || TryGetGenericInterface(expectedType, typeof(IReadOnlySet<>), out setArguments))
        {
            concreteType = typeof(HashSet<>).MakeGenericType(setArguments);
            return true;
        }

        if (TryGetGenericInterface(expectedType, typeof(IReadOnlyList<>), out Type[]? listArguments)
            || TryGetGenericInterface(expectedType, typeof(IReadOnlyCollection<>), out listArguments)
            || TryGetGenericInterface(expectedType, typeof(IList<>), out listArguments)
            || TryGetGenericInterface(expectedType, typeof(ICollection<>), out listArguments)
            || TryGetGenericInterface(expectedType, typeof(IEnumerable<>), out listArguments))
        {
            concreteType = typeof(List<>).MakeGenericType(listArguments);
            return true;
        }

        if (expectedType == typeof(IList)
            || expectedType == typeof(ICollection)
            || expectedType == typeof(IEnumerable))
        {
            concreteType = typeof(ArrayList);
            return true;
        }

        return false;
    }

    private static bool TryResolveMappingCollectionType(Type expectedType, out Type? concreteType)
    {
        concreteType = null;

        if (TryGetGenericInterface(expectedType, typeof(IReadOnlyDictionary<,>), out Type[]? dictionaryArguments)
            || TryGetGenericInterface(expectedType, typeof(IDictionary<,>), out dictionaryArguments))
        {
            concreteType = typeof(Dictionary<,>).MakeGenericType(dictionaryArguments);
            return true;
        }

        if (expectedType == typeof(IDictionary))
        {
            concreteType = typeof(Dictionary<object, object>);
            return true;
        }

        return false;
    }

    private static bool TryGetGenericInterface(Type expectedType, Type interfaceTypeDefinition, out Type[]? genericArguments)
    {
        if (expectedType.IsGenericType
            && expectedType.GetGenericTypeDefinition() == interfaceTypeDefinition)
        {
            genericArguments = expectedType.GetGenericArguments();
            return true;
        }

        foreach (Type implementedInterface in expectedType.GetInterfaces())
        {
            if (!implementedInterface.IsGenericType
                || implementedInterface.GetGenericTypeDefinition() != interfaceTypeDefinition)
            {
                continue;
            }

            genericArguments = implementedInterface.GetGenericArguments();
            return true;
        }

        genericArguments = null;
        return false;
    }
}
