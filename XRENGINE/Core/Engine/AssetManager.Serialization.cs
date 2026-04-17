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
        private static readonly Lazy<IReadOnlyList<IYamlTypeConverter>> RegisteredYamlTypeConverters = new(CreateYamlTypeConverters);

        private static readonly Lazy<ISerializer> SerializerInstance = new(CreateSerializer);

        private static readonly Lazy<IDeserializer> DeserializerInstance = new(CreateDeserializer);

        internal static bool SupportsYamlAssetRuntime => !XRRuntimeEnvironment.IsPublishedBuild;

        internal static IReadOnlyList<IYamlTypeConverter> YamlTypeConverters
        {
            get
            {
                EnsureYamlAssetRuntimeSupported();
                return RegisteredYamlTypeConverters.Value;
            }
        }

        public static ISerializer Serializer
        {
            get
            {
                EnsureYamlAssetRuntimeSupported();
                return SerializerInstance.Value;
            }
        }

        public static IDeserializer Deserializer
        {
            get
            {
                EnsureYamlAssetRuntimeSupported();
                return DeserializerInstance.Value;
            }
        }

        internal static void EnsureYamlAssetRuntimeSupported(string? path = null)
        {
            if (SupportsYamlAssetRuntime)
                return;

            string detail = string.IsNullOrWhiteSpace(path)
                ? "Published runtime does not support YAML asset serialization or deserialization."
                : $"Published runtime does not support YAML asset serialization or deserialization for '{path}'.";

            throw new NotSupportedException($"{detail} Use cooked published content instead.");
        }

        private static IReadOnlyList<IYamlTypeConverter> CreateYamlTypeConverters()
            => XRRuntimeEnvironment.IsAotRuntimeBuild
                ? LoadYamlTypeConvertersFromMetadata()
                : DiscoverYamlTypeConverters();

        private static ISerializer CreateSerializer()
        {
            var builder = new SerializerBuilder()
                //.IgnoreFields()
                .EnablePrivateConstructors() //TODO: probably avoid using this
                .EnsureRoundtrip()
                .WithEmissionPhaseObjectGraphVisitor(args => new PolymorphicTypeGraphVisitor(args.InnerVisitor))
                .WithEventEmitter(nextEmitter => new DepthTrackingEventEmitter(nextEmitter))
                .WithTypeInspector(inner => new DelegateSkippingTypeInspector(inner))
                .WithTypeInspector(inner => new TransformYamlTypeInspector(inner, applyReferenceOnRead: false))
                .WithTypeInspector(inner => new YamlDefaultTypeInspector(inner, applyDefaultTypeOnRead: false))
                //.WithTypeConverter(new XRAssetYamlConverter())
                .IncludeNonPublicProperties()
                //.WithTagMapping("!Transform", typeof(Transform))
                //.WithTagMapping("!UIBoundableTransform", typeof(UIBoundableTransform))
                //.WithTagMapping("!UITransform", typeof(UITransform))
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitEmptyCollections);

            foreach (var converter in RegisteredYamlTypeConverters.Value)
                builder.WithTypeConverter(converter);

            builder.WithTypeConverter(new XRAssetYamlConverter());

            return builder.Build();
        }

        private static IReadOnlyList<IYamlTypeConverter> LoadYamlTypeConvertersFromMetadata()
        {
            AotRuntimeMetadata? metadata = AotRuntimeMetadataStore.Metadata;
            if (metadata?.YamlTypeConverterTypeNames is null || metadata.YamlTypeConverterTypeNames.Length == 0)
                return [];

            List<IYamlTypeConverter> converters = [];
            foreach (string typeName in metadata.YamlTypeConverterTypeNames)
            {
                Type? type = AotRuntimeMetadataStore.ResolveType(typeName);
                if (type is null || !typeof(IYamlTypeConverter).IsAssignableFrom(type))
                    continue;

                if (Activator.CreateInstance(type) is IYamlTypeConverter converter)
                    converters.Add(converter);
            }

            return converters;
        }

        private static IDeserializer CreateDeserializer()
        {
            var builder = new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .EnablePrivateConstructors()
                .WithEnforceNullability()
                .WithEnforceRequiredMembers()
                .WithDuplicateKeyChecking()
                .WithTypeInspector(inner => new TransformYamlTypeInspector(inner, applyReferenceOnRead: true))
                .WithTypeInspector(inner => new YamlDefaultTypeInspector(inner, applyDefaultTypeOnRead: true))
                .WithNodeDeserializer(
                    inner => new DepthTrackingNodeDeserializer(inner),
                    s => s.InsteadOf<ObjectNodeDeserializer>())
                .WithNodeDeserializer(
                    inner => new NotSupportedAnnotatingNodeDeserializer(inner),
                    s => s.InsteadOf<DictionaryNodeDeserializer>())
                //.WithNodeDeserializer(new XRAssetDeserializer(), w => w.OnTop())
                ;

            foreach (var converter in RegisteredYamlTypeConverters.Value)
                if (converter is not IWriteOnlyYamlTypeConverter)
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

            foreach (Assembly assembly in EnumerateYamlTypeConverterAssemblies())
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

        private static IEnumerable<Assembly> EnumerateYamlTypeConverterAssemblies()
        {
            HashSet<Assembly> assemblies = [];

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                assemblies.Add(assembly);

            // Asset payload wrappers depend on DataSource's YAML converter, but the data assembly
            // is not guaranteed to be loaded before the serializer is initialized.
            assemblies.Add(typeof(DataSourceYamlTypeConverter).Assembly);

            return assemblies;
        }
    }
}
