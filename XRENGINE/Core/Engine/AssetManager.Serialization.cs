using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
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

        private static readonly Lazy<IDeserializer> DeserializerInstance = new(() => new CompatibilityNormalizingYamlDeserializer(CreateDeserializer()));

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
        {
            List<IYamlTypeConverter> converters = XRRuntimeEnvironment.IsAotRuntimeBuild
                ? [.. LoadYamlTypeConvertersFromMetadata()]
                : DiscoverYamlTypeConverters();

            bool hasLayerMaskConverter = false;
            foreach (IYamlTypeConverter converter in converters)
            {
                if (converter.GetType() != typeof(LayerMaskYamlTypeConverter))
                    continue;

                hasLayerMaskConverter = true;
                break;
            }

            if (!hasLayerMaskConverter)
                converters.Add(new LayerMaskYamlTypeConverter());

            return converters;
        }

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
                .WithNodeDeserializer(new ViewportRenderCommandContainerYamlNodeDeserializer(), w => w.OnTop())
                .WithNodeDeserializer(new LayerMaskYamlNodeDeserializer(), w => w.OnTop())
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

        private sealed class CompatibilityNormalizingYamlDeserializer(IDeserializer inner) : IDeserializer
        {
            private readonly IDeserializer _inner = inner;

            public object? Deserialize(string input)
            {
                ResetYamlReadContext();
                return _inner.Deserialize(NormalizeCompatibilityYaml(input));
            }

            public object? Deserialize(TextReader input)
            {
                ResetYamlReadContext();
                return _inner.Deserialize(NormalizeCompatibilityYaml(input.ReadToEnd()));
            }

            public object? Deserialize(string input, Type type)
            {
                ResetYamlReadContext();
                return _inner.Deserialize(NormalizeCompatibilityYaml(input), type);
            }

            public object? Deserialize(TextReader input, Type type)
            {
                ResetYamlReadContext();
                return _inner.Deserialize(NormalizeCompatibilityYaml(input.ReadToEnd()), type);
            }

            public object? Deserialize(YamlDotNet.Core.IParser parser)
            {
                ResetYamlReadContext();
                return _inner.Deserialize(parser);
            }

            public object? Deserialize(YamlDotNet.Core.IParser parser, Type type)
            {
                ResetYamlReadContext();
                return _inner.Deserialize(parser, type);
            }

            public T Deserialize<T>(string input)
            {
                ResetYamlReadContext();
                return _inner.Deserialize<T>(NormalizeCompatibilityYaml(input));
            }

            public T Deserialize<T>(TextReader input)
            {
                ResetYamlReadContext();
                return _inner.Deserialize<T>(NormalizeCompatibilityYaml(input.ReadToEnd()));
            }

            public T Deserialize<T>(YamlDotNet.Core.IParser parser)
            {
                ResetYamlReadContext();
                return _inner.Deserialize<T>(parser);
            }
        }

        private static string NormalizeCompatibilityYaml(string yaml)
        {
            if (string.IsNullOrEmpty(yaml))
                return yaml;

            string newline = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            string[] lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            List<string>? normalizedLines = null;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!TryMatchLegacyLayerMaskHeader(line, out string? indentation, out string? propertyName)
                    || i + 1 >= lines.Length
                    || !TryMatchLegacyLayerMaskValue(lines[i + 1], indentation, out int value)
                    || HasAdditionalIndentedLayerMaskEntries(lines, i + 2, indentation))
                {
                    normalizedLines?.Add(line);
                    continue;
                }

                if (normalizedLines is null)
                {
                    normalizedLines = new List<string>(lines.Length);
                    for (int priorIndex = 0; priorIndex < i; priorIndex++)
                        normalizedLines.Add(lines[priorIndex]);
                }

                normalizedLines.Add($"{indentation}{propertyName}: {value}");
                i++;
            }

            return normalizedLines is null
                ? yaml
                : string.Join(newline, normalizedLines);
        }

        private static bool TryMatchLegacyLayerMaskHeader(string line, out string indentation, out string propertyName)
        {
            indentation = string.Empty;
            propertyName = string.Empty;

            int contentIndex = 0;
            while (contentIndex < line.Length && (line[contentIndex] == ' ' || line[contentIndex] == '\t'))
                contentIndex++;

            string content = line[contentIndex..];
            if (!content.EndsWith(':'))
                return false;

            string candidateName = content[..^1].TrimEnd();
            if (candidateName is not "CullingMask"
                && candidateName is not "OverlapMask"
                && candidateName is not "LayerMask")
            {
                return false;
            }

            indentation = line[..contentIndex];
            propertyName = candidateName;
            return true;
        }

        private static bool TryMatchLegacyLayerMaskValue(string line, string headerIndentation, out int value)
        {
            value = default;

            int contentIndex = 0;
            while (contentIndex < line.Length && (line[contentIndex] == ' ' || line[contentIndex] == '\t'))
                contentIndex++;

            if (contentIndex <= headerIndentation.Length)
                return false;

            string content = line[contentIndex..].Trim();
            if (!content.StartsWith("Value:", StringComparison.Ordinal))
                return false;

            string rawValue = content["Value:".Length..].Trim();
            return int.TryParse(rawValue, out value);
        }

        private static bool HasAdditionalIndentedLayerMaskEntries(string[] lines, int startIndex, string headerIndentation)
        {
            for (int i = startIndex; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                int contentIndex = 0;
                while (contentIndex < line.Length && (line[contentIndex] == ' ' || line[contentIndex] == '\t'))
                    contentIndex++;

                return contentIndex > headerIndentation.Length;
            }

            return false;
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
