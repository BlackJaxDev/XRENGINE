using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using XREngine.Core;
using XREngine.Core.Files;
using XREngine.Diagnostics;
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
                if (_depth == 1)
                {
                    YamlDefaultTypeContext.ResetReadState();
                    YamlTransformReferenceContext.ResetReadState();
                }

                return innerDeserializer.Deserialize(reader, expectedType, nestedObjectDeserializer, out value, rootDeserializer);
            }
            catch (YamlException ex)
            {
                string parserEvent = reader.Current?.GetType().Name ?? "<null>";
                string expectedTypeName = expectedType.FullName ?? expectedType.Name;
                throw new YamlException(
                    ex.Start,
                    ex.End,
                    $"{ex.Message} [ExpectedType={expectedTypeName}, ParserEvent={parserEvent}, Depth={_depth}]",
                    ex);
            }
            finally
            {
                _depth--;
            }
        }

        // Property to access the current depth
        public static int CurrentDepth => _depth;
    }

    internal sealed class LegacyEnumYamlNodeDeserializer : INodeDeserializer
    {
        public bool Deserialize(IParser reader, Type expectedType, Func<IParser, Type, object?> nestedObjectDeserializer, out object? value, ObjectDeserializer rootDeserializer)
        {
            Type targetType = Nullable.GetUnderlyingType(expectedType) ?? expectedType;

            if (!targetType.IsEnum || !reader.Accept<Scalar>(out var scalar))
            {
                value = null;
                return false;
            }

            if (targetType == typeof(EDebugShapePopulationMode)
                && string.Equals(scalar.Value, "JobSystem", StringComparison.OrdinalIgnoreCase))
            {
                // Legacy editor preferences may still contain this removed option. It used
                // engine workers, so worker starvation could starve SwapBuffers on the main
                // thread; normalize old assets to the thread-pool Tasks path instead.
                reader.Consume<Scalar>();
                value = EDebugShapePopulationMode.Tasks;
                return true;
            }

            value = null;
            return false;
        }
    }

    public class XRAssetDeserializer : INodeDeserializer
    {
        [ThreadStatic]
        private static int _deserializeDepth;

        public bool Deserialize(IParser reader, Type expectedType, Func<IParser, Type, object?> nestedObjectDeserializer, out object? value, ObjectDeserializer rootDeserializer)
        {
            AssetManager.EnsureYamlAssetRuntimeSupported();

            if (!typeof(XRAsset).IsAssignableFrom(expectedType))
            {
                value = null;
                return false;
            }

            if (reader is PrefixReplayParser replayParser && replayParser.TryConsumeInitialInterceptSuppression())
            {
                value = null;
                return false;
            }

            // Always handle scalar XRAsset references, even during replay.
            // This prevents ScalarNodeDeserializer from attempting invalid string->XRAsset conversions.
            if (reader.Accept<Scalar>(out var scalarPeek))
                return TryHandleScalarXRAsset(reader, expectedType, scalarPeek, out value);

            // Let the normal object deserializer own each file's document root. Nested
            // asset loads can occur while another YAML parse is already active, so parser
            // depth alone is not enough to identify a new document root.
            if (AssetDeserializationContext.ConsumeRootAsset())
            {
                value = null;
                return false;
            }

            _deserializeDepth++;
            try
            {
                // XRAssets should always be represented as mappings. If not, fall back immediately.
                if (!reader.Accept<MappingStart>(out _))
                {
                    value = nestedObjectDeserializer(reader, expectedType);
                    return true;
                }

                // Nested XRAsset - probe the exact { ID: guid } shape without consuming the full node.
                // Replaying an entire subtree breaks YamlDotNet alias resolution because the replay parser
                // does not carry the original parser's anchor context.
                if (TryDeserializeNestedReferenceMapping(reader, expectedType, out value, out var deferredParser, out Type? replayType))
                {
                    return true;
                }

                value = nestedObjectDeserializer(deferredParser!, replayType ?? expectedType);
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
        internal static bool TryHandleScalarXRAsset(IParser reader, Type expectedType, Scalar scalar, out object? value)
        {
            string? scalarValue = scalar.Value;

            // Handle nulls: '~' or 'null'
            if (IsNullScalar(scalar))
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
                if (value is null)
                {
                    AssetDiagnostics.RecordMissingAsset(
                        scalarValue,
                        expectedType.Name,
                        $"XRAssetDeserializer (GUID; file='{AssetDeserializationContext.CurrentFilePath ?? "<unknown>"}')");
                    throw BuildUnresolvedScalarException(scalar, expectedType, scalarValue!,
                        $"GUID '{guid}' could not be resolved to a loaded asset or asset file" +
                        (AssetDeserializationContext.CurrentFilePath is { } cf ? $" (while reading '{cf}')." : "."));
                }
                return true;
            }

            // Best-effort: interpret scalar as a file path to an asset.
            // This avoids ScalarNodeDeserializer attempting an invalid string->XRAsset conversion.
            string candidate = scalarValue.Trim('"', '\'');
            if (TryResolveAssetPathFromScalar(candidate, out var resolvedPath, out var resolution))
            {
                reader.Consume<Scalar>();

                // Record any non-portable form so the editor can surface workspace-portability
                // issues (absolute paths get baked from a different machine layout, or simply
                // any absolute path that should be saved as a relative path on the next save).
                if (resolution != ScalarPathResolution.Relative)
                {
                    string? sourceAssetPath = AssetDeserializationContext.CurrentFilePath;
                    bool rebasedAbsolutePath = resolution == ScalarPathResolution.AbsoluteRebased;
                    AssetDiagnostics.RecordRebasedAsset(
                        candidate,
                        resolvedPath,
                        expectedType.Name,
                        rebasedAbsolutePath
                            ? $"Found current workspace path (file='{sourceAssetPath ?? "<unknown>"}')"
                            : $"Path made portable (file='{sourceAssetPath ?? "<unknown>"}')",
                        rebasedAbsolutePath
                            ? AssetDiagnostics.AssetReferenceRepairKind.FoundCurrentWorkspacePath
                            : AssetDiagnostics.AssetReferenceRepairKind.PathMadePortable,
                        sourceAssetPath);
                }

                // Backing text files (for example XRShader.Source) should stay local to the
                // parent asset deserialize. Loading them through AssetManager would register the
                // raw source file as a top-level asset for the same path, which then fights the
                // owning XRShader in the global path cache.
                if (TryLoadUntrackedTextAsset(resolvedPath, expectedType, out value))
                    return true;

                if (DeferredAssetReferenceContext.TryDeferAssetLoad(resolvedPath, expectedType, out XRAsset? deferredAsset))
                {
                    value = deferredAsset;
                    return true;
                }

                AssetLoadProgressContext.BeginReferencedAssetLoad(resolvedPath, expectedType);
                try
                {
                    value = Engine.Assets.LoadImmediate(resolvedPath, expectedType);
                }
                finally
                {
                    AssetLoadProgressContext.CompleteReferencedAssetLoad(resolvedPath, expectedType);
                }
                if (value is null)
                {
                    AssetDiagnostics.RecordMissingAsset(
                        scalarValue,
                        expectedType.Name,
                        $"XRAssetDeserializer (load failed; resolved='{resolvedPath}', file='{AssetDeserializationContext.CurrentFilePath ?? "<unknown>"}')");
                    throw BuildUnresolvedScalarException(scalar, expectedType, scalarValue!,
                        $"Asset at path '{resolvedPath}' failed to load as {expectedType.FullName}.");
                }
                return true;
            }

            // Unknown scalar format for an XRAsset reference. Throw a descriptive error so the
            // caller (and any strict-nullability enforcement above) gets actionable diagnostics
            // instead of a cryptic "Yaml value is null when target property requires non null values".
            reader.Consume<Scalar>();
            AssetDiagnostics.RecordMissingAsset(
                scalarValue,
                expectedType.Name,
                $"XRAssetDeserializer (unresolvable scalar; file='{AssetDeserializationContext.CurrentFilePath ?? "<unknown>"}')");
            throw BuildUnresolvedScalarException(scalar, expectedType, scalarValue!,
                $"Scalar value '{scalarValue}' is not a valid {expectedType.Name} reference " +
                "(expected null/~, a GUID, or an existing absolute/relative asset path)" +
                (AssetDeserializationContext.CurrentFilePath is { } cf2 ? $" while reading '{cf2}'." : "."));
        }

        private static YamlException BuildUnresolvedScalarException(Scalar scalar, Type expectedType, string scalarValue, string detail)
            => new(
                scalar.Start,
                scalar.End,
                $"Unresolved {expectedType.Name} reference: \"{scalarValue}\". {detail}");

        internal static bool IsNullScalar(Scalar scalar)
            => scalar.Value is null
               || scalar.Value == "~"
               || string.Equals(scalar.Value, "null", StringComparison.OrdinalIgnoreCase)
               || (scalar.Value.Length == 0 && scalar.Style == ScalarStyle.Plain);

        private static bool TryLoadUntrackedTextAsset(string resolvedPath, Type expectedType, out object? value)
        {
            value = null;

            if (!typeof(TextFile).IsAssignableFrom(expectedType))
                return false;

            try
            {
                if (Activator.CreateInstance(expectedType) is not TextFile textFile)
                    return false;

                textFile.OriginalPath = resolvedPath;
                textFile.FilePath = resolvedPath;
                textFile.Name = Path.GetFileNameWithoutExtension(resolvedPath);

                if (!textFile.Load3rdParty(resolvedPath))
                    return false;

                textFile.ClearDirty();
                value = textFile;
                return true;
            }
            catch
            {
                value = null;
                return false;
            }
        }

        private static bool TryResolveAssetPathFromScalar(string scalar, [NotNullWhen(true)] out string? resolvedPath)
            => TryResolveAssetPathFromScalar(scalar, out resolvedPath, out _);

        /// <summary>
        /// Describes how a scalar asset-path string was resolved.
        /// </summary>
        internal enum ScalarPathResolution
        {
            /// <summary>Scalar was a relative path resolved under a known asset root (portable).</summary>
            Relative,
            /// <summary>Scalar was an absolute path and the file existed at that exact location.</summary>
            AbsoluteExisting,
            /// <summary>Scalar was an absolute path baked from a different machine layout and had to be rebased onto a known asset root.</summary>
            AbsoluteRebased,
        }

        private static bool TryResolveAssetPathFromScalar(string scalar, [NotNullWhen(true)] out string? resolvedPath, out ScalarPathResolution resolution)
        {
            resolvedPath = null;
            resolution = ScalarPathResolution.Relative;
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
                        resolution = ScalarPathResolution.AbsoluteExisting;
                        return true;
                    }

                    // Absolute path was baked on a different machine layout (different workspace
                    // root). Try to rebase the tail under current known asset roots.
                    if (TryRebaseAbsoluteAssetPath(scalar, out string? rebased))
                    {
                        resolvedPath = rebased;
                        resolution = ScalarPathResolution.AbsoluteRebased;
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
                        resolution = ScalarPathResolution.Relative;
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
                        resolution = ScalarPathResolution.Relative;
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

        /// <summary>
        /// Attempts to recover from an absolute asset path that was serialized under a different
        /// workspace layout (for example, a clone at a different root) by extracting the tail
        /// after a known asset segment and rebasing it under the current EngineAssetsPath /
        /// GameAssetsPath.
        /// </summary>
        private static bool TryRebaseAbsoluteAssetPath(string absolutePath, [NotNullWhen(true)] out string? rebased)
        {
            rebased = null;

            string normalized = absolutePath.Replace('\\', '/');

            // Ordered by specificity: deeper segments first.
            ReadOnlySpan<string> segments = new[]
            {
                "/Build/CommonAssets/",
                "/CommonAssets/",
                "/Assets/",
            };

            string? engineRoot = string.IsNullOrWhiteSpace(Engine.Assets.EngineAssetsPath)
                ? null
                : Path.GetFullPath(Engine.Assets.EngineAssetsPath);
            string? gameRoot = string.IsNullOrWhiteSpace(Engine.Assets.GameAssetsPath)
                ? null
                : Path.GetFullPath(Engine.Assets.GameAssetsPath);

            foreach (string segment in segments)
            {
                int index = normalized.LastIndexOf(segment, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                    continue;

                string tail = normalized[(index + segment.Length)..].TrimStart('/');
                if (string.IsNullOrWhiteSpace(tail))
                    continue;

                // Engine/CommonAssets segments map to EngineAssetsPath; "/Assets/" maps to GameAssetsPath.
                bool isEngineSegment = segment.Contains("CommonAssets", StringComparison.OrdinalIgnoreCase);

                if (isEngineSegment && engineRoot is not null)
                {
                    string candidate = Path.GetFullPath(Path.Combine(engineRoot, tail));
                    if (File.Exists(candidate))
                    {
                        rebased = candidate;
                        return true;
                    }
                }

                if (!isEngineSegment && gameRoot is not null)
                {
                    string candidate = Path.GetFullPath(Path.Combine(gameRoot, tail));
                    if (File.Exists(candidate))
                    {
                        rebased = candidate;
                        return true;
                    }
                }

                // Cross-try the other root as a fallback (some assets are misclassified).
                string? otherRoot = isEngineSegment ? gameRoot : engineRoot;
                if (otherRoot is not null)
                {
                    string candidate = Path.GetFullPath(Path.Combine(otherRoot, tail));
                    if (File.Exists(candidate))
                    {
                        rebased = candidate;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryDeserializeNestedReferenceMapping(IParser reader,
                                                                 Type expectedType,
                                                                 out object? value,
                                                                 out IParser? deferredParser,
                                                                 out Type? replayType)
        {
            value = null;
            deferredParser = null;
            replayType = null;

            List<ParsingEvent> consumedEvents = [];

            if (!reader.TryConsume<MappingStart>(out var mappingStart))
                return false;

            consumedEvents.Add(mappingStart);

            if (!reader.TryConsume<Scalar>(out var keyScalar))
            {
                deferredParser = new PrefixReplayParser(consumedEvents, reader, suppressInitialIntercept: true);
                return false;
            }

            consumedEvents.Add(keyScalar);

            if ((expectedType.IsAbstract || expectedType.IsInterface)
                && (string.Equals(keyScalar.Value, PolymorphicTypeGraphVisitor.TypeKey, StringComparison.Ordinal)
                    || string.Equals(keyScalar.Value, "__assetType", StringComparison.Ordinal)))
            {
                if (!reader.TryConsume<Scalar>(out var typeScalar))
                {
                    deferredParser = new PrefixReplayParser(consumedEvents, reader, suppressInitialIntercept: true);
                    return false;
                }

                consumedEvents.Add(typeScalar);
                if (!TryResolveNestedAssetDiscriminator(typeScalar.Value, expectedType, out replayType))
                    throw new YamlException(
                        $"XRAsset polymorphic discriminator '{typeScalar.Value}' could not be resolved for '{expectedType.FullName}'.");

                deferredParser = new PrefixReplayParser(consumedEvents, reader, suppressInitialIntercept: true);
                return false;
            }

            if (!string.Equals(keyScalar.Value, "ID", StringComparison.OrdinalIgnoreCase))
            {
                deferredParser = new PrefixReplayParser(consumedEvents, reader, suppressInitialIntercept: true);
                return false;
            }

            if (!reader.TryConsume<Scalar>(out var valueScalar))
            {
                deferredParser = new PrefixReplayParser(consumedEvents, reader, suppressInitialIntercept: true);
                return false;
            }

            consumedEvents.Add(valueScalar);

            if (!Guid.TryParse(valueScalar.Value, out var guid))
            {
                deferredParser = new PrefixReplayParser(consumedEvents, reader, suppressInitialIntercept: true);
                return false;
            }

            if (!reader.TryConsume<MappingEnd>(out _))
            {
                deferredParser = new PrefixReplayParser(consumedEvents, reader, suppressInitialIntercept: true);
                return false;
            }

            value = ResolveExternalReference(guid, expectedType);
            return true;
        }

        private static bool TryResolveNestedAssetDiscriminator(string? typeName, Type expectedType, out Type? concreteType)
        {
            concreteType = null;
            if (string.IsNullOrWhiteSpace(typeName))
                return false;

            string rewrittenTypeName = XRTypeRedirectRegistry.RewriteTypeName(typeName);
            concreteType = AotRuntimeMetadataStore.ResolveType(rewrittenTypeName);
            if (concreteType is null && !XRRuntimeEnvironment.IsAotRuntimeBuild)
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    concreteType = assembly.GetType(rewrittenTypeName, throwOnError: false, ignoreCase: false);
                    if (concreteType is not null)
                        break;
                }
            }

            return concreteType is not null && expectedType.IsAssignableFrom(concreteType);
        }

        private static XRAsset? ResolveExternalReference(Guid guid, Type expectedType)
        {
            // First prefer already-loaded assets.
            if (Engine.Assets.TryGetAssetByID(guid, out var asset) && asset is not null)
                return asset;

            // Otherwise, resolve the backing file via metadata and load it.
            string? referenceAssetPath = AssetDeserializationContext.CurrentFilePath;
            if (!Engine.Assets.TryResolveAssetPathById(guid, referenceAssetPath, out var assetPath) || string.IsNullOrWhiteSpace(assetPath))
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

            if (DeferredAssetReferenceContext.TryDeferAssetLoad(assetPath, loadType, out XRAsset? deferredAsset))
                return deferredAsset;

            AssetLoadProgressContext.BeginReferencedAssetLoad(assetPath, loadType);
            try
            {
                return Engine.Assets.LoadImmediate(assetPath, loadType);
            }
            finally
            {
                AssetLoadProgressContext.CompleteReferencedAssetLoad(assetPath, loadType);
            }
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
            private bool _suppressInitialIntercept;

            public PrefixReplayParser(IReadOnlyList<ParsingEvent> prefixEvents, IParser inner, bool suppressInitialIntercept = false)
            {
                _prefixEvents = prefixEvents;
                _inner = inner;
                _prefixIndex = 0;
                _replayingPrefix = prefixEvents.Count > 0;
                _suppressInitialIntercept = suppressInitialIntercept;
            }

            public bool TryConsumeInitialInterceptSuppression()
            {
                if (!_suppressInitialIntercept)
                    return false;

                _suppressInitialIntercept = false;
                return true;
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
            return !_skipConverter && typeof(XRAsset).IsAssignableFrom(type);
        }

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            AssetManager.EnsureYamlAssetRuntimeSupported();
            throw new NotSupportedException("XRAssetYamlConverter is write-only; reading is handled by XRAssetDeserializer.");
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            AssetManager.EnsureYamlAssetRuntimeSupported();

            if (!_skipConverter && value is XRAsset asset2 && ShouldWriteReference(asset2))
            {
                WriteReference(emitter, asset2);
                return;
            }

            bool previous = _skipConverter;
            _skipConverter = true;
            try
            {
                // Preserve the declared type so polymorphic asset properties emit __type
                // before the inline mapping body when the runtime value is more specific.
                serializer(value, type);
            }
            finally
            {
                _skipConverter = previous;
            }
        }

        private static bool ShouldWriteReference(XRAsset asset)
            => TryWriteAsReference.ShouldWriteReference(asset);

        private static void WriteReference(IEmitter emitter, XRAsset asset)
            => TryWriteAsReference.WriteReference(emitter, asset);
    }

    /// <summary>
    /// Shared reference-emission gate used by <see cref="XRAssetYamlConverter"/> and type-specific
    /// asset converters (e.g. <c>XRTexture2DYamlTypeConverter</c>, <c>XRMeshYamlTypeConverter</c>,
    /// <c>AnimationClipYamlTypeConverter</c>) so an externalized asset always emits a compact
    /// <c>{ID: &lt;guid&gt;}</c> reference instead of its inline body.
    /// </summary>
    public static class TryWriteAsReference
    {
        public static bool ShouldWriteReference(XRAsset asset)
        {
            // CurrentDepth is 0 before the first MappingStart, so depth 0 means root object.
            if (DepthTrackingEventEmitter.CurrentDepth < 1)
                return false;

            // Embedded assets (SourceAsset != self) must serialize inline.
            if (!ReferenceEquals(asset.SourceAsset, asset))
                return false;

            if (string.IsNullOrWhiteSpace(asset.FilePath))
                return false;

            return File.Exists(asset.FilePath);
        }

        public static void WriteReference(IEmitter emitter, XRAsset asset)
        {
            emitter.Emit(new MappingStart(null, null, false, MappingStyle.Block));
            emitter.Emit(new Scalar("ID"));
            emitter.Emit(new Scalar(asset.ID.ToString()));
            emitter.Emit(new MappingEnd());
        }

        public static bool TryEmitReference(IEmitter emitter, XRAsset? asset)
        {
            if (asset is null || !ShouldWriteReference(asset))
                return false;

            WriteReference(emitter, asset);
            return true;
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
