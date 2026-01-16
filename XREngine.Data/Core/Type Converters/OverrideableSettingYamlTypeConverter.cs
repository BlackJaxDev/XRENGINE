using System;
using System.Diagnostics.CodeAnalysis;
using XREngine.Data.Core;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine.Data
{
    /// <summary>
    /// YAML converter for <see cref="OverrideableSetting{T}"/> to ensure generic overrides
    /// serialize as { HasOverride, Value } mappings.
    /// </summary>
    [YamlTypeConverter]
    public sealed class OverrideableSettingYamlTypeConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type)
            => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(OverrideableSetting<>);

        [UnconditionalSuppressMessage("Trimming", "IL2067:RequiresUnreferencedCode", Justification = "OverrideableSetting is expected to have a parameterless constructor.")]
        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (!parser.TryConsume<MappingStart>(out _))
                throw new YamlException("Expected a mapping to deserialize an OverrideableSetting.");

            bool hasOverride = false;
            bool hasOverrideSet = false;
            object? value = null;

            while (!parser.TryConsume<MappingEnd>(out _))
            {
                if (!parser.TryConsume<Scalar>(out var keyScalar))
                    throw new YamlException("Expected a scalar key while deserializing OverrideableSetting.");

                string key = keyScalar.Value ?? string.Empty;
                switch (key)
                {
                    case nameof(OverrideableSetting<int>.HasOverride):
                        hasOverride = (bool)(rootDeserializer(typeof(bool)) ?? false);
                        hasOverrideSet = true;
                        break;
                    case nameof(OverrideableSetting<int>.Value):
                        value = rootDeserializer(type.GetGenericArguments()[0]);
                        break;
                    default:
                        rootDeserializer(typeof(object));
                        break;
                }
            }

            object? instance = Activator.CreateInstance(type);
            if (instance is IOverrideableSetting overrideable)
            {
                if (value is not null)
                    overrideable.BoxedValue = value;
                else
                    overrideable.BoxedValue = null;

                if (hasOverrideSet)
                    overrideable.HasOverride = hasOverride;
            }

            return instance;
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            emitter.Emit(new MappingStart(null, null, false, MappingStyle.Block));

            if (value is IOverrideableSetting setting)
            {
                emitter.Emit(new Scalar(nameof(OverrideableSetting<int>.HasOverride)));
                serializer(setting.HasOverride);

                emitter.Emit(new Scalar(nameof(OverrideableSetting<int>.Value)));
                serializer(setting.BoxedValue);
            }

            emitter.Emit(new MappingEnd());
        }
    }
}
