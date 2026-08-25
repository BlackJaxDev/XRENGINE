using System.Reflection;
using XREngine.Data.Core;

namespace XREngine.Core.Files;

public static partial class CookedBinarySerializer
{
    private sealed class XREventCookedBinaryModule : CookedBinaryModule
    {
        public override CookedBinarySerializationModuleInfo Info => new(400, "XREvents", "Persistent-call serialization for XREvent and XREvent<T>.");

        public override bool TryWrite(CookedBinaryWriter writer, object value, Type runtimeType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
        {
            if (value is XREvent xrEvent)
            {
                writer.Write((byte)CookedBinaryTypeMarker.XREvent);
                WriteXREventPersistentCalls(writer, xrEvent.PersistentCalls, allowCustom, callbacks);
                return true;
            }

            return TryWriteGenericXREvent(writer, value, runtimeType, allowCustom, callbacks);
        }

        public override bool TryRead(CookedBinaryTypeMarker marker, CookedBinaryReader reader, Type? expectedType, CookedBinarySerializationCallbacks? callbacks, out object? value)
        {
            if (marker == CookedBinaryTypeMarker.XREvent)
            {
                value = ReadXREvent(reader);
                return true;
            }

            if (marker == CookedBinaryTypeMarker.XREventGeneric)
            {
                value = ReadXREventGeneric(reader);
                return true;
            }

            value = null;
            return false;
        }

        public override bool TryAddSize(CookedBinarySizeCalculator calculator, object value, Type runtimeType, bool allowCustom)
        {
            if (value is XREvent xrEvent)
            {
                calculator.AddBytes(1);
                calculator.AddXRPersistentCallListSize(xrEvent.PersistentCalls);
                return true;
            }

            return calculator.TryAddGenericXREventSize(value);
        }

        public override CookedBinarySchemaNode? TryBuildValueSchema(CookedBinarySchemaBuilder builder, string name, Type? declaredType, object value, Type runtimeType, bool allowCustom)
        {
            if (value is XREvent xrEvent)
            {
                var node = builder.NewNode(name, "value", runtimeType.FullName ?? runtimeType.Name);
                node.Marker = CookedBinaryTypeMarker.XREvent.ToString();
                builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
                builder.AddPersistentCallListNode(node, xrEvent.PersistentCalls);
                return builder.FinalizeNode(node);
            }

            if (!runtimeType.IsGenericType || runtimeType.GetGenericTypeDefinition() != typeof(XREvent<>))
                return null;

            var genericNode = builder.NewNode(name, "value", runtimeType.FullName ?? runtimeType.Name);
            genericNode.Marker = CookedBinaryTypeMarker.XREventGeneric.ToString();
            builder.AddFixedLeaf(genericNode, "marker", "marker", 1, genericNode.Marker);
            Type eventArg = runtimeType.GetGenericArguments()[0];
            builder.AddStringLeaf(genericNode, "genericArgument", eventArg.AssemblyQualifiedName ?? eventArg.FullName ?? eventArg.Name);
            PropertyInfo? prop = runtimeType.GetProperty(nameof(XREvent.PersistentCalls));
            builder.AddPersistentCallListNode(genericNode, prop?.GetValue(value) as List<XRPersistentCall>);
            return builder.FinalizeNode(genericNode);
        }

        public override CookedBinarySchemaNode? TryBuildTypeSchema(CookedBinarySchemaBuilder builder, string name, Type type, bool allowCustom)
        {
            if (type == typeof(XREvent))
            {
                var node = builder.NewNode(name, "schema", type.FullName ?? type.Name);
                node.Marker = CookedBinaryTypeMarker.XREvent.ToString();
                builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
                builder.AddUnknownLeaf(node, "persistentCalls", "sequence", "4 + serialized call entries");
                return builder.FinalizeNode(node, allowUnknownChildren: true);
            }

            if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(XREvent<>))
                return null;

            var genericNode = builder.NewNode(name, "schema", type.FullName ?? type.Name);
            genericNode.Marker = CookedBinaryTypeMarker.XREventGeneric.ToString();
            builder.AddFixedLeaf(genericNode, "marker", "marker", 1, genericNode.Marker);
            Type eventArg = type.GetGenericArguments()[0];
            builder.AddStringTemplateLeaf(genericNode, "genericArgument", eventArg.AssemblyQualifiedName ?? eventArg.FullName ?? eventArg.Name);
            builder.AddUnknownLeaf(genericNode, "persistentCalls", "sequence", "4 + serialized call entries");
            return builder.FinalizeNode(genericNode, allowUnknownChildren: true);
        }
    }
}