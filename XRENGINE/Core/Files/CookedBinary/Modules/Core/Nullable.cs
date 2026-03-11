namespace XREngine.Core.Files;

public static partial class CookedBinarySerializer
{
    private sealed class NullableCookedBinaryModule : CookedBinaryModule
    {
        public override CookedBinarySerializationModuleInfo Info => new(500, "Nullable", "Nullable<T> wrappers that serialize their underlying value when present.");

        public override bool TryWrite(CookedBinaryWriter writer, object value, Type runtimeType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
            => TryWriteNullable(writer, value, runtimeType, allowCustom, callbacks);

        public override bool TryRead(CookedBinaryTypeMarker marker, CookedBinaryReader reader, Type? expectedType, CookedBinarySerializationCallbacks? callbacks, out object? value)
        {
            if (marker != CookedBinaryTypeMarker.Nullable)
            {
                value = null;
                return false;
            }

            value = ReadNullable(reader, callbacks);
            return true;
        }

        public override bool TryAddSize(CookedBinarySizeCalculator calculator, object value, Type runtimeType, bool allowCustom)
        {
            Type? underlyingType = Nullable.GetUnderlyingType(runtimeType);
            if (underlyingType is null)
                return false;

            calculator.AddBytes(1);
            calculator.AddBytes(SizeOfTypeName(underlyingType));
            calculator.AddBytes(sizeof(bool));
            calculator.AddValue(value, allowCustom);
            return true;
        }

        public override CookedBinarySchemaNode? TryBuildValueSchema(CookedBinarySchemaBuilder builder, string name, Type? declaredType, object value, Type runtimeType, bool allowCustom)
        {
            Type? underlyingType = Nullable.GetUnderlyingType(runtimeType);
            if (underlyingType is null)
                return null;

            var node = builder.NewNode(name, "value", runtimeType.FullName ?? runtimeType.Name);
            node.Marker = CookedBinaryTypeMarker.Nullable.ToString();
            builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
            builder.AddStringLeaf(node, "underlyingType", underlyingType.AssemblyQualifiedName ?? underlyingType.FullName ?? underlyingType.Name);
            builder.AddFixedLeaf(node, "hasValue", "flag", sizeof(bool), bool.TrueString);
            node.MutableChildren.Add(builder.BuildValueNode("value", underlyingType, value, allowCustom));
            return builder.FinalizeNode(node);
        }

        public override CookedBinarySchemaNode? TryBuildTypeSchema(CookedBinarySchemaBuilder builder, string name, Type type, bool allowCustom)
        {
            Type? underlyingType = Nullable.GetUnderlyingType(type);
            if (underlyingType is null)
                return null;

            var node = builder.NewNode(name, "schema", type.FullName ?? type.Name);
            node.Marker = CookedBinaryTypeMarker.Nullable.ToString();
            builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
            builder.AddStringTemplateLeaf(node, "underlyingType", underlyingType.AssemblyQualifiedName ?? underlyingType.FullName ?? underlyingType.Name);
            builder.AddFixedLeaf(node, "hasValue", "flag", sizeof(bool), "bool");
            node.MutableChildren.Add(builder.BuildTypeNode("value", underlyingType, allowCustom));
            return builder.FinalizeNode(node, allowUnknownChildren: true);
        }
    }
}