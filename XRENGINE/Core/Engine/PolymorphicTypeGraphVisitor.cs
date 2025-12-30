using System;
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
    /// </summary>
    public sealed class PolymorphicTypeGraphVisitor(IObjectGraphVisitor<IEmitter> nextVisitor) : IObjectGraphVisitor<IEmitter>
    {
        public const string TypeKey = "__type";

        private readonly IObjectGraphVisitor<IEmitter> _next = nextVisitor;

        public bool Enter(IObjectDescriptor value, IEmitter context, ObjectSerializer serializer)
            // This YamlDotNet version routes root-object entry through the (key,value) overload,
            // where key is null for the root.
            => _next.Enter(null, value, context, serializer);

        public bool Enter(IPropertyDescriptor? key, IObjectDescriptor value, IEmitter context, ObjectSerializer serializer)
            => _next.Enter(key, value, context, serializer);

        public bool EnterMapping(IObjectDescriptor key, IObjectDescriptor value, IEmitter context, ObjectSerializer serializer)
            => _next.EnterMapping(key, value, context, serializer);

        public bool EnterMapping(IPropertyDescriptor key, IObjectDescriptor value, IEmitter context, ObjectSerializer serializer)
            => _next.EnterMapping(key, value, context, serializer);

        public void VisitScalar(IObjectDescriptor scalar, IEmitter emitter, ObjectSerializer serializer)
            => _next.VisitScalar(scalar, emitter, serializer);

        public void VisitMappingStart(IObjectDescriptor mapping, Type keyType, Type valueType, IEmitter emitter, ObjectSerializer serializer)
        {
            _next.VisitMappingStart(mapping, keyType, valueType, emitter, serializer);

            if (!ShouldEmitType(mapping))
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

        private static bool ShouldEmitType(IObjectDescriptor descriptor)
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

            // Only when the declared type can't be directly instantiated / isn't specific.
            return staticType.IsAbstract || staticType.IsInterface || staticType == typeof(object);
        }
    }
}
