using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using XREngine.Components;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;

namespace XREngine
{
    /*
    /// <summary>
    /// Provides a YAML serializer tuned for editor play-mode snapshots.
    /// Wraps XRComponent properties so individual getter failures are ignored
    /// instead of aborting the entire serialization pass.
    /// </summary>
    internal static class SnapshotYamlSerializer
    {
        public static readonly ISerializer Serializer = CreateSerializer();

        private static ISerializer CreateSerializer()
        {
            var builder = new SerializerBuilder()
                .EnablePrivateConstructors()
                .IncludeNonPublicProperties()
                .EnsureRoundtrip()
                .WithEventEmitter(nextEmitter => new DepthTrackingEventEmitter(nextEmitter))
                .WithTypeInspector(inner => new SafeComponentTypeInspector(inner))
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull |
                                                DefaultValuesHandling.OmitDefaults |
                                                DefaultValuesHandling.OmitEmptyCollections);

            foreach (var converter in AssetManager.YamlTypeConverters)
                builder.WithTypeConverter(converter);

            return builder.Build();
        }

        private sealed class SafeComponentTypeInspector(ITypeInspector inner) : ITypeInspector
        {
            private readonly ITypeInspector _inner = inner;

            public IEnumerable<IPropertyDescriptor> GetProperties(Type type, object? container)
            {
                bool wrap = typeof(XRComponent).IsAssignableFrom(type);
                foreach (var descriptor in _inner.GetProperties(type, container))
                    yield return wrap ? new SafePropertyDescriptor(descriptor, type) : descriptor;
            }

            public IPropertyDescriptor GetProperty(Type type, object? container, string name, bool ignoreUnmatched, bool caseInsensitivePropertyMatching)
            {
                var descriptor = _inner.GetProperty(type, container, name, ignoreUnmatched, caseInsensitivePropertyMatching);
                if (descriptor is null || !typeof(XRComponent).IsAssignableFrom(type))
                    return descriptor!;

                return new SafePropertyDescriptor(descriptor, type);
            }

            public string GetEnumName(Type enumType, string name)
                => _inner.GetEnumName(enumType, name);

            public string GetEnumValue(object value)
                => _inner.GetEnumValue(value);
        }

        private sealed class SafePropertyDescriptor(IPropertyDescriptor inner, Type declaringType) : IPropertyDescriptor
        {
            private static readonly ConcurrentDictionary<(Type DeclaringType, string PropertyName), byte> LoggedSkips = new();

            private readonly IPropertyDescriptor _inner = inner;
            private readonly Type _declaringType = declaringType;

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

            public Type ConverterType => _inner.ConverterType;

            public void Write(object target, object? value)
                => _inner.Write(target, value);

            public TAttribute? GetCustomAttribute<TAttribute>() where TAttribute : Attribute
                => _inner.GetCustomAttribute<TAttribute>();

            public IObjectDescriptor Read(object target)
            {
                try
                {
                    return _inner.Read(target);
                }
                catch (Exception ex)
                {
                    LogSkip(ex);
                    var fallback = GetDefaultValue();
                    var propertyType = GetPropertyType();
                    return new ObjectDescriptor(fallback, propertyType, propertyType);
                }
            }

            private object? GetDefaultValue()
            {
                var propertyType = GetPropertyType();
                return propertyType.IsValueType ? Activator.CreateInstance(propertyType) : null;
            }

            private Type GetPropertyType()
                => TypeOverride ?? Type;

            private void LogSkip(Exception ex)
            {
                var key = (_declaringType, Name);
                if (LoggedSkips.TryAdd(key, 0))
                    Debug.LogWarning($"Snapshot serializer skipped '{_declaringType.Name}.{Name}': {ex.Message}");
            }
        }
    }
    */
}