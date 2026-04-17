using System;
using System.Collections.Generic;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace XREngine;

[AttributeUsage(AttributeTargets.Property)]
internal sealed class YamlTransformReferenceAttribute : Attribute
{
}

internal sealed class TransformYamlTypeInspector(ITypeInspector inner, bool applyReferenceOnRead) : ITypeInspector
{
    private readonly ITypeInspector _inner = inner;
    private readonly bool _applyReferenceOnRead = applyReferenceOnRead;

    public IEnumerable<IPropertyDescriptor> GetProperties(Type type, object? container)
    {
        bool interceptSerializedId = typeof(XREngine.Scene.Transforms.TransformBase).IsAssignableFrom(type);
        foreach (IPropertyDescriptor descriptor in _inner.GetProperties(type, container))
            yield return Wrap(descriptor, interceptSerializedId);
    }

    public IPropertyDescriptor GetProperty(Type type, object? container, string name, bool ignoreUnmatched, bool caseInsensitivePropertyMatching)
    {
        StringComparison comparison = caseInsensitivePropertyMatching
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        bool interceptSerializedId = typeof(XREngine.Scene.Transforms.TransformBase).IsAssignableFrom(type);
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

        IPropertyDescriptor wrapped = Wrap(descriptor, interceptSerializedId);

        if (_applyReferenceOnRead && wrapped.GetCustomAttribute<YamlTransformReferenceAttribute>() is not null)
            YamlTransformReferenceContext.EnqueueRead();

        return wrapped;
    }

    public string GetEnumName(Type enumType, string name)
        => _inner.GetEnumName(enumType, name);

    public string GetEnumValue(object value)
        => _inner.GetEnumValue(value);

    private IPropertyDescriptor Wrap(IPropertyDescriptor descriptor, bool interceptSerializedId)
    {
        YamlTransformReferenceAttribute? referenceAttribute = descriptor.GetCustomAttribute<YamlTransformReferenceAttribute>();
        bool interceptIdProperty = interceptSerializedId && string.Equals(descriptor.Name, nameof(XREngine.Data.Core.XRObjectBase.ID), StringComparison.Ordinal);

        if (!interceptIdProperty && referenceAttribute is null)
            return descriptor;

        return new TransformYamlPropertyDescriptor(descriptor, interceptIdProperty, referenceAttribute, _applyReferenceOnRead);
    }

    private sealed class TransformYamlPropertyDescriptor(
        IPropertyDescriptor inner,
        bool interceptIdProperty,
        YamlTransformReferenceAttribute? referenceAttribute,
        bool applyReferenceOnRead) : IPropertyDescriptor
    {
        private readonly IPropertyDescriptor _inner = inner;
        private readonly bool _interceptIdProperty = interceptIdProperty;
        private readonly YamlTransformReferenceAttribute? _referenceAttribute = referenceAttribute;
        private readonly bool _applyReferenceOnRead = applyReferenceOnRead;

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
                return _inner.ConverterType;
#pragma warning restore CS8603
            }
        }

        public void Write(object target, object? value)
        {
            if (_interceptIdProperty && target is XREngine.Scene.Transforms.TransformBase transform)
            {
                transform.SerializedReferenceId = value switch
                {
                    Guid id => id,
                    string text when Guid.TryParse(text, out Guid parsed) => parsed,
                    _ => Guid.Empty,
                };
                return;
            }

            _inner.Write(target, value);
        }

        public TAttribute? GetCustomAttribute<TAttribute>() where TAttribute : Attribute
        {
            if (_referenceAttribute is not null && typeof(TAttribute) == typeof(YamlTransformReferenceAttribute))
                return (TAttribute?)(Attribute?)_referenceAttribute;

            return _inner.GetCustomAttribute<TAttribute>();
        }

        public IObjectDescriptor Read(object target)
        {
            if (_interceptIdProperty && target is XREngine.Scene.Transforms.TransformBase transform)
            {
                return new ObjectDescriptor(
                    transform.EffectiveSerializedReferenceId,
                    typeof(Guid),
                    typeof(Guid),
                    ScalarStyle.Any);
            }

            IObjectDescriptor descriptor = _inner.Read(target);
            if (!_applyReferenceOnRead && _referenceAttribute is not null && descriptor.Value is not null)
                YamlTransformReferenceContext.EnqueueWrite();

            return descriptor;
        }
    }
}

internal static class YamlTransformReferenceContext
{
    [ThreadStatic]
    private static Queue<bool>? _readEntries;

    [ThreadStatic]
    private static Queue<bool>? _writeEntries;

    public static void EnqueueRead()
        => (_readEntries ??= new Queue<bool>()).Enqueue(true);

    public static bool ConsumeRead()
    {
        Queue<bool>? entries = _readEntries;
        if (entries is null || entries.Count == 0)
            return false;

        _ = entries.Dequeue();
        if (entries.Count == 0)
            _readEntries = null;
        return true;
    }

    public static void EnqueueWrite()
        => (_writeEntries ??= new Queue<bool>()).Enqueue(true);

    public static bool ConsumeWrite()
    {
        Queue<bool>? entries = _writeEntries;
        if (entries is null || entries.Count == 0)
            return false;

        _ = entries.Dequeue();
        if (entries.Count == 0)
            _writeEntries = null;
        return true;
    }

    public static void ResetReadState()
        => _readEntries = null;

    public static void ResetWriteState()
        => _writeEntries = null;
}