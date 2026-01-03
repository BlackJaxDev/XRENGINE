using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            if (!typeof(XRAsset).IsAssignableFrom(expectedType))
            {
                value = null;
                return false;
            }

            // Don't intercept during replay - let normal deserializer handle it
            if (_isReplaying)
            {
                value = null;
                return false;
            }

            _deserializeDepth++;
            try
            {
                // At root level (depth 1), just deserialize normally
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

                // Nested XRAsset - capture the node to check if it's a GUID reference
                var capturedEvents = CaptureNode(reader);
                if (TryResolveExternalReference(capturedEvents, expectedType, out var referencedAsset))
                {
                    value = referencedAsset;
                    return true;
                }

                // Not a reference - replay events to deserialize inline asset
                var replayParser = new ReplayParser(capturedEvents);
                _isReplaying = true;
                try
                {
                    value = nestedObjectDeserializer(replayParser, expectedType);
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

        private static IReadOnlyList<ParsingEvent> CaptureNode(IParser parser)
        {
            var events = new List<ParsingEvent>();
            CaptureNodeRecursive(parser, events);
            return events;
        }

        private static void CaptureNodeRecursive(IParser parser, ICollection<ParsingEvent> events)
        {
            if (parser.TryConsume<Scalar>(out var scalar))
            {
                events.Add(scalar);
                return;
            }

            if (parser.TryConsume<DocumentStart>(out var documentStart))
            {
                events.Add(documentStart);
                while (true)
                {
                    if (parser.TryConsume<DocumentEnd>(out var documentEnd))
                    {
                        events.Add(documentEnd);
                        break;
                    }

                    CaptureNodeRecursive(parser, events);
                }
                return;
            }

            if (parser.TryConsume<StreamStart>(out var streamStart))
            {
                events.Add(streamStart);
                while (true)
                {
                    if (parser.TryConsume<StreamEnd>(out var streamEnd))
                    {
                        events.Add(streamEnd);
                        break;
                    }

                    CaptureNodeRecursive(parser, events);
                }
                return;
            }

            if (parser.TryConsume<AnchorAlias>(out var anchorAlias))
            {
                events.Add(anchorAlias);
                return;
            }

            if (parser.TryConsume<SequenceStart>(out var sequenceStart))
            {
                events.Add(sequenceStart);
                while (true)
                {
                    if (parser.TryConsume<SequenceEnd>(out var sequenceEnd))
                    {
                        events.Add(sequenceEnd);
                        break;
                    }

                    CaptureNodeRecursive(parser, events);
                }
                return;
            }

            if (parser.TryConsume<MappingStart>(out var mappingStart))
            {
                events.Add(mappingStart);
                while (true)
                {
                    if (parser.TryConsume<MappingEnd>(out var mappingEnd))
                    {
                        events.Add(mappingEnd);
                        break;
                    }

                    CaptureNodeRecursive(parser, events); // Key
                    CaptureNodeRecursive(parser, events); // Value
                }
                return;
            }

            throw new YamlException("Unsupported YAML node encountered while capturing XRAsset data.");
        }

        private static bool TryResolveExternalReference(IReadOnlyList<ParsingEvent> events, Type expectedType, out XRAsset? asset)
        {
            asset = null;
            if (events.Count != 4)
                return false;

            if (events[0] is not MappingStart || events[3] is not MappingEnd)
                return false;

            if (events[1] is not Scalar keyScalar || !string.Equals(keyScalar.Value, "ID", StringComparison.OrdinalIgnoreCase))
                return false;

            if (events[2] is not Scalar valueScalar)
                return false;

            if (!Guid.TryParse(valueScalar.Value, out var guid))
                return false;

            // First prefer already-loaded assets.
            if (Engine.Assets.TryGetAssetByID(guid, out asset) && asset is not null)
                return true;

            // Otherwise, resolve the backing file via metadata and load it.
            if (!Engine.Assets.TryResolveAssetPathById(guid, out var assetPath) || string.IsNullOrWhiteSpace(assetPath))
                return false;

            if (!File.Exists(assetPath))
                return false;

            Type loadType = expectedType;
            if (loadType.IsAbstract || loadType.IsInterface)
            {
                if (TryResolveConcreteAssetType(assetPath, out var concreteType))
                    loadType = concreteType;
                else
                    return false;
            }

            asset = Engine.Assets.LoadImmediate(assetPath, loadType);
            return asset is not null;
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

            // XRAsset.SerializedAssetType writes FullName (no assembly qualifier). Resolve via loaded assemblies.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var resolved = assembly.GetType(hint, throwOnError: false, ignoreCase: false);
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

        private sealed class ReplayParser : IParser
        {
            private readonly Queue<ParsingEvent> _events;
            private ParsingEvent? _current;

            public ReplayParser(IEnumerable<ParsingEvent> events)
            {
                _events = new Queue<ParsingEvent>(events);
                // Position on first event immediately
                MoveNext();
            }

            public ParsingEvent Current => _current ?? throw new InvalidOperationException("The parser is not positioned on an event.");

            public bool MoveNext()
            {
                if (_events.Count == 0)
                {
                    _current = null;
                    return false;
                }

                _current = _events.Dequeue();
                return true;
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
            catch (NotSupportedException ex)
            {
                throw new YamlException(
                    $"NotSupportedException while deserializing '{expectedType.FullName}'. Depth={DepthTrackingNodeDeserializer.CurrentDepth}.",
                    ex);
            }
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
            if (_skipConverter)
                return false;

            return typeof(XRAsset).IsAssignableFrom(type);
        }

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
            => throw new NotSupportedException("XRAssetYamlConverter is write-only; reading is handled by XRAssetDeserializer.");

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
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
                serializer(value);
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
}
