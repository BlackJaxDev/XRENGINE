using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using XREngine.Data;
using XREngine.Serialization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NodeDeserializers;

namespace XREngine
{
    public partial class AssetManager
    {
        private static readonly IReadOnlyList<IYamlTypeConverter> RegisteredYamlTypeConverters = DiscoverYamlTypeConverters();

        internal static IReadOnlyList<IYamlTypeConverter> YamlTypeConverters => RegisteredYamlTypeConverters;

        public static readonly ISerializer Serializer = CreateSerializer();

        public static readonly IDeserializer Deserializer = CreateDeserializer();

        private static ISerializer CreateSerializer()
        {
            var builder = new SerializerBuilder()
                //.IgnoreFields()
                .EnablePrivateConstructors() //TODO: probably avoid using this
                .EnsureRoundtrip()
                .WithEmissionPhaseObjectGraphVisitor(args => new PolymorphicTypeGraphVisitor(args.InnerVisitor))
                .WithEventEmitter(nextEmitter => new DepthTrackingEventEmitter(nextEmitter))
                //.WithTypeConverter(new XRAssetYamlConverter())
                .IncludeNonPublicProperties()
                //.WithTagMapping("!Transform", typeof(Transform))
                //.WithTagMapping("!UIBoundableTransform", typeof(UIBoundableTransform))
                //.WithTagMapping("!UITransform", typeof(UITransform))
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitEmptyCollections);

            foreach (var converter in RegisteredYamlTypeConverters)
                builder.WithTypeConverter(converter);

            builder.WithTypeConverter(new XRAssetYamlConverter());

            return builder.Build();
        }

        private static IDeserializer CreateDeserializer()
        {
            var builder = new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .EnablePrivateConstructors()
                .WithEnforceNullability()
                .WithEnforceRequiredMembers()
                .WithDuplicateKeyChecking()
                .WithNodeDeserializer(
                    inner => new DepthTrackingNodeDeserializer(inner),
                    s => s.InsteadOf<ObjectNodeDeserializer>())
                .WithNodeDeserializer(
                    inner => new NotSupportedAnnotatingNodeDeserializer(inner),
                    s => s.InsteadOf<DictionaryNodeDeserializer>())
                //.WithNodeDeserializer(new XRAssetDeserializer(), w => w.OnTop())
                ;

            foreach (var converter in RegisteredYamlTypeConverters)
                builder.WithTypeConverter(converter);

            builder.WithNodeDeserializer(new XRAssetDeserializer(), w => w.OnTop());

            // Must run after XRAssetDeserializer is registered (XRAssetDeserializer ignores non-XRAsset types).
            builder.WithNodeDeserializer(new PolymorphicYamlNodeDeserializer(), w => w.OnTop());

            return builder.Build();
        }

        [RequiresUnreferencedCode("Calls System.Reflection.Assembly.GetTypes()")]
        private static List<IYamlTypeConverter> DiscoverYamlTypeConverters()
        {
            List<IYamlTypeConverter> converters = [];
            HashSet<Type> registeredTypes = [];

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type?[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types ?? [];
                }

                foreach (Type? type in types)
                {
                    if (type is null || type.IsAbstract || type.IsInterface)
                        continue;

                    if (!typeof(IYamlTypeConverter).IsAssignableFrom(type))
                        continue;

                    if (type.GetCustomAttribute<YamlTypeConverterAttribute>() is null)
                        continue;

                    if (!registeredTypes.Add(type))
                        continue;

                    if (Activator.CreateInstance(type) is IYamlTypeConverter instance)
                        converters.Add(instance);
                }
            }

            return converters;
        }
    }
}
