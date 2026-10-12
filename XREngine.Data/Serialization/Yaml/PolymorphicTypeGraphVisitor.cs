using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using XREngine.Core.Files;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.ObjectGraphVisitors;

namespace XREngine
{
    /// <summary>
    /// Emits a lightweight type discriminator for polymorphic values.
    ///
    /// When an object's declared (static) type is abstract/interface (or object) and the runtime type differs,
    /// we write a mapping entry:
    ///   __type: Namespace.ConcreteType
    ///
    /// This keeps YAML readable and enables reliable deserialization of derived types.
    /// Inline assets also receive reference anchors when nested converter calls would
    /// otherwise serialize the same source separately in a standalone collection.
    /// </summary>
    public sealed class PolymorphicTypeGraphVisitor(IObjectGraphVisitor<IEmitter> nextVisitor) : IObjectGraphVisitor<IEmitter>
    {
        public const string TypeKey = "__type";

        private readonly IObjectGraphVisitor<IEmitter> _next = nextVisitor;
        // Converter re-entry can create another visitor, but retains the document emitter.
        private static readonly ConditionalWeakTable<IEmitter, InlineAssetAnchors> InlineAnchorsByEmitter = new();

        private sealed class InlineAssetAnchors
        {
            public Dictionary<object, AnchorName> ByAsset { get; } = new(ReferenceEqualityComparer.Instance);
            public int NextAnchor { get; set; }
        }

        public bool Enter(IObjectDescriptor value, IEmitter context, ObjectSerializer serializer)
        {
            ResetRootSerializationState(context);
            // This YamlDotNet version routes root-object entry through the (key,value) overload,
            // where key is null for the root.
            return _next.Enter(null, value, context, serializer);
        }

        public bool Enter(IPropertyDescriptor? key, IObjectDescriptor value, IEmitter context, ObjectSerializer serializer)
        {
            if (key is null && DepthTrackingEventEmitter.CurrentDepth == 0)
                ResetRootSerializationState(context);

            if (!_next.Enter(key, value, context, serializer))
                return false;

            if (value.Value is not XRAsset asset || TryWriteAsReference.ShouldWriteReference(asset)
                || key is null && DepthTrackingEventEmitter.CurrentDepth == 0)
                return true;

            InlineAssetAnchors state = InlineAnchorsByEmitter.GetValue(context, static _ => new InlineAssetAnchors());
            if (state.ByAsset.TryGetValue(asset, out AnchorName anchor))
            {
                context.Emit(new AnchorAlias(anchor));
                return false;
            }

            state.ByAsset.Add(asset, new AnchorName("xrasset" +
                state.NextAnchor++.ToString(CultureInfo.InvariantCulture)));
            return true;
        }

        public bool EnterMapping(IObjectDescriptor key, IObjectDescriptor value, IEmitter context, ObjectSerializer serializer)
            => _next.EnterMapping(key, value, context, serializer);

        public bool EnterMapping(IPropertyDescriptor key, IObjectDescriptor value, IEmitter context, ObjectSerializer serializer)
            => _next.EnterMapping(key, value, context, serializer);

        public void VisitScalar(IObjectDescriptor scalar, IEmitter emitter, ObjectSerializer serializer)
            => _next.VisitScalar(scalar, emitter, serializer);

        public void VisitMappingStart(IObjectDescriptor mapping, Type keyType, Type valueType, IEmitter emitter, ObjectSerializer serializer)
        {
            InlineAssetAnchors state = InlineAnchorsByEmitter.GetValue(emitter, static _ => new InlineAssetAnchors());
            if (mapping.Value is XRAsset asset && state.ByAsset.TryGetValue(asset, out AnchorName anchor))
                _next.VisitMappingStart(mapping, keyType, valueType, new AssetAnchorEmitter(emitter, anchor,
                    actual => state.ByAsset[asset] = actual), serializer);
            else
                _next.VisitMappingStart(mapping, keyType, valueType, emitter, serializer);

            Type? defaultType = YamlDefaultTypeContext.ConsumeWriteDefaultType();

            if (!ShouldEmitType(mapping, defaultType))
                return;

            string typeName = mapping.Type.FullName ?? mapping.Type.Name;
            emitter.Emit(new Scalar(TypeKey));
            emitter.Emit(new Scalar(typeName));
        }

        public void VisitMappingEnd(IObjectDescriptor mapping, IEmitter emitter, ObjectSerializer serializer)
            => _next.VisitMappingEnd(mapping, emitter, serializer);

        public void VisitSequenceStart(IObjectDescriptor sequence, Type elementType, IEmitter emitter, ObjectSerializer serializer)
            => _next.VisitSequenceStart(sequence, elementType, emitter, serializer);

        public void VisitSequenceEnd(IObjectDescriptor sequence, IEmitter emitter, ObjectSerializer serializer)
            => _next.VisitSequenceEnd(sequence, emitter, serializer);

        private void ResetRootSerializationState(IEmitter emitter)
        {
            YamlDefaultTypeContext.ResetWriteState();
            YamlTransformReferenceContext.ResetWriteState();
            InlineAnchorsByEmitter.Remove(emitter);
        }

        private sealed class AssetAnchorEmitter(IEmitter inner, AnchorName preferredAnchor,
            Action<AnchorName> recordAnchor) : IEmitter
        {
            private bool _anchored;

            public void Emit(ParsingEvent @event)
            {
                if (!_anchored && @event is MappingStart start)
                {
                    _anchored = true;
                    AnchorName anchor = start.Anchor.IsEmpty ? preferredAnchor : start.Anchor;
                    recordAnchor(anchor);
                    inner.Emit(new MappingStart(anchor, start.Tag, start.IsImplicit, start.Style, start.Start, start.End));
                    return;
                }

                inner.Emit(@event);
            }
        }

        private static bool ShouldEmitType(IObjectDescriptor descriptor, Type? defaultType)
        {
            if (descriptor.Value is null)
                return false;

            // Avoid emitting for primitives/scalars.
            if (descriptor.Type.IsPrimitive || descriptor.Type == typeof(string) || descriptor.Type.IsEnum)
                return false;

            Type staticType = descriptor.StaticType ?? descriptor.Type;
            Type runtimeType = descriptor.Type;

            if (runtimeType == staticType)
                return false;

            if (defaultType is not null && runtimeType == defaultType)
                return false;

            // Only when the declared type can't be directly instantiated / isn't specific.
            return staticType.IsAbstract || staticType.IsInterface || staticType == typeof(object);
        }
    }
}
