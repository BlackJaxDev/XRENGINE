using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using XREngine.Core;
using XREngine.Core.Files;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;

namespace XREngine
{
    //public class XRAssetYamlTypeConverter : IYamlTypeConverter
    //{
    //    public const string ID = "ID";

    //    public bool Accepts(Type type)
    //        => typeof(XRAsset).IsAssignableFrom(type);

    //    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    //    {
    //        parser.Consume<MappingStart>();
    //        parser.Consume<Scalar>();
    //        var id = parser.Consume<Scalar>();
    //        parser.Consume<MappingEnd>();

    //        return Engine.Assets.GetAssetByID(Guid.Parse(id.Value));
    //    }

    //    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    //    {
    //        if (value is not XRAsset source)
    //            return;

    //        emitter.Emit(new MappingStart(null, null, false, MappingStyle.Block));
    //        {
    //            emitter.Emit(new Scalar(ID));
    //            emitter.Emit(new Scalar(source.ID.ToString()));
    //        }
    //        emitter.Emit(new MappingEnd());
    //    }
    //}

    public class DepthTrackingNodeDeserializer(INodeDeserializer innerDeserializer) : INodeDeserializer
    {
        // Thread-static variable to track the depth
        [ThreadStatic]
        private static int _depth;

        public bool Deserialize(IParser reader, Type expectedType, Func<IParser, Type, object?> nestedObjectDeserializer, out object? value, ObjectDeserializer rootDeserializer)
        {
            _depth++;
            try
            {
                return innerDeserializer.Deserialize(reader, expectedType, nestedObjectDeserializer, out value, rootDeserializer);
            }
            finally
            {
                _depth--;
            }
        }

        // Property to access the current depth
        public static int CurrentDepth => _depth;
    }

    public class XRAssetDeserializer : INodeDeserializer
    {
        [ThreadStatic]
        private static int _deserializeDepth;

        [ThreadStatic] 
        private static bool _isReplaying;

        public bool Deserialize(IParser reader, Type expectedType, Func<IParser, Type, object?> nestedObjectDeserializer, out object? value, ObjectDeserializer rootDeserializer)
        {
            AssetManager.EnsureYamlAssetRuntimeSupported();

            if (!typeof(XRAsset).IsAssignableFrom(expectedType))
            {
                value = null;
                return false;
            }

            // Always handle scalar XRAsset references, even during replay.
            // This prevents ScalarNodeDeserializer from attempting invalid string->XRAsset conversions.
            if (reader.Accept<Scalar>(out var scalarPeek))
            {
                return TryHandleScalarXRAsset(reader, expectedType, scalarPeek.Value, out value);
            }

            // Don't intercept mapping/sequence during replay - let normal deserializer handle it
            if (_isReplaying)
            {
                value = null;
                return false;
            }

            _deserializeDepth++;
            try
            {
                // At root level (depth 1), just deserialize normally.
                if (_deserializeDepth <= 1)
                {
                    value = nestedObjectDeserializer(reader, expectedType);
                    return true;
                }

                // XRAssets should always be represented as mappings. If not, fall back immediately.
                if (!reader.Accept<MappingStart>(out _))
                {
                    value = nestedObjectDeserializer(reader, expectedType);
                    return true;
                }

                // Nested XRAsset - probe the exact { ID: guid } shape without consuming the full node.
                // Replaying an entire subtree breaks YamlDotNet alias resolution because the replay parser
                // does not carry the original parser's anchor context.
                if (TryDeserializeNestedReferenceMapping(reader, expectedType, out value, out var deferredParser))
                {
                    return true;
                }

                _isReplaying = true;
                try
                {
                    value = nestedObjectDeserializer(deferredParser!, expectedType);
                }
                finally
                {
                    _isReplaying = false;
                }
                return true;
            }
            finally
            {
                _deserializeDepth--;
            }
        }

        /// <summary>
        /// Handles scalar values for XRAsset types (null, GUID references, or file paths).
        /// </summary>
        private static bool TryHandleScalarXRAsset(IParser reader, Type expectedType, string? scalarValue, out object? value)
        {
            // Handle nulls: '~' or 'null'
            if (scalarValue is null || scalarValue == "~" || string.Equals(scalarValue, "null", StringComparison.OrdinalIgnoreCase))
            {
                reader.Consume<Scalar>();
                value = null;
                return true;
            }

            // Handle GUID references
            if (Guid.TryParse(scalarValue, out var guid))
            {
                reader.Consume<Scalar>();
                value = ResolveExternalReference(guid, expectedType);
                return true;
            }

            // Best-effort: interpret scalar as a file path to an asset.
            // This avoids ScalarNodeDeserializer attempting an invalid string->XRAsset conversion.
            string candidate = scalarValue.Trim('"', '\'');
            if (TryResolveAssetPathFromScalar(candidate, out var resolvedPath))
            {
                reader.Consume<Scalar>();
                value = Engine.Assets.LoadImmediate(resolvedPath, expectedType);
                return true;
            }

            // Unknown scalar format for an XRAsset reference. Consume it and return null.
            // This is safer than falling through to ScalarNodeDeserializer (which will throw).
            reader.Consume<Scalar>();
            value = null;
            return true;
        }

        private static bool TryResolveAssetPathFromScalar(string scalar, [NotNullWhen(true)] out string? resolvedPath)
        {
            resolvedPath = null;
            if (string.IsNullOrWhiteSpace(scalar))
                return false;

            try
            {
                // Absolute path.
                if (Path.IsPathRooted(scalar))
                {
                    string full = Path.GetFullPath(scalar);
                    if (File.Exists(full))
                    {
                        resolvedPath = full;
                        return true;
                    }
                    return false;
                }

                // Relative path under game assets.
                if (!string.IsNullOrWhiteSpace(Engine.Assets.GameAssetsPath))
                {
                    string candidate = Path.GetFullPath(Path.Combine(Engine.Assets.GameAssetsPath, scalar));
                    if (File.Exists(candidate))
                    {
                        resolvedPath = candidate;
                        return true;
                    }
                }

                // Relative path under engine assets.
                if (!string.IsNullOrWhiteSpace(Engine.Assets.EngineAssetsPath))
                {
                    string candidate = Path.GetFullPath(Path.Combine(Engine.Assets.EngineAssetsPath, scalar));
                    if (File.Exists(candidate))
                    {
                        resolvedPath = candidate;
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static bool TryDeserializeNestedReferenceMapping(IParser reader, Type expectedType, out object? value, out IParser? deferredParser)
        {
            value = null;
            deferredParser = null;

            List<ParsingEvent> consumedEvents = [];

            if (!reader.TryConsume<MappingStart>(out var mappingStart))
                return false;

            consumedEvents.Add(mappingStart);

            if (!reader.TryConsume<Scalar>(out var keyScalar))
            {
                deferredParser = new PrefixReplayParser(consumedEvents, reader);
                return false;
            }

            consumedEvents.Add(keyScalar);

            if (!string.Equals(keyScalar.Value, "ID", StringComparison.OrdinalIgnoreCase))
            {
                deferredParser = new PrefixReplayParser(consumedEvents, reader);
                return false;
            }

            if (!reader.TryConsume<Scalar>(out var valueScalar))
            {
                deferredParser = new PrefixReplayParser(consumedEvents, reader);
                return false;
            }

            consumedEvents.Add(valueScalar);

            if (!Guid.TryParse(valueScalar.Value, out var guid))
            {
                deferredParser = new PrefixReplayParser(consumedEvents, reader);
                return false;
            }

            if (!reader.TryConsume<MappingEnd>(out _))
            {
                deferredParser = new PrefixReplayParser(consumedEvents, reader);
                return false;
            }

            value = ResolveExternalReference(guid, expectedType);
            return true;
        }

        private static XRAsset? ResolveExternalReference(Guid guid, Type expectedType)
        {
            // First prefer already-loaded assets.
            if (Engine.Assets.TryGetAssetByID(guid, out var asset) && asset is not null)
                return asset;

            // Otherwise, resolve the backing file via metadata and load it.
            if (!Engine.Assets.TryResolveAssetPathById(guid, out var assetPath) || string.IsNullOrWhiteSpace(assetPath))
                return null;

            if (!File.Exists(assetPath))
                return null;

            Type loadType = expectedType;
            if (loadType.IsAbstract || loadType.IsInterface)
            {
                if (TryResolveConcreteAssetType(assetPath, out var concreteType))
                    loadType = concreteType;
                else
                    return null;
            }

            return Engine.Assets.LoadImmediate(assetPath, loadType);
        }

        private static bool TryResolveConcreteAssetType(string assetPath, out Type type)
        {
            type = typeof(XRAsset);

            string? hint = null;
            try
            {
                foreach (var line in File.ReadLines(assetPath).Take(128))
                {
                    string trimmed = line.Trim();
                    if (!trimmed.StartsWith("__assetType:", StringComparison.Ordinal))
                        continue;

                    hint = trimmed.Substring("__assetType:".Length).Trim();
                    hint = hint.Trim('"', '\'');
                    break;
                }
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(hint))
                return false;

            // Back-compat: allow types to redirect legacy names via [XRTypeRedirect].
            hint = XRTypeRedirectRegistry.RewriteTypeName(hint);

            Type? resolved = AotRuntimeMetadataStore.ResolveType(hint);
            if (resolved is not null && typeof(XRAsset).IsAssignableFrom(resolved))
            {
                type = resolved;
                return true;
            }

            if (XRRuntimeEnvironment.IsAotRuntimeBuild)
                return false;

            // XRAsset.SerializedAssetType writes FullName (no assembly qualifier). Resolve via loaded assemblies.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                resolved = assembly.GetType(hint, throwOnError: false, ignoreCase: false);
                if (resolved is not null && typeof(XRAsset).IsAssignableFrom(resolved))
                {
                    type = resolved;
                    return true;
                }
            }

            // Last-chance attempt.
            var direct = Type.GetType(hint, throwOnError: false);
            if (direct is not null && typeof(XRAsset).IsAssignableFrom(direct))
            {
                type = direct;
                return true;
            }

            return false;
        }

        private sealed class PrefixReplayParser : IParser
        {
            private readonly IReadOnlyList<ParsingEvent> _prefixEvents;
            private readonly IParser _inner;
            private int _prefixIndex;
            private bool _replayingPrefix;

            public PrefixReplayParser(IReadOnlyList<ParsingEvent> prefixEvents, IParser inner)
            {
                _prefixEvents = prefixEvents;
                _inner = inner;
                _prefixIndex = 0;
                _replayingPrefix = prefixEvents.Count > 0;
            }

            public ParsingEvent Current => _replayingPrefix
                ? _prefixEvents[_prefixIndex]
                : _inner.Current ?? throw new InvalidOperationException("The parser is not positioned on an event.");

            public bool MoveNext()
            {
                if (_replayingPrefix)
                {
                    _prefixIndex++;
                    if (_prefixIndex < _prefixEvents.Count)
                        return true;

                    _replayingPrefix = false;
                    return true;
                }

                return _inner.MoveNext();
            }
        }
    }

    public sealed class NotSupportedAnnotatingNodeDeserializer(INodeDeserializer innerDeserializer) : INodeDeserializer
    {
        public bool Deserialize(
            IParser reader,
            Type expectedType,
            Func<IParser, Type, object?> nestedObjectDeserializer,
            out object? value,
            ObjectDeserializer rootDeserializer)
        {
            try
            {
                return innerDeserializer.Deserialize(reader, expectedType, nestedObjectDeserializer, out value, rootDeserializer);
            }
            catch (NotSupportedException)
            {
                throw;
            }
            catch (YamlException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"NotSupportedException while deserializing '{expectedType.FullName}'. Depth={DepthTrackingNodeDeserializer.CurrentDepth}. Exception: {ex.Message}\n{ex.StackTrace}");
                value = null;
            }
            return false;
        }
    }

    public class DepthTrackingEventEmitter(IEventEmitter nextEmitter) : ChainedEventEmitter(nextEmitter)
    {
        // Thread-static variable to track the depth
        [ThreadStatic]
        private static int _depth;

        public override void Emit(AliasEventInfo eventInfo, IEmitter emitter)
        {
            base.Emit(eventInfo, emitter);
        }

        public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
        {
            base.Emit(eventInfo, emitter);
        }

        public override void Emit(MappingStartEventInfo eventInfo, IEmitter emitter)
        {
            _depth++;
            base.Emit(eventInfo, emitter);
        }

        public override void Emit(MappingEndEventInfo eventInfo, IEmitter emitter)
        {
            base.Emit(eventInfo, emitter);
            _depth--;
        }

        public override void Emit(SequenceStartEventInfo eventInfo, IEmitter emitter)
        {
            _depth++;
            base.Emit(eventInfo, emitter);
        }

        public override void Emit(SequenceEndEventInfo eventInfo, IEmitter emitter)
        {
            base.Emit(eventInfo, emitter);
            _depth--;
        }

        // Property to access the current depth
        public static int CurrentDepth => _depth;
    }

    public class XRAssetYamlConverter : IYamlTypeConverter
    {
        [ThreadStatic]
        private static bool _skipConverter;

        public bool Accepts(Type type)
        {
            AssetManager.EnsureYamlAssetRuntimeSupported();

            if (_skipConverter)
                return false;

            return typeof(XRAsset).IsAssignableFrom(type);
        }

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            AssetManager.EnsureYamlAssetRuntimeSupported();
            throw new NotSupportedException("XRAssetYamlConverter is write-only; reading is handled by XRAssetDeserializer.");
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            AssetManager.EnsureYamlAssetRuntimeSupported();

            if (value is XRAsset asset)
            {
                System.Diagnostics.Trace.WriteLine($"[XRAssetYamlConverter] WriteYaml type={type.Name}, depth={DepthTrackingEventEmitter.CurrentDepth}, skipConverter={_skipConverter}, SourceAsset==this:{ReferenceEquals(asset.SourceAsset, asset)}, FilePath={asset.FilePath ?? "null"}");
            }

            if (!_skipConverter && value is XRAsset asset2 && ShouldWriteReference(asset2))
            {
                WriteReference(emitter, asset2);
                return;
            }

            bool previous = _skipConverter;
            _skipConverter = true;
            try
            {
                serializer(value, value?.GetType() ?? type);
            }
            finally
            {
                _skipConverter = previous;
            }
        }

        private static bool ShouldWriteReference(XRAsset asset)
        {
            // CurrentDepth is 0 before the first MappingStart, so depth 0 means root object
            if (DepthTrackingEventEmitter.CurrentDepth < 1)
                return false;

            // Embedded assets should always serialize inline
            if (!ReferenceEquals(asset.SourceAsset, asset))
                return false;

            if (string.IsNullOrWhiteSpace(asset.FilePath))
                return false;

            return File.Exists(asset.FilePath);
        }

        private static void WriteReference(IEmitter emitter, XRAsset asset)
        {
            emitter.Emit(new MappingStart(null, null, false, MappingStyle.Block));
            emitter.Emit(new Scalar("ID"));
            emitter.Emit(new Scalar(asset.ID.ToString()));
            emitter.Emit(new MappingEnd());
        }
    }

    public sealed class DelegateSkippingTypeInspector(ITypeInspector inner) : ITypeInspector
    {
        private readonly ITypeInspector _inner = inner;

        public IEnumerable<IPropertyDescriptor> GetProperties(Type type, object? container)
        {
            foreach (var descriptor in _inner.GetProperties(type, container))
            {
                Type descriptorType = descriptor.TypeOverride ?? descriptor.Type;
                if (typeof(Delegate).IsAssignableFrom(descriptorType))
                    continue;

                yield return descriptor;
            }
        }

        public IPropertyDescriptor GetProperty(Type type, object? container, string name, bool ignoreUnmatched, bool caseInsensitivePropertyMatching)
            => _inner.GetProperty(type, container, name, ignoreUnmatched, caseInsensitivePropertyMatching);

        public string GetEnumName(Type enumType, string name)
            => _inner.GetEnumName(enumType, name);

        public string GetEnumValue(object value)
            => _inner.GetEnumValue(value);
    }
}
