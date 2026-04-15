using System;
using System.Collections;
using System.Collections.Generic;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace XREngine;

internal sealed class YamlDefaultTypeInspector(ITypeInspector inner, bool applyDefaultTypeOnRead) : ITypeInspector
{
    private readonly ITypeInspector _inner = inner;
    private readonly bool _applyDefaultTypeOnRead = applyDefaultTypeOnRead;

    public IEnumerable<IPropertyDescriptor> GetProperties(Type type, object? container)
    {
        foreach (IPropertyDescriptor descriptor in _inner.GetProperties(type, container))
            yield return Wrap(descriptor);
    }

    public IPropertyDescriptor GetProperty(Type type, object? container, string name, bool ignoreUnmatched, bool caseInsensitivePropertyMatching)
    {
        StringComparison comparison = caseInsensitivePropertyMatching
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        IPropertyDescriptor? descriptor = null;
        foreach (IPropertyDescriptor candidate in _inner.GetProperties(type, container))
        {
            if (!string.Equals(candidate.Name, name, comparison))
                continue;

            descriptor = candidate;
            break;
        }

        descriptor ??= _inner.GetProperty(type, container, name, ignoreUnmatched, caseInsensitivePropertyMatching);
        if (descriptor is null)
            return null!;

        if (_applyDefaultTypeOnRead && descriptor.GetCustomAttribute<YamlDefaultTypeAttribute>() is { } attribute)
            YamlDefaultTypeContext.EnqueueRead(descriptor.Type, attribute.DefaultType);

        return Wrap(descriptor);
    }

    public string GetEnumName(Type enumType, string name)
        => _inner.GetEnumName(enumType, name);

    public string GetEnumValue(object value)
        => _inner.GetEnumValue(value);

    private IPropertyDescriptor Wrap(IPropertyDescriptor descriptor)
    {
        YamlDefaultTypeAttribute? attribute = descriptor.GetCustomAttribute<YamlDefaultTypeAttribute>();
        if (attribute is null)
            return descriptor;

        return new YamlDefaultTypePropertyDescriptor(descriptor, attribute, _applyDefaultTypeOnRead);
    }

    private sealed class YamlDefaultTypePropertyDescriptor(
        IPropertyDescriptor inner,
        YamlDefaultTypeAttribute attribute,
        bool applyDefaultTypeOnRead) : IPropertyDescriptor
    {
        private readonly IPropertyDescriptor _inner = inner;
        private readonly YamlDefaultTypeAttribute _attribute = attribute;
        private readonly bool _applyDefaultTypeOnRead = applyDefaultTypeOnRead;

        public string Name => _inner.Name;

        public Type Type => _inner.Type;

        public Type? TypeOverride
        {
            get => _inner.TypeOverride;
            set => _inner.TypeOverride = value;
        }

        public int Order
        {
            get => _inner.Order;
            set => _inner.Order = value;
        }

        public ScalarStyle ScalarStyle
        {
            get => _inner.ScalarStyle;
            set => _inner.ScalarStyle = value;
        }

        public bool CanWrite => _inner.CanWrite;

        public bool AllowNulls => _inner.AllowNulls;

        public bool Required => _inner.Required;

        public Type ConverterType
        {
            get
            {
#pragma warning disable CS8603
                // YamlDotNet uses null here to mean "no explicit converter".
                return _inner.ConverterType;
#pragma warning restore CS8603
            }
        }

        public void Write(object target, object? value)
            => _inner.Write(target, value);

        public TAttribute? GetCustomAttribute<TAttribute>() where TAttribute : Attribute
        {
            if (typeof(TAttribute) == typeof(YamlDefaultTypeAttribute))
                return (TAttribute?)(Attribute?)_attribute;

            return _inner.GetCustomAttribute<TAttribute>();
        }

        public IObjectDescriptor Read(object target)
        {
            IObjectDescriptor descriptor = _inner.Read(target);

            if (!_applyDefaultTypeOnRead && IsPotentialMapping(descriptor))
                YamlDefaultTypeContext.EnqueueWrite(_attribute.DefaultType);

            return descriptor;
        }

        private static bool IsPotentialMapping(IObjectDescriptor descriptor)
        {
            object? value = descriptor.Value;
            if (value is null)
                return false;

            Type runtimeType = descriptor.Type;
            if (runtimeType.IsPrimitive || runtimeType == typeof(string) || runtimeType.IsEnum)
                return false;

            if (typeof(IDictionary).IsAssignableFrom(runtimeType))
                return true;

            return !typeof(IEnumerable).IsAssignableFrom(runtimeType);
        }
    }
}

internal static class YamlDefaultTypeContext
{
    private readonly record struct Entry(Type DeclaredType, Type DefaultType);

    [ThreadStatic]
    private static List<Entry>? _readEntries;

    [ThreadStatic]
    private static Queue<Type>? _writeDefaults;

    public static void EnqueueRead(Type declaredType, Type defaultType)
    {
        (_readEntries ??= []).Add(new Entry(declaredType, defaultType));
    }

    public static bool TryConsumeRead(Type expectedType, out Type? defaultType)
    {
        List<Entry>? entries = _readEntries;
        if (entries is not null)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                if (entry.DeclaredType != expectedType)
                    continue;

                defaultType = entry.DefaultType;
                entries.RemoveAt(i);
                if (entries.Count == 0)
                    _readEntries = null;
                return true;
            }
        }

        defaultType = null;
        return false;
    }

    public static void EnqueueWrite(Type defaultType)
    {
        (_writeDefaults ??= new Queue<Type>()).Enqueue(defaultType);
    }

    public static Type? ConsumeWriteDefaultType()
    {
        Queue<Type>? defaults = _writeDefaults;
        if (defaults is null || defaults.Count == 0)
            return null;

        Type defaultType = defaults.Dequeue();
        if (defaults.Count == 0)
            _writeDefaults = null;
        return defaultType;
    }
}