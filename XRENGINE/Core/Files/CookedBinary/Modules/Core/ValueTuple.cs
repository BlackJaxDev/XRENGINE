using System.Globalization;
using System.Runtime.CompilerServices;

namespace XREngine.Core.Files;

public static partial class CookedBinarySerializer
{
    private sealed class ValueTupleCookedBinaryModule : CookedBinaryModule
    {
        public override CookedBinarySerializationModuleInfo Info => new(600, "ValueTuple", "ValueTuple element serialization with per-item type hints.");

        public override bool TryWrite(CookedBinaryWriter writer, object value, Type runtimeType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
            => TryWriteValueTuple(writer, value, runtimeType, allowCustom, callbacks);

        public override bool TryRead(CookedBinaryTypeMarker marker, CookedBinaryReader reader, Type? expectedType, CookedBinarySerializationCallbacks? callbacks, out object? value)
        {
            if (marker != CookedBinaryTypeMarker.ValueTuple)
            {
                value = null;
                return false;
            }

            value = ReadValueTuple(reader, callbacks);
            return true;
        }

        public override bool TryAddSize(CookedBinarySizeCalculator calculator, object value, Type runtimeType, bool allowCustom)
        {
            if (!IsValueTupleType(runtimeType) || value is not ITuple tuple)
                return false;

            Type[] typeArgs = runtimeType.GetGenericArguments();
            calculator.AddBytes(1);
            calculator.AddBytes(sizeof(int));
            for (int i = 0; i < tuple.Length; i++)
            {
                calculator.AddBytes(SizeOfTypeName(typeArgs[i]));
                calculator.AddValue(tuple[i], allowCustom);
            }
            return true;
        }

        public override CookedBinarySchemaNode? TryBuildValueSchema(CookedBinarySchemaBuilder builder, string name, Type? declaredType, object value, Type runtimeType, bool allowCustom)
        {
            if (!IsValueTupleType(runtimeType) || value is not ITuple tuple)
                return null;

            var node = builder.NewNode(name, "value", runtimeType.FullName ?? runtimeType.Name);
            node.Marker = CookedBinaryTypeMarker.ValueTuple.ToString();
            builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
            builder.AddFixedLeaf(node, "count", "count", sizeof(int), tuple.Length.ToString(CultureInfo.InvariantCulture));
            Type[] typeArgs = runtimeType.GetGenericArguments();
            for (int i = 0; i < tuple.Length; i++)
            {
                var item = builder.NewNode($"item{i}", "tupleItem", typeArgs[i].FullName ?? typeArgs[i].Name);
                builder.AddStringLeaf(item, "itemType", typeArgs[i].AssemblyQualifiedName ?? typeArgs[i].FullName ?? typeArgs[i].Name);
                item.MutableChildren.Add(builder.BuildValueNode("itemValue", typeArgs[i], tuple[i], allowCustom));
                node.MutableChildren.Add(builder.FinalizeNode(item));
            }

            return builder.FinalizeNode(node);
        }

        public override CookedBinarySchemaNode? TryBuildTypeSchema(CookedBinarySchemaBuilder builder, string name, Type type, bool allowCustom)
        {
            if (!IsValueTupleType(type))
                return null;

            var node = builder.NewNode(name, "schema", type.FullName ?? type.Name);
            node.Marker = CookedBinaryTypeMarker.ValueTuple.ToString();
            builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
            Type[] typeArgs = type.GetGenericArguments();
            builder.AddFixedLeaf(node, "count", "count", sizeof(int), typeArgs.Length.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < typeArgs.Length; i++)
            {
                var item = builder.NewNode($"item{i}", "tupleItem", typeArgs[i].FullName ?? typeArgs[i].Name);
                builder.AddStringTemplateLeaf(item, "itemType", typeArgs[i].AssemblyQualifiedName ?? typeArgs[i].FullName ?? typeArgs[i].Name);
                item.MutableChildren.Add(builder.BuildTypeNode("itemValue", typeArgs[i], allowCustom));
                node.MutableChildren.Add(builder.FinalizeNode(item, allowUnknownChildren: true));
            }

            return builder.FinalizeNode(node, allowUnknownChildren: true);
        }
    }
}